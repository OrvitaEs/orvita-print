<?php
/**
 * LocationRepository — CRUD de orp_locations desde el panel (F1-02).
 * Las reglas viajan como JSON validado por RuleMatcher::sanitize_rules_json().
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Domain;

use Orvita\PrintBridge\Support\Clock;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class LocationRepository {

	/** Todos los locales, por prioridad ascendente. */
	public static function all(): array {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_locations';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		return (array) $wpdb->get_results(
			"SELECT id, code, name, enabled, priority, rules, is_default, created_at, updated_at
			   FROM {$table}
			  ORDER BY priority ASC, id ASC",
			ARRAY_A
		);
	}

	/** Local por id (array o null). */
	public static function get( int $id ): ?array {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_locations';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$row = $wpdb->get_row(
			$wpdb->prepare(
				"SELECT id, code, name, enabled, priority, rules, is_default, created_at, updated_at
				   FROM {$table}
				  WHERE id = %d",
				$id
			),
			ARRAY_A
		);

		return is_array( $row ) ? $row : null;
	}

	/** Local por código canónico. */
	public static function find_by_code( string $code ): ?array {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_locations';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$row = $wpdb->get_row(
			$wpdb->prepare(
				"SELECT id, code, name, enabled, priority, rules, is_default, created_at, updated_at
				   FROM {$table}
				  WHERE code = %s",
				$code
			),
			ARRAY_A
		);

		return is_array( $row ) ? $row : null;
	}

	/**
	 * Crea o actualiza un local. Sanea y valida; devuelve el id nuevo o
	 * el error del JSON de reglas.
	 *
	 * @param array{id?: int, code: string, name: string, enabled: bool, priority: int, rules_json: string, is_default: bool} $data Datos.
	 * @return array{id?: int, error?: string}
	 */
	public static function save( array $data ): array {
		global $wpdb;

		$code = sanitize_key( (string) $data['code'] );
		$name = sanitize_text_field( (string) $data['name'] );

		if ( '' === $code || '' === $name ) {
			return array( 'error' => 'missing_fields' );
		}

		// El código es canónico: solo minúsculas, números y guiones.
		if ( ! preg_match( '/^[a-z0-9][a-z0-9_-]*$/', $code ) ) {
			return array( 'error' => 'invalid_code' );
		}

		$validated = RuleMatcher::sanitize_rules_json( (string) $data['rules_json'] );
		if ( null !== $validated['error'] ) {
			return array( 'error' => $validated['error'] );
		}

		$id        = isset( $data['id'] ) ? (int) $data['id'] : 0;
		$is_default = ! empty( $data['is_default'] );

		// Unicidad del código (excluyendo el propio local al editar).
		$dup = self::find_by_code( $code );
		if ( null !== $dup && $id !== (int) $dup['id'] ) {
			return array( 'error' => 'duplicate_code' );
		}

		$row = array(
			'code'       => $code,
			'name'       => $name,
			'enabled'    => ! empty( $data['enabled'] ) ? 1 : 0,
			'priority'   => max( 0, (int) $data['priority'] ),
			'rules'      => $validated['rules'] ? (string) wp_json_encode( $validated['rules'] ) : null,
			'is_default' => $is_default ? 1 : 0,
			'updated_at' => Clock::now_sql(),
		);
		$fmt = array( '%s', '%s', '%d', '%d', '%s', '%d', '%s' );

		if ( $id > 0 ) {
			// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
			$wpdb->update( $wpdb->prefix . 'orp_locations', $row, array( 'id' => $id ), $fmt, array( '%d' ) );

			return array( 'id' => $id );
		}

		$row['created_at'] = Clock::now_sql();
		$fmt[]             = '%s';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$wpdb->insert( $wpdb->prefix . 'orp_locations', $row, $fmt );

		$id = (int) $wpdb->insert_id;

		// Solo hay un local por defecto: reajusta el resto si este lo es.
		if ( $is_default ) {
			self::make_sole_default( $id );
		}

		return array( 'id' => $id );
	}

	/** Garantiza que solo este local tenga is_default = 1. */
	public static function make_sole_default( int $id ): void {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_locations';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$wpdb->query(
			$wpdb->prepare( "UPDATE {$table} SET is_default = CASE WHEN id = %d THEN 1 ELSE 0 END", $id )
		);
	}

	/** Marca un local como por defecto y sincroniza orp_settings.default_location_id. */
	public static function set_default( int $id ): bool {
		if ( null === self::get( $id ) ) {
			return false;
		}

		self::make_sole_default( $id );

		$settings           = \Orvita\PrintBridge\Support\Settings::all();
		$settings['default_location_id'] = $id;
		update_option( \Orvita\PrintBridge\Support\Settings::OPTION, $settings, false );

		return true;
	}

	/** Borra un local. Sus salidas y agentes quedan huérfanos: se limpian. */
	public static function delete( int $id ): bool {
		global $wpdb;

		if ( null === self::get( $id ) ) {
			return false;
		}

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$wpdb->delete( $wpdb->prefix . 'orp_locations', array( 'id' => $id ), array( '%d' ) );

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$wpdb->delete( $wpdb->prefix . 'orp_destinations', array( 'location_id' => $id ), array( '%d' ) );

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$wpdb->delete( $wpdb->prefix . 'orp_agents', array( 'location_id' => $id ), array( '%d' ) );

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$wpdb->delete( $wpdb->prefix . 'orp_pairing_codes', array( 'location_id' => $id ), array( '%d' ) );

		return true;
	}

	/** Cuenta los locales existentes. */
	public static function count(): int {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		return (int) $wpdb->get_var( 'SELECT COUNT(*) FROM ' . $wpdb->prefix . 'orp_locations' );
	}
}
