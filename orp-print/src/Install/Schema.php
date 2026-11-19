<?php
/**
 * Esquema de base de datos: las 6 tablas de Doc/04 §2 con dbDelta y opciones por defecto.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Install;

use Orvita\PrintBridge\Support\Settings;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class Schema {

	/**
	 * Crea o actualiza el esquema. Idempotente: reactivar no duplica tablas.
	 */
	public static function install(): void {
		global $wpdb;

		require_once ABSPATH . 'wp-admin/includes/upgrade.php';

		$charset_collate = $wpdb->get_charset_collate();
		$prefix          = $wpdb->prefix . 'orp_';

		// Doc/04 §2.1 — Locales y reglas de asignación.
		// Nota dbDelta: dos espacios en «PRIMARY KEY  (id)»; sin claves foráneas.
		$sql = "CREATE TABLE {$prefix}locations (
			id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
			code VARCHAR(32) NOT NULL,
			name VARCHAR(120) NOT NULL,
			enabled TINYINT(1) NOT NULL DEFAULT 1,
			priority SMALLINT NOT NULL DEFAULT 100,
			rules LONGTEXT NULL,
			is_default TINYINT(1) NOT NULL DEFAULT 0,
			created_at DATETIME NOT NULL,
			updated_at DATETIME NOT NULL,
			PRIMARY KEY  (id),
			UNIQUE KEY code (code),
			KEY enabled_priority (enabled, priority)
		) {$charset_collate};";
		dbDelta( $sql );

		// Doc/04 §2.2 — Agentes. Solo se guarda el SHA-256 del token y un prefijo de 8 caracteres.
		$sql = "CREATE TABLE {$prefix}agents (
			id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
			location_id BIGINT UNSIGNED NOT NULL,
			name VARCHAR(120) NOT NULL,
			token_hash CHAR(64) NOT NULL,
			token_prefix CHAR(8) NOT NULL,
			status VARCHAR(16) NOT NULL DEFAULT 'active',
			agent_version VARCHAR(32) NULL,
			platform VARCHAR(64) NULL,
			last_seen_at DATETIME NULL,
			created_at DATETIME NOT NULL,
			revoked_at DATETIME NULL,
			PRIMARY KEY  (id),
			UNIQUE KEY token_hash (token_hash),
			KEY location_status (location_id, status),
			KEY last_seen (last_seen_at)
		) {$charset_collate};";
		dbDelta( $sql );

		// Doc/04 §2.3 — Códigos de emparejamiento (se usan desde F1-07).
		$sql = "CREATE TABLE {$prefix}pairing_codes (
			id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
			code CHAR(6) NOT NULL,
			location_id BIGINT UNSIGNED NOT NULL,
			attempts TINYINT NOT NULL DEFAULT 0,
			expires_at DATETIME NOT NULL,
			used_at DATETIME NULL,
			created_by BIGINT UNSIGNED NOT NULL,
			PRIMARY KEY  (id),
			UNIQUE KEY code (code),
			KEY expires (expires_at)
		) {$charset_collate};";
		dbDelta( $sql );

		// Doc/04 §2.4 — Cola de trabajos. payload es LONGTEXT (no JSON) por compatibilidad con MariaDB y dbDelta.
		$sql = "CREATE TABLE {$prefix}jobs (
			id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
			location_id BIGINT UNSIGNED NOT NULL,
			order_id BIGINT UNSIGNED NOT NULL,
			printer_role VARCHAR(32) NOT NULL DEFAULT 'default',
			copies TINYINT UNSIGNED NOT NULL DEFAULT 1,
			status VARCHAR(16) NOT NULL DEFAULT 'pending',
			payload LONGTEXT NOT NULL,
			idempotency_key CHAR(64) NOT NULL,
			attempts TINYINT NOT NULL DEFAULT 0,
			max_attempts TINYINT NOT NULL DEFAULT 5,
			claimed_by BIGINT UNSIGNED NULL,
			claimed_at DATETIME NULL,
			delivered_at DATETIME NULL,
			last_error TEXT NULL,
			created_at DATETIME NOT NULL,
			expires_at DATETIME NOT NULL,
			PRIMARY KEY  (id),
			UNIQUE KEY idempotency_key (idempotency_key),
			KEY location_status_id (location_id, status, id),
			KEY status_expires (status, expires_at),
			KEY order_id (order_id)
		) {$charset_collate};";
		dbDelta( $sql );

		// Doc/04 §2.5 — Salidas (destinos físicos) reportadas por el agente.
		$sql = "CREATE TABLE {$prefix}destinations (
			id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
			location_id BIGINT UNSIGNED NOT NULL,
			role VARCHAR(32) NOT NULL,
			label VARCHAR(120) NOT NULL,
			printer_name VARCHAR(255) NOT NULL,
			copies TINYINT UNSIGNED NOT NULL DEFAULT 1,
			enabled TINYINT(1) NOT NULL DEFAULT 1,
			updated_at DATETIME NOT NULL,
			PRIMARY KEY  (id),
			UNIQUE KEY location_role (location_id, role),
			KEY location_enabled (location_id, enabled)
		) {$charset_collate};";
		dbDelta( $sql );

		// Doc/04 §2.6 — Registros técnicos. Sin datos personales; retención 30 días (F1-09).
		$sql = "CREATE TABLE {$prefix}logs (
			id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
			created_at DATETIME NOT NULL,
			level VARCHAR(16) NOT NULL,
			context VARCHAR(32) NOT NULL,
			job_id BIGINT UNSIGNED NULL,
			agent_id BIGINT UNSIGNED NULL,
			message VARCHAR(255) NOT NULL,
			data LONGTEXT NULL,
			PRIMARY KEY  (id),
			KEY created (created_at),
			KEY job (job_id)
		) {$charset_collate};";
		dbDelta( $sql );

		self::seed_options();
	}

	/** Opciones por defecto (Doc/04 §2.7). add_option no sobreescribe lo ya guardado. */
	private static function seed_options(): void {
		add_option( 'orp_db_version', ORP_DB_VERSION );
		add_option( 'orp_template_version', 1 );
		add_option( 'orp_config_version', 1 );
		add_option( Settings::OPTION, Settings::defaults() );

		// Migraciones futuras: si la versión guardada difiere de ORP_DB_VERSION,
		// aquí se ejecutan las migraciones acumuladas y luego se actualiza.
		if ( get_option( 'orp_db_version' ) !== ORP_DB_VERSION ) {
			update_option( 'orp_db_version', ORP_DB_VERSION );
		}
	}
}
