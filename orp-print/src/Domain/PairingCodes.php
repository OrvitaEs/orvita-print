<?php
/**
 * Códigos de emparejamiento (F1-07, Doc/04 §2.3 y §5.2): 6 dígitos, 10 minutos
 * de vida, máximo 5 intentos y un solo uso. Se generan desde el panel (el
 * administrador los teclea en el agente) y se consumen en POST /pair.
 *
 * El agente HEREDA el location_id del código (Doc/04 §2.2): nunca elige local.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Domain;

use Orvita\PrintBridge\Support\Clock;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class PairingCodes {

	/** Vida del código en minutos (Doc/03 §8: 10 minutos). */
	public const TTL_MINUTES = 10;

	/** Intentos fallidos antes de bloquear el código (Doc/05 §4.6). */
	public const MAX_ATTEMPTS = 5;

	/** Intentos de búsqueda ante colisión del código aleatorio. */
	private const GENERATE_TRIES = 5;

	/**
	 * Crea un código nuevo para un local. Unico por clave de tabla; si la
	 * lotería de 6 dígitos colisiona con un código vivo, reintenta.
	 *
	 * @param int $location_id Local al que se emparejará el agente.
	 * @param int $user_id     Usuario del panel que lo genera.
	 * @return array{code: string, expires_at: string}|null null si el local no existe.
	 */
	public static function create( int $location_id, int $user_id ): ?array {
		global $wpdb;

		if ( null === LocationRepository::get( $location_id ) ) {
			return null;
		}

		$table      = $wpdb->prefix . 'orp_pairing_codes';
		$expires_at = Clock::sql_offset_minutes( self::TTL_MINUTES );

		for ( $try = 0; $try < self::GENERATE_TRIES; $try++ ) {
			$code = str_pad( (string) random_int( 0, 999999 ), 6, '0', STR_PAD_LEFT );

			// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
			$affected = $wpdb->insert(
				$table,
				array(
					'code'        => $code,
					'location_id' => $location_id,
					'attempts'    => 0,
					'expires_at'  => $expires_at,
					'used_at'     => null,
					'created_by'  => $user_id,
				),
				array( '%s', '%d', '%d', '%s', '%s', '%d' )
			);

			if ( false !== $affected && $affected > 0 ) {
				return array(
					'code'       => $code,
					'expires_at' => $expires_at,
				);
			}
		}

		// Cinco colisiones seguidas: imposible en la práctica; el panel
		// reintenta con pulsar el botón otra vez.
		return null;
	}

	/** Busca un código por su valor exacto de 6 dígitos (fila o null). */
	public static function find( string $code ): ?object {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_pairing_codes';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$row = $wpdb->get_row(
			$wpdb->prepare(
				"SELECT id, code, location_id, attempts, expires_at, used_at
				   FROM {$table}
				  WHERE code = %s",
				$code
			)
		);

		return $row ?: null;
	}

	/**
	 * Incrementa el contador de intentos fallidos del código, con techo en
	 * MAX_ATTEMPTS: TINYINT no debe desbordarse bajo fuerza bruta (el límite
	 * por IP de S-02 frena el resto) y con 5 el código queda bloqueado igual.
	 */
	public static function bump_attempts( int $id ): void {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_pairing_codes';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Contador atómico.
		$wpdb->query(
			$wpdb->prepare(
				"UPDATE {$table} SET attempts = LEAST( attempts + 1, %d ) WHERE id = %d",
				self::MAX_ATTEMPTS,
				$id
			)
		);
	}

	/**
	 * Marca un código como usado DE FORMA ATÓMICA (patrón del claim de
	 * Doc/04 §4): affected_rows = 0 significa que otro proceso lo marcó antes
	 * o cambió de estado — solo uno de los emparejamientos simultáneos gana.
	 *
	 * @return bool true si este proceso ganó el código.
	 */
	public static function mark_used( int $id, string $when_utc ): bool {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_pairing_codes';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- UPDATE atómico, sin caché.
		return (int) $wpdb->query(
			$wpdb->prepare(
				"UPDATE {$table} SET used_at = %s WHERE id = %d AND used_at IS NULL",
				$when_utc,
				$id
			)
		) > 0;
	}

	/**
	 * Código vigente más reciente de un local (para mostrarlo en el panel):
	 * sin usar, sin caducar y sin bloquear.
	 */
	public static function latest_valid( int $location_id ): ?object {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_pairing_codes';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$row = $wpdb->get_row(
			$wpdb->prepare(
				"SELECT id, code, attempts, expires_at
				   FROM {$table}
				  WHERE location_id = %d
				    AND used_at IS NULL
				    AND expires_at > %s
				    AND attempts < %d
				  ORDER BY id DESC
				  LIMIT 1",
				$location_id,
				Clock::now_sql(),
				self::MAX_ATTEMPTS
			)
		);

		return $row ?: null;
	}
}
