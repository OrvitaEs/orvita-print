<?php
/**
 * Registro de rutas REST: namespace orp-print/v1 (contrato congelado, Doc/06 §8).
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Rest;

use Orvita\PrintBridge\Support\Clock;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class Router {

	/** Namespace REST del contrato (Doc/04 §1). */
	public const API_NAMESPACE = 'orp-print/v1';

	/** Versión de la API anunciada en /health. */
	public const API_VERSION = 1;

	/** Registra el hook que crea las rutas. */
	public static function register(): void {
		// Cabecera Retry-After en el 429 del límite de peticiones (B1).
		Auth::register_hooks();

		add_action( 'rest_api_init', array( self::class, 'register_routes' ) );
	}

	/** Crea todas las rutas del contrato. */
	public static function register_routes(): void {
		// GET /health — público, sin autenticación (Doc/04 §5.1).
		register_rest_route(
			self::API_NAMESPACE,
			'/health',
			array(
				'methods'             => 'GET',
				'callback'            => array( self::class, 'handle_health' ),
				'permission_callback' => '__return_true',
			)
		);

		JobsController::register();
		HeartbeatController::register();
		ConfigController::register();

		// POST /pair — pública junto a /health (F1-07, Doc/04 §5.2).
		PairController::register();
	}

	/** Respuesta de salud y versiones (Doc/04 §5.1). */
	public static function handle_health(): \WP_REST_Response {
		$hpos = false;
		if ( class_exists( \Automattic\WooCommerce\Utilities\OrderUtil::class ) ) {
			$hpos = \Automattic\WooCommerce\Utilities\OrderUtil::custom_orders_table_usage_is_enabled();
		}

		return new \WP_REST_Response(
			array(
				'ok'             => true,
				'plugin_version' => ORP_VERSION,
				'api_version'    => self::API_VERSION,
				'woocommerce'    => defined( 'WC_VERSION' ) ? WC_VERSION : null,
				'hpos'           => $hpos,
				'server_time'    => Clock::now_iso(),
			),
			200
		);
	}
}
