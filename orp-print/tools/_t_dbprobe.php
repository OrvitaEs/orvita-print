<?php
/**
 * Sonda del entorno de desarrollo tras las verificaciones (sin strict_types:
 * wp eval-file la exige como primera sentencia absoluta del script).
 */

global $wpdb;

$tables = array( 'orp_locations', 'orp_destinations', 'orp_jobs', 'orp_agents' );

foreach ( $tables as $t ) {
	$name = $wpdb->prefix . $t;
	// phpcs:ignore WordPress.DB.PreparedSQL.NotPrepared, WordPress.DB.DirectDatabaseQuery
	$rows = (array) $wpdb->get_results( "SELECT * FROM {$name} LIMIT 12", ARRAY_A );
	echo "== {$name}: " . count( $rows ) . " filas\n";
	foreach ( $rows as $row ) {
		echo json_encode( $row, JSON_UNESCAPED_UNICODE ) . "\n";
	}
}

echo "\n== settings\n";
echo json_encode( get_option( 'orp_settings' ), JSON_UNESCAPED_UNICODE ) . "\n";
echo "orp_db_version: " . get_option( 'orp_db_version' ) . "\n";
