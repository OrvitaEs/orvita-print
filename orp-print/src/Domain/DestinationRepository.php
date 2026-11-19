<?php
/**
 * Salidas (destinos físicos): impresora, copias y estado por rol (Doc/04 §2.5).
 * El agente las registra en cada heartbeat; el plugin las usa para el fan-out (F1-06).
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Domain;

use Orvita\PrintBridge\Support\Clock;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class DestinationRepository {

	/**
	 * Upsert por (location_id, role): el agente reporta sus salidas en cada
	 * heartbeat y el plugin las guarda sin duplicar (Doc/04 §5.3).
	 *
	 * @param int   $location_id Local al que pertenece la salida.
	 * @param array $destination role, label, printer_name, copies, enabled.
	 */
	public static function upsert( int $location_id, array $destination ): void {
		global $wpdb;

		$table   = $wpdb->prefix . 'orp_destinations';
		$role    = (string) $destination['role'];
		$printer = (string) $destination['printer_name'];
		$label   = (string) ( $destination['label'] ?? $role );
		$copies  = max( 1, (int) ( $destination['copies'] ?? 1 ) );
		$enabled = ! empty( $destination['enabled'] );

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$existing = (int) $wpdb->get_var(
			$wpdb->prepare(
				"SELECT id FROM {$table} WHERE location_id = %d AND role = %s",
				$location_id,
				$role
			)
		);

		if ( $existing > 0 ) {
			// phpcs:ignore WordPress.DB.DirectDatabaseQuery
			$wpdb->update(
				$table,
				array(
					'label'        => $label,
					'printer_name' => $printer,
					'copies'       => $copies,
					'enabled'      => $enabled ? 1 : 0,
					'updated_at'   => Clock::now_sql(),
				),
				array( 'id' => $existing ),
				array( '%s', '%s', '%d', '%d', '%s' ),
				array( '%d' )
			);
			return;
		}

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->insert(
			$table,
			array(
				'location_id'  => $location_id,
				'role'         => $role,
				'label'        => $label,
				'printer_name' => $printer,
				'copies'       => $copies,
				'enabled'      => $enabled ? 1 : 0,
				'updated_at'   => Clock::now_sql(),
			),
			array( '%d', '%s', '%s', '%s', '%d', '%d', '%s' )
		);
	}

	/** Salidas de un local. */
	public static function for_location( int $location_id, bool $only_enabled = false ): array {
		global $wpdb;

		$table   = $wpdb->prefix . 'orp_destinations';
		$where   = $only_enabled ? ' AND enabled = 1' : '';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		return (array) $wpdb->get_results(
			$wpdb->prepare(
				"SELECT id, role, label, printer_name, copies, enabled, updated_at
				   FROM {$table}
				  WHERE location_id = %d{$where}
				  ORDER BY id ASC",
				$location_id
			)
		);
	}
}
