<?php
/**
 * Resolución del local de un pedido.
 *
 * Doc/03 §3: el local se decide por regla, nunca por el agente. La evaluación
 * real viaja en RuleMatcher (F1-02): se recorren los locales activos por
 * prioridad ascendente y gana el primero cuyas reglas (todas, en AND) se
 * cumplen. Si ninguno coincide, se usa el local con is_default = 1 y se deja
 * un aviso para el panel: nunca se descarta un pedido en silencio.
 *
 * Doc/05 §5: se lee **shipping primero** (billing solo como respaldo) y no se
 * asume billing: el checkout del piloto lo desactiva.
 *
 * Nota de alcance: el local se resuelve al crear el trabajo (aquí). Cambiar una
 * regla después no altera los trabajos ya encolados: location_id queda
 * congelado en la fila de orp_jobs.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Domain;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class LocationResolver {

	/** Transient que guarda los avisos de caída al local por defecto. */
	private const FALLBACK_TRANSIENT = 'orp_fallback_notice';

	/** Transient de avisos de pedidos sin local por defecto (F1-05: nunca en silencio). */
	private const MISSING_DEFAULT_TRANSIENT = 'orp_missing_default_notice';

	/** Local por id (row de BD). */
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
	 * Local de un pedido: primer local activo (por prioridad) cuyas reglas
	 * se cumplen; si ninguno, el local por defecto con aviso.
	 */
	public static function resolve_for_order( \WC_Order $order ): ?object {
		foreach ( self::enabled() as $location ) {
			$rules = json_decode( (string) ( $location->rules ?? '' ), true );
			if ( ! is_array( $rules ) || array() === $rules ) {
				continue; // Sin reglas solo puede ser el local por defecto.
			}

			if ( RuleMatcher::order_matches( $order, $rules ) ) {
				return $location;
			}
		}

		$fallback = self::default_location();

		if ( null !== $fallback ) {
			self::record_fallback( $order, $fallback );
		} else {
			// Sin local por defecto: queda registrado Y avisado, el pedido no
			// se pierde en silencio (F1-05: log + aviso en el panel).
			\Orvita\PrintBridge\Support\Logger::log(
				'warning',
				'locations',
				'Pedido sin local coincidente y sin local por defecto.',
				null,
				null,
				array( 'order_id' => $order->get_id() )
			);

			self::record_missing_default( $order );
		}

		return $fallback;
	}

	/** Deja el aviso de pedidos que no se encolaron por falta de local por defecto. */
	private static function record_missing_default( \WC_Order $order ): void {
		$entries = get_transient( self::MISSING_DEFAULT_TRANSIENT );
		if ( ! is_array( $entries ) ) {
			$entries = array();
		}

		// Un pedido entra UNA vez en la cola (el disparo puede resolver varias veces).
		$order_id = $order->get_id();
		foreach ( $entries as $entry ) {
			if ( (int) ( $entry['order_id'] ?? 0 ) === $order_id ) {
				return;
			}
		}

		$entries[] = array(
			'order_id' => $order_id,
			'time'     => time(),
		);

		// Máximo 5 avisos en cola para no crecer sin control.
		set_transient( self::MISSING_DEFAULT_TRANSIENT, array_slice( $entries, -5, 5, true ), HOUR_IN_SECONDS );
	}

	/** Avisos de falta de local por defecto pendientes de mostrar (se consumen una vez). */
	public static function take_missing_default_notices(): array {
		$entries = get_transient( self::MISSING_DEFAULT_TRANSIENT );

		if ( ! is_array( $entries ) || array() === $entries ) {
			return array();
		}

		delete_transient( self::MISSING_DEFAULT_TRANSIENT );

		return $entries;
	}

	/** Deja el aviso de caída al local por defecto (se muestra en admin_notices). */
	private static function record_fallback( \WC_Order $order, object $location ): void {
		$entries = get_transient( self::FALLBACK_TRANSIENT );
		if ( ! is_array( $entries ) ) {
			$entries = array();
		}

		// Un pedido entra UNA vez en la cola de avisos: el mismo pedido se
		// resuelve varias veces (creación + cambios de estado) y el aviso no
		// debe multiplicarse.
		$order_id = $order->get_id();
		foreach ( $entries as $entry ) {
			if ( (int) ( $entry['order_id'] ?? 0 ) === $order_id ) {
				return;
			}
		}

		$entries[] = array(
			'order_id'      => $order_id,
			'location_code' => (string) $location->code,
			'time'          => time(),
		);

		// Máximo 5 avisos en cola para no crecer sin control.
		set_transient( self::FALLBACK_TRANSIENT, array_slice( $entries, -5, 5, true ), HOUR_IN_SECONDS );
	}

	/** Avisos pendientes de mostrar en el panel. */
	public static function take_fallback_notices(): array {
		$entries = get_transient( self::FALLBACK_TRANSIENT );

		if ( ! is_array( $entries ) || array() === $entries ) {
			return array();
		}

		delete_transient( self::FALLBACK_TRANSIENT );

		return $entries;
	}
}
