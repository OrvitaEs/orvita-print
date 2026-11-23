<?php
/**
 * Pruebas de POST /pair (F1-07): cuerpo exacto de la respuesta (Doc/04 §5.2),
 * errores contractuales 400/410/429, el sexto intento bloquea el código,
 * throttling por IP (S-02) con Retry-After, el token nunca en orp_logs y el
 * upsert de destinos compartido con el heartbeat.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Tests;

use Orvita\PrintBridge\Domain\DestinationRepository;
use Orvita\PrintBridge\Domain\LocationRepository;
use Orvita\PrintBridge\Domain\PairingCodes;
use Orvita\PrintBridge\Rest\Auth;
use Orvita\PrintBridge\Rest\PairController;
use Orvita\PrintBridge\Support\Settings;

final class PairTest extends \WP_UnitTestCase {

	/** @var int Local de pruebas. */
	private int $location_id;

	/** IP fija para las pruebas del throttling. */
	private const TEST_IP = '203.0.113.77';

	public function set_up(): void {
		parent::set_up();

		global $wpdb;

		Auth::reset();
		$_SERVER['REMOTE_ADDR'] = self::TEST_IP;
		$this->clear_ip_transients();

		$now = \Orvita\PrintBridge\Support\Clock::now_sql();

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->insert(
			$wpdb->prefix . 'orp_locations',
			array(
				'code'       => 'test-pair',
				'name'       => 'Local de emparejamiento',
				'enabled'    => 1,
				'priority'   => 100,
				'rules'      => null,
				'is_default' => 0,
				'created_at' => $now,
				'updated_at' => $now,
			),
			array( '%s', '%s', '%d', '%d', '%s', '%d', '%s', '%s' )
		);
		$this->location_id = (int) $wpdb->insert_id;
	}

	public function tear_down(): void {
		$this->clear_ip_transients();
		Auth::reset();

		parent::tear_down();
	}

	/** Limpia los transients del throttling por IP (y sus timeouts). */
	private function clear_ip_transients(): void {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery, WordPress.DB.PreparedSQL
		$wpdb->query(
			$wpdb->prepare(
				"DELETE FROM {$wpdb->options} WHERE option_name LIKE %s OR option_name LIKE %s",
				$wpdb->esc_like( '_transient_orp_pair_rl_' ) . '%',
				$wpdb->esc_like( '_transient_timeout_orp_pair_rl_' ) . '%'
			)
		);
		wp_cache_flush();
	}

	/** Petición POST /pair como la haría el agente (Doc/04 §5.2). */
	private function pair_request( array $body ): \WP_REST_Response {
		$request = new \WP_REST_Request( 'POST', '/orp-print/v1/pair' );
		foreach ( $body as $key => $value ) {
			$request->set_param( $key, $value );
		}

		return rest_get_server()->dispatch( $request );
	}

	/** Código vigente para el local de pruebas. */
	private function fresh_code(): string {
		$created = PairingCodes::create( $this->location_id, 1 );
		$this->assertIsArray( $created );

		return (string) $created['code'];
	}

	/** Fuerza la caducidad de todos los códigos del local (marca SQL directa). */
	private function expire_all_codes(): void {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->query(
			$wpdb->prepare(
				"UPDATE {$wpdb->prefix}orp_pairing_codes SET expires_at = '2000-01-01 00:00:00' WHERE location_id = %d",
				$this->location_id
			)
		);
	}

	/** Marca todos los códigos del local como usados. */
	private function use_all_codes(): void {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->query(
			$wpdb->prepare(
				"UPDATE {$wpdb->prefix}orp_pairing_codes SET used_at = '2000-01-01 00:00:00' WHERE location_id = %d",
				$this->location_id
			)
		);
	}

	/** 200 con el cuerpo EXACTO del contrato: token, location e intervalo. */
	public function test_pair_success_returns_exact_contract_body(): void {
		$code = $this->fresh_code();

		// El poll_interval sale de orp_settings: se fija un valor propio para
		// demostrar que NO está hardcodeado (Doc/04 §2.7).
		$original_settings = get_option( Settings::OPTION, array() );
		Settings::save( array( 'poll_interval' => 33 ) );

		$response = $this->pair_request(
			array(
				'code'          => $code,
				'agent_name'    => 'TPV Mostrador',
				'platform'      => 'windows',
				'agent_version' => '1.0.0',
				'destinations'  => array(
					array(
						'role'         => 'default',
						'label'        => 'Mostrador',
						'printer_name' => 'EPSON TM-T20III',
						'copies'       => 2,
						'enabled'      => true,
					),
				),
			)
		);

		$this->assertSame( 200, $response->get_status() );

		$data = $response->get_data();

		$this->assertArrayHasKey( 'token', $data );
		$this->assertArrayHasKey( 'location', $data );
		$this->assertArrayHasKey( 'poll_interval', $data );

		$this->assertStringStartsWith( 'orp_', (string) $data['token'] );
		$this->assertSame( $this->location_id, (int) $data['location']['id'] );
		$this->assertSame( 'test-pair', $data['location']['code'] );
		$this->assertSame( 'Local de emparejamiento', $data['location']['name'] );
		$this->assertSame( 33, (int) $data['poll_interval'] );

		// El token funciona: hash + prefijo guardados, autenticación real.
		$agent = Auth::find_by_hash( hash( 'sha256', (string) $data['token'] ) );
		$this->assertNotNull( $agent, 'El token nuevo debe autenticar.' );
		$this->assertSame( $this->location_id, (int) $agent->location_id, 'El agente HEREDA el local del código (Doc/04 §2.2).' );
		$this->assertSame( 'orp_' . substr( (string) $data['token'], 4, 4 ), (string) $agent->token_prefix );
		$this->assertSame( 'active', (string) $agent->status );

		// El código queda usado.
		$row = PairingCodes::find( $code );
		$this->assertNotNull( $row );
		$this->assertNotNull( $row->used_at );

		// Destinos upsertados con la misma lógica del heartbeat.
		$destinations = DestinationRepository::for_location( $this->location_id );
		$this->assertCount( 1, $destinations );
		$this->assertSame( 'default', (string) $destinations[0]->role );
		$this->assertSame( 'EPSON TM-T20III', (string) $destinations[0]->printer_name );
		$this->assertSame( 2, (int) $destinations[0]->copies );
		$this->assertSame( 1, (int) $destinations[0]->enabled );

		// La IP se resetea con el éxito (Doc/07 §3 F1-07).
		$request = new \WP_REST_Request( 'POST', '/orp-print/v1/pair' );
		$this->assertSame( 0, PairController::ip_failures( $request )['failures'] );

		// Restaurar ajustes.
		update_option( Settings::OPTION, $original_settings );
	}

	/** Código inexistente → 400 orp_pairing_invalid (Doc/04 §6). */
	public function test_unknown_code_returns_400(): void {
		$response = $this->pair_request(
			array(
				'code'       => '000001',
				'agent_name' => 'TPV',
			)
		);

		$this->assertSame( 400, $response->get_status() );
		$this->assertSame( 'orp_pairing_invalid', $response->get_data()['code'] );
	}

	/** Código malformado (5 dígitos o con letras) → 400. */
	public function test_malformed_code_returns_400(): void {
		$response = $this->pair_request(
			array(
				'code'       => '12345',
				'agent_name' => 'TPV',
			)
		);
		$this->assertSame( 400, $response->get_status() );
		$this->assertSame( 'orp_pairing_invalid', $response->get_data()['code'] );

		$response = $this->pair_request(
			array(
				'code'       => '12a45b',
				'agent_name' => 'TPV',
			)
		);
		$this->assertSame( 400, $response->get_status() );
		$this->assertSame( 'orp_pairing_invalid', $response->get_data()['code'] );
	}

	/** Código caducado → 410 orp_pairing_expired. */
	public function test_expired_code_returns_410(): void {
		$code = $this->fresh_code();
		$this->expire_all_codes();

		$response = $this->pair_request(
			array(
				'code'       => $code,
				'agent_name' => 'TPV',
			)
		);

		$this->assertSame( 410, $response->get_status() );
		$this->assertSame( 'orp_pairing_expired', $response->get_data()['code'] );
	}

	/** Código ya usado → 400 orp_pairing_invalid. */
	public function test_used_code_returns_400(): void {
		$code = $this->fresh_code();
		$this->use_all_codes();

		$response = $this->pair_request(
			array(
				'code'       => $code,
				'agent_name' => 'TPV',
			)
		);

		$this->assertSame( 400, $response->get_status() );
		$this->assertSame( 'orp_pairing_invalid', $response->get_data()['code'] );
	}

	/**
	 * El sexto intento fallido bloquea el código (Doc/05 §4.6): cinco fallos
	 * con el mismo código válido (falta agent_name) suben attempts a 5 y la
	 * sexta petición recibe 429 orp_pairing_locked.
	 */
	public function test_sixth_attempt_locks_the_code(): void {
		$code = $this->fresh_code();

		for ( $attempt = 1; $attempt <= 5; $attempt++ ) {
			$response = $this->pair_request(
				array(
					'code' => $code,
					// Sin agent_name: fallo de código que sube attempts.
				)
			);

			$this->assertSame( 400, $response->get_status(), "El intento $attempt debe ser 400." );
			$this->assertSame( 'orp_pairing_invalid', $response->get_data()['code'] );
		}

		$row = PairingCodes::find( $code );
		$this->assertSame( 5, (int) $row->attempts, 'El contador se plafona en el máximo.' );

		// Sexto intento: bloqueado con el código contractual del 429.
		$response = $this->pair_request(
			array(
				'code'       => $code,
				'agent_name' => 'TPV Mostrador',
			)
		);

		$this->assertSame( 429, $response->get_status() );
		$this->assertSame( 'orp_pairing_locked', $response->get_data()['code'] );

		// Y NINGÚN agente se creó.
		global $wpdb;
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$total = (int) $wpdb->get_var(
			$wpdb->prepare( "SELECT COUNT(*) FROM {$wpdb->prefix}orp_agents WHERE location_id = %d", $this->location_id )
		);
		$this->assertSame( 0, $total );
	}

	/**
	 * S-02: diez fallos por hora por IP → la siguiente petición (incluso con
	 * un código VÁLIDO) recibe 429 orp_rate_limited con Retry-After.
	 */
	public function test_ip_throttle_blocks_after_ten_failures(): void {
		// Diez fallos con un código inexistente.
		for ( $attempt = 0; $attempt < 10; $attempt++ ) {
			$response = $this->pair_request(
				array(
					'code'       => '99999' . (string) $attempt,
					'agent_name' => 'TPV',
				)
			);
			$this->assertSame( 400, $response->get_status() );
		}

		// La undécima lleva un código VÁLIDO: el throttle por IP manda.
		$code     = $this->fresh_code();
		$response = $this->pair_request(
			array(
				'code'       => $code,
				'agent_name' => 'TPV Mostrador',
			)
		);

		$this->assertSame( 429, $response->get_status() );
		$this->assertSame( 'orp_rate_limited', $response->get_data()['code'] );

		// El mecanismo de la cabecera (rest_post_dispatch, B1) quedó cargado.
		$this->assertNotNull( Auth::$rate_limited_after );
		$this->assertGreaterThan( 0, (int) Auth::$rate_limited_after );
		$this->assertLessThanOrEqual( PairController::IP_WINDOW_SECONDS, (int) Auth::$rate_limited_after );

		// La cabecera sale en la respuesta (la añade el filtro de Auth).
		$with_header = Auth::add_retry_after_header( $response, rest_get_server(), new \WP_REST_Request( 'POST', '/orp-print/v1/pair' ) );
		$headers     = $with_header->get_headers();
		$this->assertArrayHasKey( 'Retry-After', $headers );
		$this->assertGreaterThan( 0, (int) $headers['Retry-After'] );
	}

	/** El éxito resetea el contador de la IP (Doc/07 §3 F1-07). */
	public function test_success_resets_the_ip_counter(): void {
		// Dos fallos.
		for ( $attempt = 0; $attempt < 2; $attempt++ ) {
			$this->pair_request(
				array(
					'code'       => '88888' . (string) $attempt,
					'agent_name' => 'TPV',
				)
			);
		}

		$request = new \WP_REST_Request( 'POST', '/orp-print/v1/pair' );
		$this->assertSame( 2, PairController::ip_failures( $request )['failures'] );

		// Éxito.
		$code = $this->fresh_code();
		$this->pair_request(
			array(
				'code'       => $code,
				'agent_name' => 'TPV Mostrador',
			)
		);

		$this->assertSame( 0, PairController::ip_failures( $request )['failures'], 'El éxito limpia la ventana de la IP.' );
	}

	/** El token NUNCA aparece en orp_logs (Doc/05 §4.6). */
	public function test_token_never_reaches_the_logs(): void {
		$code = $this->fresh_code();

		$response = $this->pair_request(
			array(
				'code'          => $code,
				'agent_name'    => 'TPV Mostrador',
				'platform'      => 'windows',
				'agent_version' => '1.0.0',
			)
		);

		$this->assertSame( 200, $response->get_status() );
		$token = (string) $response->get_data()['token'];

		global $wpdb;
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$logs = (array) $wpdb->get_results( "SELECT message, data FROM {$wpdb->prefix}orp_logs" );
		$this->assertNotEmpty( $logs, 'Debe existir el log del emparejamiento.' );

		foreach ( $logs as $log ) {
			$this->assertStringNotContainsString( $token, (string) $log->message, 'El token no puede salir en los mensajes.' );
			$this->assertStringNotContainsString( $token, (string) $log->data, 'El token no puede salir en los datos del log.' );
		}

		// El log sí identifica el evento sin el secreto.
		$pairing_logs = array_filter(
			$logs,
			static fn( $log ) => str_contains( (string) $log->message, 'emparejado' )
		);
		$this->assertNotEmpty( $pairing_logs, 'El evento de emparejamiento se registra.' );
	}

	/** El upsert de destinos acota copias (99) y descarta salidas incompletas. */
	public function test_destinations_are_sanitized_like_the_heartbeat(): void {		$code = $this->fresh_code();

		$response = $this->pair_request(
			array(
				'code'       => $code,
				'agent_name' => 'TPV',
				'destinations' => array(
					array(
						'role'         => 'default',
						'label'        => 'Mostrador',
						'printer_name' => 'EPSON TM-T20III',
						'copies'       => 200,
						'enabled'      => true,
					),
					array( 'role' => 'sin-impresora' ),
					'no-es-array',
				),
			)
		);

		$this->assertSame( 200, $response->get_status() );

		$destinations = DestinationRepository::for_location( $this->location_id );
		$this->assertCount( 1, $destinations, 'Solo la salida completa se guarda.' );
		$this->assertSame( 99, (int) $destinations[0]->copies, 'Las copias se acotan a 1-99.' );
	}

	/**
	 * F1-13: upsert_many con destinos inválidos — se descartan las que no
	 * llegan (no array, sin rol o sin impresora), las copias absurdas se
	 * acotan a 1–99 y el rol se normaliza con sanitize_key (minúsculas y
	 * guiones). Nada inválido toca la tabla.
	 */
	public function test_upsert_many_rejects_invalid_destinations(): void {
		global $wpdb;

		$agents_before = (int) $wpdb->get_var(
			$wpdb->prepare( "SELECT COUNT(*) FROM {$wpdb->prefix}orp_destinations WHERE location_id = %d", $this->location_id )
		);

		$result = DestinationRepository::upsert_many(
			$this->location_id,
			array(
				'no-es-array',                                                        // rechazado.
				array( 'role' => 'sin-impresora' ),                                   // sin printer_name → rechazado.
				array( 'printer_name' => 'EPSON' ),                                   // sin role → rechazado.
				array( 'role' => 'Cajon-Rapido', 'printer_name' => 'XPRINTER', 'copies' => -5, 'enabled' => true ), // rol normalizado, copias → 1.
				array( 'role' => 'ok', 'printer_name' => 'EPSON', 'copies' => 1000 ), // copias → 99.
			)
		);

		$this->assertSame( 2, $result['upserted'], 'Solo las dos salidas completas se guardan.' );
		$this->assertSame( 3, $result['rejected'], 'Las tres inválidas se descartan.' );

		$destinations = DestinationRepository::for_location( $this->location_id );
		$this->assertCount( $agents_before + 2, $destinations );

		$by_role = array();
		foreach ( $destinations as $destination ) {
			$by_role[ (string) $destination->role ] = $destination;
		}

		$this->assertArrayHasKey( 'cajon-rapido', $by_role, 'El rol se normaliza a minúsculas (sanitize_key).' );
		$this->assertSame( 1, (int) $by_role['cajon-rapido']->copies, 'Copias negativas acotadas a 1.' );
		$this->assertSame( 'XPRINTER', (string) $by_role['cajon-rapido']->printer_name, 'El nombre de impresora se conserva tal cual.' );
		$this->assertSame( 99, (int) $by_role['ok']->copies, 'Copias 1000 acotadas a 99.' );
	}

	/** Solo el primer emparejamiento consume el código (mark_used atómico). */
	public function test_mark_used_is_atomic(): void {
		$code  = $this->fresh_code();
		$row   = PairingCodes::find( $code );
		$now   = \Orvita\PrintBridge\Support\Clock::now_sql();

		$this->assertTrue( PairingCodes::mark_used( (int) $row->id, $now ) );
		$this->assertFalse( PairingCodes::mark_used( (int) $row->id, $now ), 'El segundo no puede volver a marcar el código.' );
	}

	/** El código vigente del panel es el más reciente sin usar ni caducar. */
	public function test_latest_valid_code_for_the_panel(): void {
		$this->assertNull( PairingCodes::latest_valid( $this->location_id ) );

		$first  = PairingCodes::create( $this->location_id, 1 );
		$second = PairingCodes::create( $this->location_id, 1 );

		$this->assertNotNull( $first );
		$this->assertNotNull( $second );
		$this->assertSame( (string) $second['code'], (string) PairingCodes::latest_valid( $this->location_id )->code );
	}
}
