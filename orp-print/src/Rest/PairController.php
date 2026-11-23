<?php
/**
 * POST /pair — emparejamiento del agente (F1-07, Doc/04 §5.2).
 *
 * Única ruta junto a /health SIN token. Seguridad (Doc/03 §8, S-02):
 * - código de 6 dígitos, vida de 10 min, un solo uso, máximo 5 intentos;
 * - throttling por IP: 10 fallos/hora → 429 orp_rate_limited con Retry-After
 *   (contador en transient, misma técnica que el límite de peticiones por
 *   token; Doc/06 §11);
 * - el token se muestra UNA vez: en BD solo SHA-256 + prefijo de 8 caracteres
 *   y NUNCA aparece en orp_logs (Doc/05 §4.6).
 *
 * Orden de comprobaciones de un fallo de código (Doc/07 §3 F1-07):
 * no existe/malformado → 400 orp_pairing_invalid · caducado → 410
 * orp_pairing_expired · 5 intentos → 429 orp_pairing_locked · usado → 400.
 * Los intentos del CÓDIGO suben solo cuando el código existe (fallo de
 * código); los fallos de la IP, en cada respuesta no correcta.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Rest;

use Orvita\PrintBridge\Domain\DestinationRepository;
use Orvita\PrintBridge\Domain\LocationRepository;
use Orvita\PrintBridge\Domain\PairingCodes;
use Orvita\PrintBridge\Support\Clock;
use Orvita\PrintBridge\Support\Logger;
use Orvita\PrintBridge\Support\Settings;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class PairController {

	/** Fallos por IP antes del 429 en una ventana de una hora (S-02, Doc/03 §8). */
	public const IP_FAILURES_LIMIT = 10;

	/** Ventana del contador por IP en segundos. */
	public const IP_WINDOW_SECONDS = HOUR_IN_SECONDS;

	/** Registra la ruta pública. */
	public static function register(): void {
		register_rest_route(
			Router::API_NAMESPACE,
			'/pair',
			array(
				'methods'             => 'POST',
				'callback'            => array( self::class, 'handle' ),
				'permission_callback' => '__return_true',
			)
		);
	}

	/** Maneja POST /pair (Doc/04 §5.2). */
	public static function handle( \WP_REST_Request $request ) {
		// 1. Throttling por IP (S-02): con la ventana agotada, ni se mira el cuerpo.
		$ip = self::ip_failures( $request );
		if ( $ip['failures'] >= self::IP_FAILURES_LIMIT ) {
			return self::rate_limited( $ip['retry_after'] );
		}

		// 2. Cuerpo: código y nombre del agente (platform/agent_version son
		// informativos y destinations es opcional: llegará por heartbeat).
		$code = trim( (string) $request->get_param( 'code' ) );
		$name = sanitize_text_field( (string) $request->get_param( 'agent_name' ) );

		if ( ! preg_match( '/^\d{6}$/', $code ) ) {
			return self::fail_ip( $request, self::invalid( __( 'Falta el código de emparejamiento o no tiene 6 dígitos.', 'orp-print' ) ) );
		}

		$row = PairingCodes::find( $code );

		if ( null === $row ) {
			return self::fail_ip( $request, self::invalid( __( 'Código desconocido.', 'orp-print' ) ) );
		}

		// A partir de aquí el código existe: los fallos cuentan para él también.
		$now = Clock::now_sql();

		if ( (string) $row->expires_at <= $now ) {
			// «Y marcarlo»: el esquema congelado (Doc/04 §2.3) no tiene columna
			// de estado y used_at significa éxito; la marca es el registro en
			// orp_logs. El código caducado devuelve 410 para siempre.
			PairingCodes::bump_attempts( (int) $row->id );
			Logger::log(
				'warning',
				'pairing',
				'Intento de emparejamiento con un código caducado.',
				null,
				null,
				array(
					'location_id' => (int) $row->location_id,
					'attempts'    => (int) $row->attempts + 1,
				)
			);

			return self::fail_ip(
				$request,
				new \WP_Error(
					'orp_pairing_expired',
					__( 'El código de emparejamiento ha caducado. Genera otro en el panel (Locales y reglas).', 'orp-print' ),
					array( 'status' => 410 )
				)
			);
		}

		if ( (int) $row->attempts >= PairingCodes::MAX_ATTEMPTS ) {
			return self::fail_ip(
				$request,
				new \WP_Error(
					'orp_pairing_locked',
					__( 'Demasiados intentos con este código. Espera 15 minutos o genera un código nuevo.', 'orp-print' ),
					array( 'status' => 429 )
				)
			);
		}

		if ( null !== $row->used_at ) {
			PairingCodes::bump_attempts( (int) $row->id );

			return self::fail_ip( $request, self::invalid( __( 'Este código ya se usó: un código empareja un solo agente.', 'orp-print' ) ) );
		}

		if ( '' === $name ) {
			PairingCodes::bump_attempts( (int) $row->id );

			return self::fail_ip( $request, self::invalid( __( 'Falta el nombre del agente (agent_name).', 'orp-print' ) ) );
		}

		// El agente hereda el local del código (Doc/04 §2.2): debe seguir vivo.
		$location = LocationRepository::get( (int) $row->location_id );

		if ( null === $location || 1 !== (int) $location['enabled'] ) {
			PairingCodes::bump_attempts( (int) $row->id );
			Logger::log(
				'error',
				'pairing',
				'Intento de emparejamiento con el local del código borrado o desactivado.',
				null,
				null,
				array( 'location_id' => (int) $row->location_id )
			);

			return self::fail_ip( $request, self::invalid( __( 'El local del código ya no existe o está desactivado.', 'orp-print' ) ) );
		}

		// 3. Consumir el código de forma atómica: dos /pair simultáneos con el
		// mismo código no pueden crear dos agentes (patrón claim de Doc/04 §4).
		if ( ! PairingCodes::mark_used( (int) $row->id, $now ) ) {
			// Perdió la carrera: relee y responde con el motivo real.
			$fresh = PairingCodes::find( $code );
			if ( null !== $fresh && null !== $fresh->used_at ) {
				return self::fail_ip( $request, self::invalid( __( 'Este código ya se usó: un código empareja un solo agente.', 'orp-print' ) ) );
			}

			return self::fail_ip( $request, self::invalid( __( 'El código ya no es válido. Genera otro en el panel.', 'orp-print' ) ) );
		}

		// 4. Agente nuevo con token de un solo uso: hash + prefijo en BD; el
		// token en claro SOLO en esta respuesta (Doc/04 §5.2).
		$token         = Auth::generate_token();
		$agent_id      = self::insert_agent(
			(int) $location['id'],
			$name,
			$token,
			sanitize_text_field( (string) $request->get_param( 'platform' ) ),
			sanitize_text_field( (string) $request->get_param( 'agent_version' ) )
		);

		if ( null === $agent_id ) {
			// Sin agente creado, el código quemado dejaría al local sin par: lo
			// registramos; el administrador genera otro código. Es la única
			// vía para llegar aquí (fallo de BD).
			Logger::log( 'error', 'pairing', 'El código se consumió pero falló la creación del agente.', null, null, array( 'location_id' => (int) $location['id'] ) );

			return self::error( 'orp_pairing_invalid', __( 'No se pudo completar el emparejamiento. Genera un código nuevo e inténtalo de nuevo.', 'orp-print' ), 400 );
		}

		// 5. Salidas del cuerpo con la MISMA lógica del heartbeat (Doc/04 §5.3):
		// upsert por (location_id, role). Los destinos no tocan orp_config_version.
		$destinations = $request->get_param( 'destinations' );
		$upsert_many  = DestinationRepository::upsert_many(
			(int) $location['id'],
			is_array( $destinations ) ? $destinations : array()
		);

		// Un descarte deja rastro (hallazgo del E2E): sin este log, una salida
		// rechazada en el emparejamiento no aparecería en ningún sitio.
		if ( $upsert_many['rejected'] > 0 ) {
			Logger::log(
				'warning',
				'pairing',
				'Salidas rechazadas en el emparejamiento.',
				null,
				$agent_id,
				array( 'rechazadas' => $upsert_many['rejected_items'] )
			);
		}

		// 6. Éxito: la IP vuelve a cero.
		self::reset_ip_failures( $request );

		// Log SIN el token (Doc/05 §4.6): nombre, local y prefijo localizador.
		Logger::log(
			'info',
			'agents',
			'Agente emparejado. El token solo se muestra en la respuesta.',
			null,
			$agent_id,
			array(
				'agent_name'   => $name,
				'location_id'  => (int) $location['id'],
				'token_prefix' => $token['prefix'],
			)
		);

		return new \WP_REST_Response(
			array(
				'token'         => $token['token'],
				'location'      => array(
					'id'   => (int) $location['id'],
					'code' => (string) $location['code'],
					'name' => (string) $location['name'],
				),
				'poll_interval' => (int) Settings::get( 'poll_interval', 10 ),
			),
			200
		);
	}

	/* ---------------------------------------------------------------------
	 * Throttling por IP (S-02).
	 * ------------------------------------------------------------------- */

	/** Clave de transient para la IP (hasheado: los IPv6 no van en nombres de opción). */
	private static function ip_key( \WP_REST_Request $request ): string {
		return 'orp_pair_rl_' . md5( self::client_ip( $request ) );
	}

	/** IP del cliente (REMOTE_ADDR saneado; sin proxies en V1). */
	private static function client_ip( \WP_REST_Request $request ): string {
		unset( $request );

		$ip = isset( $_SERVER['REMOTE_ADDR'] ) ? (string) wp_unslash( $_SERVER['REMOTE_ADDR'] ) : '';

		return (string) preg_replace( '/[^0-9a-fA-F:.]/', '', $ip );
	}

	/**
	 * Estado del contador de fallos por IP: cuántos lleva y cuánto queda de
	 * ventana (para el Retry-After del 429).
	 *
	 * @return array{failures: int, retry_after: int}
	 */
	public static function ip_failures( \WP_REST_Request $request ): array {
		$key    = self::ip_key( $request );
		$count  = get_transient( $key );
		$failures = false === $count ? 0 : (int) $count;

		return array(
			'failures'    => $failures,
			'retry_after' => self::transient_ttl( $key ),
		);
	}

	/** Registra un fallo de la IP y devuelve el WP_Error que corresponde propagar. */
	private static function fail_ip( \WP_REST_Request $request, \WP_Error $error ) {
		$key     = self::ip_key( $request );
		$current = self::ip_failures( $request );
		set_transient( $key, $current['failures'] + 1, self::IP_WINDOW_SECONDS );

		return $error;
	}

	/** Éxito: la IP empieza de cero (Doc/07 §3 F1-07). */
	private static function reset_ip_failures( \WP_REST_Request $request ): void {
		delete_transient( self::ip_key( $request ) );
	}

	/** Segundos que le quedan a la ventana actual del transient (técnica de Auth). */
	private static function transient_ttl( string $key ): int {
		$timeout = get_option( '_transient_timeout_' . $key );

		return $timeout ? max( 1, (int) $timeout - time() ) : self::IP_WINDOW_SECONDS;
	}

	/**
	 * Error 429 orp_rate_limited con Retry-After: reutiliza el mecanismo de
	 * cabeceras de Auth (rest_post_dispatch, B1 de F0).
	 */
	private static function rate_limited( int $retry_after ): \WP_Error {
		// La cabecera la añade Auth::add_retry_after_header() en rest_post_dispatch.
		Auth::$rate_limited_after = max( 1, $retry_after );

		return self::error(
			'orp_rate_limited',
			sprintf(
				/* translators: 1: número de fallos permitidos, 2: minutos de espera. */
				__( 'Demasiados intentos de emparejamiento desde esta IP (%1$d fallos en una hora). Espera unos %2$d minutos antes de reintentar.', 'orp-print' ),
				self::IP_FAILURES_LIMIT,
				(int) ceil( max( 1, $retry_after ) / 60 )
			),
			429
		);
	}

	/* ---------------------------------------------------------------------
	 * Internos.
	 * ------------------------------------------------------------------- */

	/** Inserta el agente con el token dado; devuelve su id o null si falla. */
	private static function insert_agent( int $location_id, string $name, array $token, string $platform, string $agent_version ): ?int {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$affected = $wpdb->insert(
			$wpdb->prefix . 'orp_agents',
			array(
				'location_id'  => $location_id,
				'name'         => mb_substr( $name, 0, 120 ),
				'token_hash'   => $token['hash'],
				'token_prefix' => $token['prefix'],
				'status'       => 'active',
				'agent_version' => '' !== $agent_version ? mb_substr( $agent_version, 0, 32 ) : null,
				'platform'     => '' !== $platform ? mb_substr( $platform, 0, 64 ) : null,
				'created_at'   => Clock::now_sql(),
			),
			array( '%d', '%s', '%s', '%s', '%s', '%s', '%s', '%s' )
		);

		if ( false === $affected || 0 === (int) $affected ) {
			return null;
		}

		return (int) $wpdb->insert_id;
	}

	/** Error 400 orp_pairing_invalid con el mensaje dado. */
	private static function invalid( string $message ): \WP_Error {
		return self::error( 'orp_pairing_invalid', $message, 400 );
	}

	/**
	 * Error REST con el código del contrato (Doc/04 §6) y su estado HTTP.
	 *
	 * @param array<string, mixed> $extra Datos adicionales para el cuerpo.
	 */
	private static function error( string $code, string $message, int $status, array $extra = array() ): \WP_Error {
		return new \WP_Error( $code, $message, array_merge( array( 'status' => $status ), $extra ) );
	}
}
