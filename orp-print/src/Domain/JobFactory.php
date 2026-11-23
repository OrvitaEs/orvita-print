<?php
/**
 * JobFactory — creación de trabajos con idempotencia (Doc/04 §3).
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Domain;

use Orvita\PrintBridge\Support\Clock;
use Orvita\PrintBridge\Support\Logger;
use Orvita\PrintBridge\Support\Settings;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class JobFactory {

	/** Reintentos máximos por defecto (Doc/04 §2.4). */
	private const DEFAULT_MAX_ATTEMPTS = 5;

	/**
	 * Crea un trabajo en la cola con INSERT IGNORE sobre idempotency_key.
	 * Si la clave ya existe, no se duplica: devuelve null y se registra.
	 *
	 * @param int    $location_id  Local destino.
	 * @param int    $order_id     Pedido (0 en demostraciones).
	 * @param string $printer_role Salida (rol de impresión).
	 * @param int    $copies       Copias congeladas de la salida al crear el trabajo.
	 * @param array  $document     Documento neutro v1 (se guarda en payload).
	 * @param string $trigger      «created» o el slug del estado (o «seed»/«manual»).
	 * @return int|null Id nuevo, o null si el trabajo ya existía.
	 */
	public static function create(
		int $location_id,
		int $order_id,
		string $printer_role,
		int $copies,
		array $document,
		string $trigger = 'created'
	): ?int {
		$key = self::idempotency_key( $order_id, $trigger, $location_id, $printer_role );

		return self::insert(
			$location_id,
			$order_id,
			$printer_role,
			$copies,
			$document,
			$key
		);
	}

	/**
	 * Clave de idempotencia según Doc/04 §3. El ámbito de deduplicación
	 * (dedupe_scope) decide si el disparador forma parte de la clave:
	 * - order:   sha256(order_id | location_id | role | template_version)
	 * - trigger: incluye el trigger entre order_id y location_id.
	 */
	public static function idempotency_key( int $order_id, string $trigger, int $location_id, string $printer_role ): string {
		$template_version = (int) get_option( 'orp_template_version', 1 );
		$scope            = (string) Settings::get( 'dedupe_scope', 'order' );

		if ( 'trigger' === $scope ) {
			return hash(
				'sha256',
				$order_id . '|' . $trigger . '|' . $location_id . '|' . $printer_role . '|' . $template_version
			);
		}

		return hash(
			'sha256',
			$order_id . '|' . $location_id . '|' . $printer_role . '|' . $template_version
		);
	}

	/**
	 * Reimpresión manual desde la ficha del pedido (F1-04, Doc/04 §3):
	 * sha256(order_id | manual | printer_role | unix_timestamp | user_id).
	 * El timestamp y el usuario garantizan que siempre genera un trabajo
	 * nuevo, con el documento de la plantilla vigente.
	 *
	 * Desviación documentada del literal «unix_timestamp»: se usa
	 * microtime(true) (unix timestamp con microsegundos) para que dos
	 * reimpresiones del mismo usuario en el mismo segundo tampoco colisionen;
	 * el contrato exige que la reimpresión «siempre genere un trabajo nuevo».
	 *
	 * @param int    $location_id  Local destino.
	 * @param int    $order_id     Pedido.
	 * @param string $printer_role Salida (rol de impresión).
	 * @param int    $copies       Copias congeladas de la salida.
	 * @param array  $document     Documento neutro v1 con la plantilla actual.
	 * @param int    $user_id      Usuario que lanza la reimpresión.
	 * @return int|null Id del trabajo nuevo, o null si colisionara (no debería).
	 */
	public static function create_manual(
		int $location_id,
		int $order_id,
		string $printer_role,
		int $copies,
		array $document,
		int $user_id
	): ?int {
		$key = hash(
			'sha256',
			$order_id . '|manual|' . $printer_role . '|' . (string) microtime( true ) . '|' . $user_id
		);

		return self::insert(
			$location_id,
			$order_id,
			$printer_role,
			$copies,
			$document,
			$key
		);
	}

	/**
	 * Inserción idempotente común (INSERT IGNORE, Doc/06 §11).
	 * Si la clave ya existe, no se duplica: devuelve null y se registra.
	 */
	private static function insert(
		int $location_id,
		int $order_id,
		string $printer_role,
		int $copies,
		array $document,
		string $key
	): ?int {
		global $wpdb;

		$table     = $wpdb->prefix . 'orp_jobs';
		$now       = Clock::now_sql();
		$expires   = Clock::sql_offset_minutes( (int) Settings::get( 'job_expiry_minutes', 30 ) );
		$max_tries = self::DEFAULT_MAX_ATTEMPTS;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Inserción idempotente, sin caché.
		$affected = $wpdb->query(
			$wpdb->prepare(
				"INSERT IGNORE INTO {$table}
					(location_id, order_id, printer_role, copies, status, payload, idempotency_key, attempts, max_attempts, created_at, expires_at)
				 VALUES (%d, %d, %s, %d, 'pending', %s, %s, 0, %d, %s, %s)",
				$location_id,
				$order_id,
				$printer_role,
				max( 1, $copies ),
				(string) wp_json_encode( $document ),
				$key,
				$max_tries,
				$now,
				$expires
			)
		);

		if ( 0 === (int) $affected ) {
			// La clave ya existía: deduplicación funcionando (Doc/06 §11).
			Logger::log(
				'info',
				'jobs',
				'Trabajo duplicado descartado por idempotencia.',
				null,
				null,
				array( 'idempotency_key' => $key )
			);
			return null;
		}

		return (int) $wpdb->insert_id;
	}
}
