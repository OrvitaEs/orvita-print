<?php
/**
 * Ajustes del plugin: valores por defecto y lectura de orp_settings (Doc/04 §2.7).
 *
 * Principio: en el servidor nada va hardcoded; todo lo configurable vive en la
 * base de datos y el código solo define los valores por defecto. El panel es el
 * único escritor (F1-01).
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Support;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class Settings {

	/** Nombre de la opción en wp_options. */
	public const OPTION = 'orp_settings';

	/** Valores por defecto del preset «Aviso» (el más simple, Doc 03 §15). */
	public static function defaults(): array {
		return array(
			'trigger_states'      => array( 'created' ),
			'dedupe_scope'        => 'order',
			'paper_width_mm'      => 80,
			'encoding'            => 'CP850',
			'cut_mode'            => 'partial',
			'open_drawer'         => false,
			'poll_interval'       => 10,
			'sound_alert'         => false,
			'default_location_id' => 0,
			'job_expiry_minutes'  => 30,
			'retention_days'      => 90,
			// Borrado de datos en la desinstalación: apagado por defecto.
			'uninstall_purge'     => false,
		);
	}

	/** Ajustes completos (guardados + por defecto). */
	public static function all(): array {
		$stored = get_option( self::OPTION, array() );
		if ( ! is_array( $stored ) ) {
			$stored = array();
		}

		return array_merge( self::defaults(), $stored );
	}

	/** Lee una clave concreta con su valor por defecto como respaldo. */
	public static function get( string $key, mixed $fallback = null ): mixed {
		$all = self::all();
		return $all[ $key ] ?? $fallback;
	}
}
