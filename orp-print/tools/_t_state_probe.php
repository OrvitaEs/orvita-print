<?php
/**
 * Sonda de estado (QA temporal _t_): locales, ajustes y versión de config.
 * Uso: wp eval-file tools/_t_state_probe.php
 */

namespace Orvita\PrintBridge\Tools;

global $wpdb;

$rows = $wpdb->get_results( 'SELECT id, code, name, enabled, priority, is_default FROM ' . $wpdb->prefix . 'orp_locations ORDER BY id', ARRAY_A ); // phpcs:ignore WordPress.DB.DirectDatabaseQuery
echo "== LOCALES ==\n";
foreach ( (array) $rows as $r ) {
	echo sprintf( "id=%s code=%s enabled=%s prio=%s default=%s name=%s\n", $r['id'], $r['code'], $r['enabled'], $r['priority'], $r['is_default'], $r['name'] );
}

$settings        = \Orvita\PrintBridge\Support\Settings::all();
$settings_stored = get_option( 'orp_settings' );
echo "== AJUSTES ==\n";
echo 'default_location_id=' . ( $settings['default_location_id'] ?? '(sin clave)' ) . "\n";
echo 'stored=' . wp_json_encode( $settings_stored ) . "\n";
echo 'config_version=' . (int) get_option( 'orp_config_version', 1 ) . "\n";
echo 'db_version=' . (int) get_option( 'orp_db_version', 0 ) . "\n";

// Pedidos existentes (para no chocar con otros trabajos del mismo WP).
$count = function_exists( 'wc_get_orders' ) ? count( (array) wc_get_orders( array( 'limit' => 5, 'return' => 'ids' ) ) ) : 'woo-no-activo';
echo "== PEDIDOS (primeros 5) == " . wp_json_encode( $count ) . "\n";
