<?php
/**
 * POST /heartbeat — el agente reporta su estado y sus salidas (Doc/04 §5.3).
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Rest;

use Orvita\PrintBridge\Domain\DestinationRepository;
use Orvita\PrintBridge\Support\Clock;
use Orvita\PrintBridge\Support\Logger;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class HeartbeatController {

	/** Registra la ruta. */
	public static function register(): void {
		register_rest_route(
			Router::API_NAMESPACE,
			'/heartbeat',
			array(
				'methods'             => 'POST',
				'callback'            => array( self::class, 'handle' ),
				'permission_callback' => array( Auth::class, 'check' ),
			)
		);
	}

	/**
	 * Guarda last_seen_at y agent_version, y hace upsert de las salidas por
	 * (location_id, role). Los heartbeats no se registran en orp_logs:
	 * llegarían cada 60 s y llenarían la tabla sin aportar nada.
	 */
	public static function handle( \WP_REST_Request $request ): \WP_REST_Response {
		$agent = Auth::agent( $request );

		$agent_version = sanitize_text_field( (string) $request->get_param( 'agent_version' ) );
		$platform      = sanitize_text_field( (string) $request->get_param( 'platform' ) );
		$destinations  = $request->get_param( 'destinations' );
		if ( ! is_array( $destinations ) ) {
			$destinations = array();
		}

		self::touch_agent( (int) $agent['id'], $agent_version, $platform );

		$last_error = (string) $request->get_param( 'last_error' );

		if ( '' !== $last_error ) {
			Logger::log( 'warning', 'agents', 'El agente reporta un error.', null, (int) $agent['id'], array( 'last_error' => sanitize_text_field( $last_error ) ) );
		}

		// Saneado y upsert compartidos con POST /pair (F1-07): una sola lógica.
		$upsert_many = DestinationRepository::upsert_many( (int) $agent['location_id'], $destinations );

		// Un descarte del servidor deja rastro en orp_logs (hallazgo del E2E):
		// el agente puede mandar salidas sin printer_name ni address y hasta
		// ahora el rechazo era invisible en el panel.
		if ( $upsert_many['rejected'] > 0 ) {
			Logger::log(
				'warning',
				'agents',
				'Salidas rechazadas en el heartbeat.',
				null,
				(int) $agent['id'],
				array( 'rechazadas' => $upsert_many['rejected_items'] )
			);
		}

		return new \WP_REST_Response(
			array(
				'ok'             => true,
				'server_time'    => Clock::now_iso(),
				'pending_count'  => JobsController::pending_count( (int) $agent['location_id'] ),
				'config_version' => (int) get_option( 'orp_config_version', 1 ),
			),
			200
		);
	}

	/** Actualiza last_seen_at, agent_version y platform del agente. */
	private static function touch_agent( int $agent_id, string $agent_version, string $platform ): void {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_agents';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->query(
			$wpdb->prepare(
				"UPDATE {$table} SET last_seen_at = %s, agent_version = %s, platform = %s WHERE id = %d",
				Clock::now_sql(),
				'' !== $agent_version ? mb_substr( $agent_version, 0, 32 ) : null,
				'' !== $platform ? mb_substr( $platform, 0, 64 ) : null,
				$agent_id
			)
		);
	}
}
