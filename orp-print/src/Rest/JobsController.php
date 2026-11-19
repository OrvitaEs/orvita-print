<?php
/**
 * Endpoints REST de trabajos: poll con cursor, claim atómico, ack idempotente
 * y release. Contrato en Doc/04 §5.4–5.7 y códigos de error en §6.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Rest;

use Orvita\PrintBridge\Support\Clock;
use Orvita\PrintBridge\Support\Logger;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class JobsController {

	/** Máximo de trabajos por poll (Doc/04 §5.4). */
	private const MAX_LIMIT = 20;

	/** Registra las rutas de trabajos. */
	public static function register(): void {
		register_rest_route(
			Router::API_NAMESPACE,
			'/jobs',
			array(
				'methods'             => 'GET',
				'callback'            => array( self::class, 'handle_list' ),
				'permission_callback' => array( Auth::class, 'check' ),
				'args'                => array(
					'after' => array(
						'type'              => 'integer',
						'default'           => 0,
						'sanitize_callback' => 'absint',
					),
					'limit' => array(
						'type'              => 'integer',
						'default'           => 10,
						'sanitize_callback' => 'absint',
					),
				),
			)
		);

		register_rest_route(
			Router::API_NAMESPACE,
			'/jobs/(?P<id>\d+)/claim',
			array(
				'methods'             => 'POST',
				'callback'            => array( self::class, 'handle_claim' ),
				'permission_callback' => array( Auth::class, 'check' ),
			)
		);

		register_rest_route(
			Router::API_NAMESPACE,
			'/jobs/(?P<id>\d+)/ack',
			array(
				'methods'             => 'POST',
				'callback'            => array( self::class, 'handle_ack' ),
				'permission_callback' => array( Auth::class, 'check' ),
			)
		);

		register_rest_route(
			Router::API_NAMESPACE,
			'/jobs/(?P<id>\d+)/release',
			array(
				'methods'             => 'POST',
				'callback'            => array( self::class, 'handle_release' ),
				'permission_callback' => array( Auth::class, 'check' ),
			)
		);
	}

	/** GET /jobs?after=<id>&limit=<n> — trabajos pendientes del local del token. */
	public static function handle_list( \WP_REST_Request $request ): \WP_REST_Response {
		$agent = Auth::agent( $request );
		$after = max( 0, (int) $request->get_param( 'after' ) );
		$limit = min( self::MAX_LIMIT, max( 1, (int) $request->get_param( 'limit' ) ) );

		$rows   = self::pending_for_location( (int) $agent['location_id'], $after, $limit );
		$jobs   = array();
		$cursor = $after;

		foreach ( $rows as $row ) {
			$jobs[] = array(
				'id'           => (int) $row->id,
				'order_id'     => (int) $row->order_id,
				'printer_role' => (string) $row->printer_role,
				'created_at'   => Clock::to_iso( $row->created_at ),
				'expires_at'   => Clock::to_iso( $row->expires_at ),
				'attempt'      => (int) $row->attempts,
				'document'     => json_decode( (string) $row->payload, true ),
			);
			$cursor = (int) $row->id;
		}

		return new \WP_REST_Response(
			array(
				'jobs'   => $jobs,
				'cursor' => $cursor,
			),
			200
		);
	}

	/**
	 * POST /jobs/{id}/claim — reclamo atómico (Doc/04 §4).
	 * 200 con el trabajo · 409 si otro lo cogió · 410 si caducó · 404 si no es del local.
	 */
	public static function handle_claim( \WP_REST_Request $request ) {
		$agent  = Auth::agent( $request );
		$job_id = (int) $request['id'];
		$loc_id = (int) $agent['location_id'];

		$job = self::find( $job_id, $loc_id );
		if ( null === $job ) {
			return self::error( 'orp_not_found', __( 'Trabajo no encontrado para este local.', 'orp-print' ), 404 );
		}

		if ( 'pending' === $job->status ) {
			$affected = self::claim_atomically( $job_id, (int) $agent['id'] );
			if ( $affected > 0 ) {
				$job = self::find( $job_id, $loc_id );
				return self::job_response( $job );
			}

			// affected_rows = 0: otro agente ganó la carrera o caducó entre lecturas. Releer el estado real.
			$job = self::find( $job_id, $loc_id );
		}

		if ( null === $job || self::is_expired( $job ) ) {
			return self::error( 'orp_job_expired', __( 'El trabajo ha caducado y no se imprimirá.', 'orp-print' ), 410 );
		}

		return self::error(
			'orp_already_claimed',
			__( 'El trabajo ya no está pendiente.', 'orp-print' ),
			409,
			array( 'job_status' => (string) $job->status )
		);
	}

	/**
	 * POST /jobs/{id}/ack — resultado de impresión, idempotente (Doc/04 §5.6).
	 * Un ack repetido no altera el estado final.
	 */
	public static function handle_ack( \WP_REST_Request $request ) {
		$agent  = Auth::agent( $request );
		$job_id = (int) $request['id'];
		$loc_id = (int) $agent['location_id'];

		$job = self::find( $job_id, $loc_id );
		if ( null === $job ) {
			return self::error( 'orp_not_found', __( 'Trabajo no encontrado para este local.', 'orp-print' ), 404 );
		}

		$status = (string) $request->get_param( 'status' );
		if ( ! in_array( $status, array( 'delivered', 'failed' ), true ) ) {
			return self::error( 'orp_invalid_payload', __( 'El ack exige status «delivered» o «failed».', 'orp-print' ), 400 );
		}

		// Idempotencia: si ya tiene estado final, se devuelve sin tocar nada.
		if ( in_array( $job->status, array( 'delivered', 'failed' ), true ) ) {
			return new \WP_REST_Response(
				array( 'ok' => true, 'job_status' => (string) $job->status ),
				200
			);
		}

		if ( 'claimed' !== $job->status || (int) $job->claimed_by !== (int) $agent['id'] ) {
			return self::error(
				'orp_already_claimed',
				__( 'El trabajo no está reclamado por este agente.', 'orp-print' ),
				409,
				array( 'job_status' => (string) $job->status )
			);
		}

		$error = null;
		if ( 'failed' === $status ) {
			$code  = sanitize_text_field( (string) $request->get_param( 'error_code' ) );
			$detail = sanitize_text_field( (string) $request->get_param( 'error' ) );
			$error = trim( $code . ( '' !== $detail ? ': ' . $detail : '' ) );
			if ( '' === $error ) {
				$error = __( 'Error sin detalle.', 'orp-print' );
			}
		}

		self::ack( $job_id, $status, $error );

		Logger::log(
			'delivered' === $status ? 'info' : 'error',
			'jobs',
			'delivered' === $status ? 'Trabajo entregado al spooler.' : 'Trabajo marcado como failed.',
			$job_id,
			(int) $agent['id'],
			null !== $error ? array( 'error' => $error ) : array()
		);

		return new \WP_REST_Response(
			array( 'ok' => true, 'job_status' => $status ),
			200
		);
	}

	/**
	 * POST /jobs/{id}/release — devuelve un trabajo a pending sin consumir intento (Doc/04 §5.7).
	 */
	public static function handle_release( \WP_REST_Request $request ) {
		$agent  = Auth::agent( $request );
		$job_id = (int) $request['id'];
		$loc_id = (int) $agent['location_id'];

		$job = self::find( $job_id, $loc_id );
		if ( null === $job ) {
			return self::error( 'orp_not_found', __( 'Trabajo no encontrado para este local.', 'orp-print' ), 404 );
		}

		// Ya estaba en cola: resultado equivalente, idempotente.
		if ( 'pending' === $job->status ) {
			return new \WP_REST_Response( array( 'ok' => true, 'job_status' => 'pending' ), 200 );
		}

		$reason = sanitize_text_field( (string) $request->get_param( 'reason' ) );
		if ( '' === $reason ) {
			$reason = __( 'fallo transitorio', 'orp-print' );
		}

		if ( 'claimed' === $job->status && (int) $job->claimed_by === (int) $agent['id'] ) {
			self::release_job( $job_id, (int) $agent['id'], $reason );
			Logger::log( 'warning', 'jobs', 'Trabajo devuelto a la cola sin consumir intento.', $job_id, (int) $agent['id'], array( 'reason' => $reason ) );

			return new \WP_REST_Response( array( 'ok' => true, 'job_status' => 'pending' ), 200 );
		}

		return self::error(
			'orp_already_claimed',
			__( 'El trabajo no está reclamado por este agente.', 'orp-print' ),
			409,
			array( 'job_status' => (string) $job->status )
		);
	}

	/* ---------------------------------------------------------------------
	 * Acceso a datos (público para poder probar el reclamo atómico).
	 * ------------------------------------------------------------------- */

	/** Busca un trabajo limitado al local del token. */
	public static function find( int $job_id, int $location_id ): ?object {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_jobs';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché: la cola debe ser exacta.
		$row = $wpdb->get_row(
			$wpdb->prepare(
				"SELECT * FROM {$table} WHERE id = %d AND location_id = %d",
				$job_id,
				$location_id
			)
		);

		return $row ?: null;
	}

	/**
	 * Reclamo atómico exactamente como lo define Doc/04 §4. Solo gana el
	 * primero: si affected_rows = 0, otro agente lo cogió o el trabajo
	 * caducó (el controlador relee el estado y responde 409/410).
	 *
	 * @return int Filas afectadas (0 o 1).
	 */
	public static function claim_atomically( int $job_id, int $agent_id ): int {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_jobs';
		$now   = Clock::now_sql();

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- UPDATE atómico, sin caché.
		return (int) $wpdb->query(
			$wpdb->prepare(
				"UPDATE {$table}
				    SET status     = 'claimed',
				        claimed_by = %d,
				        claimed_at = %s,
				        attempts   = attempts + 1
				  WHERE id = %d
				    AND status = 'pending'
				    AND expires_at > %s
				    AND attempts < max_attempts",
				$agent_id,
				$now,
				$job_id,
				$now
			)
		);
	}

	/**
	 * Aplica el ack: delivered fija delivered_at y limpia el error; failed
	 * guarda el último error. La cláusula status = 'claimed' lo hace idempotente.
	 */
	public static function ack( int $job_id, string $status, ?string $error ): bool {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_jobs';

		if ( 'delivered' === $status ) {
			// phpcs:ignore WordPress.DB.DirectDatabaseQuery
			$affected = $wpdb->query(
				$wpdb->prepare(
					"UPDATE {$table} SET status = 'delivered', delivered_at = %s, last_error = NULL
					  WHERE id = %d AND status = 'claimed'",
					Clock::now_sql(),
					$job_id
				)
			);
		} else {
			// phpcs:ignore WordPress.DB.DirectDatabaseQuery
			$affected = $wpdb->query(
				$wpdb->prepare(
					"UPDATE {$table} SET status = 'failed', last_error = %s
					  WHERE id = %d AND status = 'claimed'",
					(string) $error,
					$job_id
				)
			);
		}

		return (int) $affected > 0;
	}

	/**
	 * Devuelve un trabajo reclamado a pending sin consumir intento.
	 */
	public static function release_job( int $job_id, int $agent_id, string $reason ): bool {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_jobs';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$affected = $wpdb->query(
			$wpdb->prepare(
				"UPDATE {$table} SET status = 'pending', claimed_by = NULL, claimed_at = NULL, last_error = %s
				  WHERE id = %d AND status = 'claimed' AND claimed_by = %d",
				'Devuelto a la cola: ' . $reason,
				$job_id,
				$agent_id
			)
		);

		return (int) $affected > 0;
	}

	/** Trabajos pendientes del local, con cursor por id (nunca por fecha). */
	public static function pending_for_location( int $location_id, int $after, int $limit ): array {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_jobs';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		return (array) $wpdb->get_results(
			$wpdb->prepare(
				"SELECT id, order_id, printer_role, attempts, payload, created_at, expires_at
				   FROM {$table}
				  WHERE location_id = %d
				    AND status = 'pending'
				    AND id > %d
				  ORDER BY id ASC
				  LIMIT %d",
				$location_id,
				$after,
				$limit
			)
		);
	}

	/** Nº de trabajos pendientes de un local (para el heartbeat). */
	public static function pending_count( int $location_id ): int {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_jobs';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		return (int) $wpdb->get_var(
			$wpdb->prepare(
				"SELECT COUNT(*) FROM {$table} WHERE location_id = %d AND status = 'pending'",
				$location_id
			)
		);
	}

	/* ---------------------------------------------------------------------
	 * Internos.
	 * ------------------------------------------------------------------- */

	/** ¿Ha superado el trabajo su fecha de caducidad? (comparación en UTC) */
	private static function is_expired( object $job ): bool {
		if ( 'expired' === $job->status ) {
			return true;
		}

		$expires = strtotime( (string) $job->expires_at . ' UTC' );
		return false !== $expires && $expires <= time();
	}

	/** Respuesta 200 con el trabajo completo reclamado. */
	private static function job_response( ?object $job ): \WP_REST_Response {
		if ( null === $job ) {
			return new \WP_REST_Response( array(), 200 );
		}

		return new \WP_REST_Response(
			array(
				'id'           => (int) $job->id,
				'order_id'     => (int) $job->order_id,
				'printer_role' => (string) $job->printer_role,
				'copies'       => (int) $job->copies,
				'status'       => (string) $job->status,
				'created_at'   => Clock::to_iso( $job->created_at ),
				'expires_at'   => Clock::to_iso( $job->expires_at ),
				'attempt'      => (int) $job->attempts,
				'document'     => json_decode( (string) $job->payload, true ),
			),
			200
		);
	}

	/**
	 * Error REST con el código del contrato (Doc/04 §6) y su estado HTTP.
	 *
	 * @param array<string, mixed> $extra Datos adicionales (p. ej. job_status).
	 */
	private static function error( string $code, string $message, int $status, array $extra = array() ): \WP_Error {
		return new \WP_Error( $code, $message, array_merge( array( 'status' => $status ), $extra ) );
	}
}
