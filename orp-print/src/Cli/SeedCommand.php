<?php
/**
 * `wp orp seed` — deja el entorno de F0 listo en un comando (Doc/06 §7, F0-08):
 * 3 locales, 1 agente con token conocido (el token se imprime una sola vez),
 * 1 salida y 1 trabajo de prueba con documento neutro.
 *
 * POST /pair no existe todavía (llega en F1-07): este comando es la única
 * fuente de tokens en F0.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Cli;

use Orvita\PrintBridge\Domain\DestinationRepository;
use Orvita\PrintBridge\Domain\DocumentBuilder;
use Orvita\PrintBridge\Domain\JobFactory;
use Orvita\PrintBridge\Rest\Auth;
use Orvita\PrintBridge\Support\Clock;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class SeedCommand {

	/** Códigos de los locales sembrados. */
	private const LOCATION_CODES = array( 'local-centro', 'local-norte', 'local-sur' );

	/** Nombre del agente de pruebas. */
	private const AGENT_NAME = 'Agente de pruebas (Mostrador)';

	/**
	 * Ejecuta el comando.
	 *
	 * ## OPTIONS
	 *
	 * [--reset]
	 * : Borra los datos sembrados antes de volver a crearlos.
	 *
	 * ## EXAMPLES
	 *
	 *     wp orp seed
	 *     wp orp seed --reset
	 *
	 * @param array<string, mixed>       $assoc_args Argumentos con --.
	 * @param array<int, string>         $args       Argumentos posicionales.
	 */
	public static function run( array $args, array $assoc_args = array() ): void {
		unset( $args );

		if ( ! empty( $assoc_args['reset'] ) ) {
			self::reset();
			\WP_CLI::log( __( 'Datos de siembra anteriores eliminados.', 'orp-print' ) );
		}

		if ( null !== self::find_agent() ) {
			\WP_CLI::warning( __( 'La siembra ya existe. Usa «wp orp seed --reset» para regenerarla (el token anterior quedará revocado al borrar el agente).', 'orp-print' ) );
			return;
		}

		$locations = self::seed_locations();

		// El agente se empareja con el primer local (el predeterminado).
		$location = $locations[0];

		// Salida asociada al local: el simulador la reportará igual en su heartbeat.
		DestinationRepository::upsert(
			(int) $location['id'],
			array(
				'role'         => 'default',
				'label'        => 'Mostrador',
				'printer_name' => 'Simuladora Virtual',
				'copies'       => 1,
				'enabled'      => true,
			)
		);

		// Agente con token nuevo: solo se guarda el SHA-256 + prefijo de 8 caracteres.
		$token = Auth::generate_token();
		self::insert_agent( (int) $location['id'], $token );

		// Un trabajo de prueba con documento neutro (order_id 0 = demo).
		$job_id = JobFactory::create(
			(int) $location['id'],
			0,
			'default',
			1,
			DocumentBuilder::build_demo(
				array(
					'order_id'      => 0,
					'location_code' => (string) $location['code'],
					'copies'        => 1,
					'printer_role'  => 'default',
				)
			),
			'seed'
		);

		\WP_CLI::success( __( 'Entorno listo: 3 locales, 1 salida, 1 agente y 1 trabajo de prueba.', 'orp-print' ) );
		/* translators: %s: códigos de los locales separados por comas. */
		\WP_CLI::log( sprintf( __( 'Locales: %s', 'orp-print' ), implode( ', ', array_column( $locations, 'code' ) ) ) );
		/* translators: 1: nombre del agente, 2: código del local. */
		\WP_CLI::log( sprintf( __( 'Agente: %1$s (local %2$s)', 'orp-print' ), self::AGENT_NAME, $location['code'] ) );
		/* translators: %s: id del trabajo o «ya existía (idempotencia)». */
		\WP_CLI::log( sprintf( __( 'Trabajo de prueba: %s', 'orp-print' ), $job_id ? '#' . $job_id : __( 'ya existía (idempotencia)', 'orp-print' ) ) );
		\WP_CLI::log( '' );

		\WP_CLI::log( \WP_CLI::colorize( '%Y' . __( 'ATENCIÓN: el token del agente solo se muestra una vez. Cópialo ahora:', 'orp-print' ) . '%n' ) );
		\WP_CLI::log( \WP_CLI::colorize( '%G' . $token['token'] . '%n' ) );
		\WP_CLI::log( '' );
		\WP_CLI::log( __( 'Prueba el contrato con el simulador:', 'orp-print' ) );
		/* translators: %s: token del agente (no se traduce la sintaxis del comando). */
		\WP_CLI::log( sprintf( __( 'php tools/simulator.php --url=<URL-del-sitio> --token=%s --once', 'orp-print' ), $token['token'] ) );
	}

	/* ---------------------------------------------------------------------
	 * Siembra.
	 * ------------------------------------------------------------------- */

	/** Crea los 3 locales y devuelve [id, code] de cada uno. */
	private static function seed_locations(): array {
		global $wpdb;

		$table   = $wpdb->prefix . 'orp_locations';
		$now     = Clock::now_sql();
		$created = array();

		$definitions = array(
			array(
				'code'      => 'local-centro',
				'name'      => 'Pizzería Centro',
				'priority'  => 10,
				'is_default' => 1,
				'rules'     => wp_json_encode( array(
					array(
						'type'     => 'city',
						'operator' => 'in',
						'values'   => array( 'Madrid' ),
					),
				) ),
			),
			array(
				'code'      => 'local-norte',
				'name'      => 'Pizzería Norte',
				'priority'  => 20,
				'is_default' => 0,
				'rules'     => wp_json_encode( array(
					array(
						'type'     => 'city',
						'operator' => 'in',
						'values'   => array( 'Bilbao' ),
					),
				) ),
			),
			array(
				'code'      => 'local-sur',
				'name'      => 'Pizzería Sur',
				'priority'  => 30,
				'is_default' => 0,
				'rules'     => wp_json_encode( array(
					array(
						'type'     => 'city',
						'operator' => 'in',
						'values'   => array( 'Sevilla' ),
					),
				) ),
			),
		);

		foreach ( $definitions as $definition ) {
			// phpcs:ignore WordPress.DB.DirectDatabaseQuery
			$existing_id = (int) $wpdb->get_var(
				$wpdb->prepare( "SELECT id FROM {$table} WHERE code = %s", $definition['code'] )
			);

			if ( $existing_id > 0 ) {
				$created[] = array( 'id' => $existing_id, 'code' => $definition['code'] );
				continue;
			}

			// phpcs:ignore WordPress.DB.DirectDatabaseQuery
			$wpdb->insert(
				$table,
				array(
					'code'       => $definition['code'],
					'name'       => $definition['name'],
					'enabled'    => 1,
					'priority'   => $definition['priority'],
					'rules'      => $definition['rules'],
					'is_default' => $definition['is_default'],
					'created_at' => $now,
					'updated_at' => $now,
				),
				array( '%s', '%s', '%d', '%d', '%s', '%d', '%s', '%s' )
			);

			$created[] = array( 'id' => (int) $wpdb->insert_id, 'code' => $definition['code'] );
		}

		return $created;
	}

	/** Inserta el agente de pruebas con el token dado. */
	private static function insert_agent( int $location_id, array $token ): void {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_agents';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->insert(
			$table,
			array(
				'location_id'  => $location_id,
				'name'         => self::AGENT_NAME,
				'token_hash'   => $token['hash'],
				'token_prefix' => $token['prefix'],
				'status'       => 'active',
				'created_at'   => Clock::now_sql(),
			),
			array( '%d', '%s', '%s', '%s', '%s', '%s' )
		);
	}

	/** Busca el agente de pruebas ya sembrado. */
	private static function find_agent(): ?object {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_agents';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$row = $wpdb->get_row(
			$wpdb->prepare( "SELECT id, location_id FROM {$table} WHERE name = %s", self::AGENT_NAME )
		);

		return $row ?: null;
	}

	/** Borra los datos sembrados: trabajos, destinos, agente y locales. */
	private static function reset(): void {
		global $wpdb;

		$jobs_table    = $wpdb->prefix . 'orp_jobs';
		$agents_table  = $wpdb->prefix . 'orp_agents';
		$dest_table    = $wpdb->prefix . 'orp_destinations';
		$locations_tab = $wpdb->prefix . 'orp_locations';

		$location_ids = array_map( 'intval', $wpdb->get_col(
			// phpcs:ignore WordPress.DB.PreparedSQL.NotPrepared, WordPress.DB.DirectDatabaseQuery
			$wpdb->prepare(
				'SELECT id FROM ' . $locations_tab . ' WHERE code IN (' . implode( ', ', array_fill( 0, count( self::LOCATION_CODES ), '%s' ) ) . ')',
				self::LOCATION_CODES
			)
		) );

		if ( $location_ids ) {
			$placeholders = implode( ', ', array_fill( 0, count( $location_ids ), '%d' ) );

			// phpcs:ignore WordPress.DB.PreparedSQL.NotPrepared, WordPress.DB.DirectDatabaseQuery
			$wpdb->query( $wpdb->prepare( "DELETE FROM {$jobs_table} WHERE location_id IN ({$placeholders})", $location_ids ) );
			// phpcs:ignore WordPress.DB.PreparedSQL.NotPrepared, WordPress.DB.DirectDatabaseQuery
			$wpdb->query( $wpdb->prepare( "DELETE FROM {$dest_table} WHERE location_id IN ({$placeholders})", $location_ids ) );
		}

		// phpcs:ignore WordPress.DB.PreparedSQL.NotPrepared, WordPress.DB.DirectDatabaseQuery
		$wpdb->query( $wpdb->prepare( "DELETE FROM {$agents_table} WHERE name = %s", self::AGENT_NAME ) );

		$placeholders = implode( ', ', array_fill( 0, count( self::LOCATION_CODES ), '%s' ) );
		// phpcs:ignore WordPress.DB.PreparedSQL.NotPrepared, WordPress.DB.DirectDatabaseQuery
		$wpdb->query(
			$wpdb->prepare( "DELETE FROM {$locations_tab} WHERE code IN ({$placeholders})", self::LOCATION_CODES )
		);
	}
}
