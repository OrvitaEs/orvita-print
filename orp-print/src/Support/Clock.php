<?php
/**
 * Reloj: la base de datos siempre va en UTC; la vista usa wp_timezone()/wp_date().
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Support;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class Clock {

	/** Fecha y hora UTC en formato SQL para INSERT/UPDATE. */
	public static function now_sql(): string {
		return gmdate( 'Y-m-d H:i:s' );
	}

	/** Fecha y hora actual en ISO-8601 con la zona horaria del sitio. */
	public static function now_iso(): string {
		return wp_date( DATE_ATOM );
	}

	/**
	 * Convierte un datetime UTC de la base de datos a ISO-8601 con la zona
	 * horaria del sitio, como exige el contrato (Doc/04 §5).
	 *
	 * @param string|null $utc Datetime en formato SQL guardado en UTC.
	 */
	public static function to_iso( ?string $utc ): ?string {
		if ( null === $utc || '' === $utc ) {
			return null;
		}

		$timestamp = strtotime( $utc . ' UTC' );
		if ( false === $timestamp ) {
			return null;
		}

		return wp_date( DATE_ATOM, $timestamp );
	}

	/** Fecha SQL en UTC desplazada n minutos desde ahora (caducidad de trabajos). */
	public static function sql_offset_minutes( int $minutes ): string {
		return gmdate( 'Y-m-d H:i:s', time() + ( $minutes * MINUTE_IN_SECONDS ) );
	}
}
