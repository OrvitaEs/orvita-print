<?php
/**
 * GET /config — configuración común del documento que el agente re-sincroniza
 * cuando cambia config_version (Doc/04 §5.8). Las impresoras y las copias no
 * viajan aquí: las define el agente y las reporta en el heartbeat.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Rest;

use Orvita\PrintBridge\Support\Settings;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class ConfigController {

	/** Registra la ruta. */
	public static function register(): void {
		register_rest_route(
			Router::API_NAMESPACE,
			'/config',
			array(
				'methods'             => 'GET',
				'callback'            => array( self::class, 'handle' ),
				'permission_callback' => array( Auth::class, 'check' ),
			)
		);
	}

	/** Respuesta según Doc/04 §5.8. */
	public static function handle( \WP_REST_Request $request ): \WP_REST_Response {
		$settings = Settings::all();

		return new \WP_REST_Response(
			array(
				'config_version' => (int) get_option( 'orp_config_version', 1 ),
				'paper_width_mm' => (int) $settings['paper_width_mm'],
				'encoding'       => (string) $settings['encoding'],
				'cut_mode'       => (string) $settings['cut_mode'],
				'open_drawer'    => (bool) $settings['open_drawer'],
				'poll_interval'  => (int) $settings['poll_interval'],
				'sound_alert'    => (bool) $settings['sound_alert'],
			),
			200
		);
	}
}
