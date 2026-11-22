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
	 * B5: una sola consulta INSERT ... ON DUPLICATE KEY UPDATE sobre la clave
	 * única location_role. Sin SELECT previo: dos heartbeats concurrentes no
	 * pueden dejar filas duplicadas (clave única) ni sobrescribirse.
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

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Upsert atómico sobre la clave única (location_id, role).
		$wpdb->query(
			$wpdb->prepare(
				"INSERT INTO {$table}
					(location_id, role, label, printer_name, copies, enabled, updated_at)
				 VALUES (%d, %s, %s, %s, %d, %d, %s)
				 ON DUPLICATE KEY UPDATE
					label        = VALUES(label),
					printer_name = VALUES(printer_name),
					copies       = VALUES(copies),
					enabled      = VALUES(enabled),
					updated_at   = VALUES(updated_at)",
				$location_id,
				$role,
				$label,
				$printer,
				$copies,
				$enabled ? 1 : 0,
				Clock::now_sql()
			)
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
