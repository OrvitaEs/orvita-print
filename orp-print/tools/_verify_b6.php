<?php
/**
 * Verificación B6 en la BD de PRUEBAS (tests-cli, aislada): prepara capacidad,
 * transient y ajuste de purga, ejecuta uninstall.php tal cual y comprueba la
 * limpieza completa (tablas, opciones, capacidad y transients orp_rl_*).
 *
 * Uso: npx @wordpress/env run tests-cli wp eval-file /var/www/html/wp-content/plugins/orp-print/tools/_verify_b6.php
 */

// --- Preparación -----------------------------------------------------------
$role = get_role( 'administrator' );
if ( $role && ! $role->has_cap( 'manage_orp_print' ) ) {
	$role->add_cap( 'manage_orp_print', true );
}
set_transient( 'orp_rl_999', 7, 120 );
set_transient( 'orp_rl_998', 3, 120 );

$settings         = get_option( 'orp_settings', array() );
$settings['uninstall_purge'] = true;
update_option( 'orp_settings', $settings );

global $wpdb;
$tables_before = $wpdb->get_col( "SHOW TABLES LIKE '%orp_%'" );
printf( "antes: tablas orp = %d, cap = %s, transient orp_rl_999 = %s\n",
	count( $tables_before ),
	$role && $role->has_cap( 'manage_orp_print' ) ? 'sí' : 'no',
	(string) get_transient( 'orp_rl_999' )
);

// --- Ejecutar uninstall.php tal cual ---------------------------------------
if ( ! defined( 'WP_UNINSTALL_PLUGIN' ) ) {
	define( 'WP_UNINSTALL_PLUGIN', true );
}
include '/var/www/html/wp-content/plugins/orp-print/uninstall.php';

// --- Comprobaciones ---------------------------------------------------------
$pass = true;

$tables_after = $wpdb->get_col( "SHOW TABLES LIKE '%orp_%'" );
printf( "después: tablas orp = %d\n", count( $tables_after ) );
if ( 0 !== count( $tables_after ) ) {
	echo "FAIL: quedan tablas orp_*.\n";
	$pass = false;
}

if ( false !== get_option( 'orp_settings' ) || false !== get_option( 'orp_db_version' ) ) {
	echo "FAIL: quedan opciones orp_*.\n";
	$pass = false;
}

$leftover = (int) $wpdb->get_var( "SELECT COUNT(*) FROM {$wpdb->options} WHERE option_name LIKE '%orp_rl%'" );
printf( "transients orp_rl_* restantes en wp_options: %d\n", $leftover );
if ( 0 !== $leftover ) {
	echo "FAIL: quedan transients orp_rl_*.\n";
	$pass = false;
}

$role = get_role( 'administrator' );
$has_cap = $role ? $role->has_cap( 'manage_orp_print' ) : false;
printf( "capacidad manage_orp_print en administrator: %s\n", $has_cap ? 'sí' : 'no' );
if ( $has_cap ) {
	echo "FAIL: la capacidad sigue en administrator.\n";
	$pass = false;
}

echo $pass ? "RESULTADO: PASS\n" : "RESULTADO: FAIL\n";
exit( $pass ? 0 : 1 );
