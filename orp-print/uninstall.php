<?php
/**
 * Desinstalación de Orvita Print.
 *
 * @package Orvita\PrintBridge
 *
 * Por defecto NO se borra ningún dato (trabajos, locales, agentes y registros se
 * conservan por si el plugin se reinstala). Solo se elimina todo si el
 * administrador activó explícitamente la clave `uninstall_purge` de `orp_settings`.
 *
 * La rama de purga borra además la capacidad `manage_orp_print` del rol
 * administrator y los transients `orp_rl_*` (límite de peticiones) con sus
 * timeouts.
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

foreach ( array( 'orp_db_version', 'orp_settings', 'orp_template', 'orp_template_version', 'orp_config_version' ) as $option ) {
	delete_option( $option );
}

// B6: la capacidad se concedió al rol administrator en la activación; se retira.
// ORP_CAPABILITY no está definida aquí: uninstall.php se ejecuta sin cargar el plugin.
// phpcs:ignore WordPress.WP.GlobalVariablesOverride.Prohibited -- Procedural en el ámbito global: operar sobre el rol es la propia tarea de desinstalación.
$role = get_role( 'administrator' );
if ( $role instanceof WP_Role ) {
	$role->remove_cap( 'manage_orp_print' );
}

// B6: transients del límite de peticiones por token (orp_rl_*) y sus timeouts,
// tanto en la tabla de opciones como en la caché de objetos.
// phpcs:ignore WordPress.DB.DirectDatabaseQuery, WordPress.DB.PreparedSQL -- Operación de desinstalación.
$wpdb->query(
	"DELETE FROM {$wpdb->options}
	  WHERE option_name LIKE '\\_transient\\_orp\\_rl\\_%'
	     OR option_name LIKE '\\_transient\\_timeout\\_orp\\_rl\\_%'"
);

// Si hay caché de objetos (redis/memcached), los transients viven fuera de wp_options.
wp_cache_flush();
