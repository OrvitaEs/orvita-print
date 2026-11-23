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

		// F1-06: una salida desactivada deja de generar trabajos nuevos y sus
		// pendientes se cancelan (Doc/04 §2.8: «Si una salida está desactivada,
		// no se crean trabajos para ella. Los trabajos ya pendientes se cancelan»).
		if ( ! $enabled ) {
			self::cancel_pending_for( $location_id, $role );
		}
	}

	/**
	 * Upsert en lote de las salidas de un cuerpo (heartbeat y pair, F1-07):
	 * saneado y cotas idénticos en ambos caminos (Doc/04 §5.2 y §5.3).
	 *
	 * @param array<mixed> $destinations Lista cruda del cuerpo.
	 * @return array{upserted: int, rejected: int} Aceptadas y descartadas.
	 */
	public static function upsert_many( int $location_id, array $destinations ): array {
		$upserted  = 0;
		$rejected  = 0;

		foreach ( $destinations as $destination ) {
			if ( ! is_array( $destination ) ) {
				++$rejected;
				continue;
			}

			$role         = sanitize_key( (string) ( $destination['role'] ?? '' ) );
			$printer_name = sanitize_text_field( (string) ( $destination['printer_name'] ?? '' ) );

			if ( '' === $role || '' === $printer_name ) {
				++$rejected;
				continue;
			}

			$label  = sanitize_text_field( (string) ( $destination['label'] ?? '' ) );
			$copies = (int) ( $destination['copies'] ?? 1 );

			self::upsert(
				$location_id,
				array(
					'role'         => $role,
					'label'        => '' !== $label ? $label : $role,
					'printer_name' => $printer_name,
					'copies'       => max( 1, min( 99, $copies ) ),
					'enabled'      => ! empty( $destination['enabled'] ),
				)
			);

			++$upserted;
		}

		return array(
			'upserted' => $upserted,
			'rejected' => $rejected,
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

	/**
	 * Todas las salidas, agrupables por local (panel de destinos, F1-06).
	 * Solo lectura: las salidas las reporta el agente en su heartbeat.
	 */
	public static function all(): array {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_destinations';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		return (array) $wpdb->get_results(
			"SELECT id, location_id, role, label, printer_name, copies, enabled, updated_at
			   FROM {$table}
			  ORDER BY location_id ASC, id ASC"
		);
	}

	/**
	 * Cancela los trabajos PENDIENTES de una salida (local + rol). Se llama al
	 * desactivar la salida (Doc/04 §2.8); los trabajos ya claimed/delivered no
	 * se tocan: un trabajo reclamado está en el agente y el papel no se desimprime.
	 *
	 * @return int Número de trabajos cancelados.
	 */
	public static function cancel_pending_for( int $location_id, string $role ): int {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_jobs';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$affected = $wpdb->query(
			$wpdb->prepare(
				"UPDATE {$table} SET status = 'cancelled'
				  WHERE location_id = %d AND printer_role = %s AND status = 'pending'",
				$location_id,
				$role
			)
		);

		if ( (int) $affected > 0 ) {
			\Orvita\PrintBridge\Support\Logger::log(
				'warning',
				'jobs',
				'Salida desactivada: trabajos pendientes cancelados, sin papel.',
				null,
				null,
				array(
					'location_id' => $location_id,
					'role'        => $role,
					'cancelados'  => (int) $affected,
				)
			);
		}

		return (int) $affected;
	}
}
