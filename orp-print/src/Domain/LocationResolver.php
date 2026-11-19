<?php
/**
 * Resolución del local de un pedido.
 *
 * Doc/03 §3: el local se decide por regla, nunca por el agente. La evaluación
 * completa de reglas (city, postcode, shipping_method, product_category,
 * order_meta) es la tarea F1-02; en F0 solo existe el local por defecto como
 * red de seguridad, de modo que ningún pedido desaparezca en silencio.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Domain;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class LocationResolver {

	/** Local por id. */
	public static function get( int $id ): ?object {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_locations';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$row = $wpdb->get_row(
			$wpdb->prepare(
				"SELECT id, code, name, enabled, priority, rules, is_default
				   FROM {$table}
				  WHERE id = %d",
				$id
			)
		);

		return $row ?: null;
	}

	/** Local por defecto activo con menor prioridad numérica. */
	public static function default_location(): ?object {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_locations';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$row = $wpdb->get_row(
			"SELECT id, code, name, enabled, priority, rules, is_default
			   FROM {$table}
			  WHERE is_default = 1 AND enabled = 1
			  ORDER BY priority ASC
			  LIMIT 1"
		);

		return $row ?: null;
	}

	/** Todos los locales activos ordenados por prioridad ascendente. */
	public static function enabled(): array {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_locations';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		return (array) $wpdb->get_results(
			"SELECT id, code, name, enabled, priority, rules, is_default
			   FROM {$table}
			  WHERE enabled = 1
			  ORDER BY priority ASC, id ASC"
		);
	}

	/**
	 * Local de un pedido. En F0: siempre el local por defecto (F1-02 añadirá
	 * RuleMatcher con las reglas de orp_locations evaluadas por prioridad).
	 */
	public static function resolve_for_order( \WC_Order $order ): ?object {
		unset( $order ); // La evaluación de reglas llega en F1-02.

		return self::default_location();
	}
}
