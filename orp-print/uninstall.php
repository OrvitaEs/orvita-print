<?php
/**
 * Desinstalación de Orvita Print.
 *
 * Por defecto NO se borra ningún dato (trabajos, locales, agentes y registros se
 * conservan por si el plugin se reinstala). Solo se elimina todo si el
 * administrador activó explícitamente la clave `uninstall_purge` de `orp_settings`.
 */

declare(strict_types=1);

if ( ! defined( 'WP_UNINSTALL_PLUGIN' ) ) {
	exit;
}

$settings = get_option( 'orp_settings', array() );
if ( ! is_array( $settings ) || empty( $settings['uninstall_purge'] ) ) {
	return;
}

global $wpdb;
$prefix = $wpdb->prefix . 'orp_';

// Se borran primero las tablas hijas.
foreach ( array( 'logs', 'destinations', 'jobs', 'pairing_codes', 'agents', 'locations' ) as $suffix ) {
	// phpcs:ignore WordPress.DB.DirectDatabaseQuery, WordPress.DB.PreparedSQL -- Operación de desinstalación.
	$wpdb->query( "DROP TABLE IF EXISTS {$prefix}{$suffix}" );
}

foreach ( array( 'orp_db_version', 'orp_settings', 'orp_template_version', 'orp_config_version' ) as $option ) {
	delete_option( $option );
}

// Transients del límite de peticiones por token.
// phpcs:ignore WordPress.DB.DirectDatabaseQuery, WordPress.DB.PreparedSQL -- Operación de desinstalación.
$wpdb->query(
	"DELETE FROM {$wpdb->options}
	  WHERE option_name LIKE '\\_transient\\_orp\\_rl\\_%'
	     OR option_name LIKE '\\_transient\\_timeout\\_orp\\_rl\\_%'"
);
