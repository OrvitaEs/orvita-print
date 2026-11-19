<?php
/**
 * Registro técnico en orp_logs: sin datos personales, solo job_id, agent_id y códigos.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Support;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class Logger {

	/**
	 * Escribe una fila en orp_logs (Doc/04 §2.6).
	 *
	 * @param string      $level    info | warning | error.
	 * @param string      $context  Contexto corto: jobs, agents, config…
	 * @param string      $message  Mensaje legible (máx. 255 caracteres).
	 * @param int|null    $job_id   Trabajo relacionado, si lo hay.
	 * @param int|null    $agent_id Agente relacionado, si lo hay.
	 * @param array<mixed> $data    Datos adicionales (se serializan como JSON).
	 */
	public static function log(
		string $level,
		string $context,
		string $message,
		?int $job_id = null,
		?int $agent_id = null,
		array $data = array()
	): void {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_logs';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia del plugin, sin caché.
		$wpdb->query(
			$wpdb->prepare(
				"INSERT INTO {$table} (created_at, level, context, job_id, agent_id, message, data)
				 VALUES (%s, %s, %s, %d, %d, %s, %s)",
				Clock::now_sql(),
				mb_substr( $level, 0, 16 ),
				mb_substr( $context, 0, 32 ),
				$job_id ?? 0,
				$agent_id ?? 0,
				mb_substr( $message, 0, 255 ),
				$data ? (string) wp_json_encode( $data ) : null
			)
		);
	}
}
