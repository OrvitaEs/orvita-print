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

	/** Preset por defecto: el más simple (Doc/03 §15). */
	public const DEFAULT_PRESET = 'aviso';

	/**
	 * Claves cuyo cambio afecta al agente: al guardarlas con valor distinto
	 * sube orp_config_version (el agente re-sincroniza por esa vía).
	 */
	public const AGENT_KEYS = array( 'paper_width_mm', 'encoding', 'cut_mode', 'open_drawer', 'poll_interval', 'sound_alert' );

	/** Valores por defecto del preset «Aviso» (el más simple, Doc 03 §15). */
	public static function defaults(): array {
		return array(
			'preset'              => self::DEFAULT_PRESET,
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

	/**
	 * Guarda ajustes saneados. Es el único escritor de orp_settings (Doc/04 §2.7).
	 * Si cambia alguna clave que afecta al agente, incrementa orp_config_version.
	 *
	 * @param array<string, mixed> $values Valores ya saneados por el panel.
	 */
	public static function save( array $values ): bool {
		$previous = self::all();

		$merged = array_merge( $previous, $values );

		update_option( self::OPTION, $merged, false );

		$bumped = false;
		foreach ( self::AGENT_KEYS as $key ) {
			if ( array_key_exists( $key, $values ) && $values[ $key ] !== $previous[ $key ] ) {
				$bumped = true;
			}
		}

		if ( $bumped ) {
			// El agente re-sincroniza su configuración al cambiar config_version.
			update_option( 'orp_config_version', (int) get_option( 'orp_config_version', 1 ) + 1, false );
		}

		return $bumped;
	}
}
