<?php
/**
 * Verificación B5 in-process: dos upsert seguidos sobre (location_id, role)
 * deben dejar UNA fila y los valores de la última.
 *
 * Uso: npx @wordpress/env run cli wp eval-file .../_verify_b5.php
 */

global $wpdb;
$table = $wpdb->prefix . 'orp_destinations';

// Limpia las salidas de prueba.
$wpdb->query( "DELETE FROM {$table} WHERE role = 'b5-test'" );

$repo = 'Orvita\PrintBridge\Domain\DestinationRepository';

// Primer upsert: INSERT.
$repo::upsert( 999901, array(
	'role'         => 'b5-test',
	'label'        => 'Primera',
	'printer_name' => 'Impresora A',
	'copies'       => 1,
	'enabled'      => true,
) );

// Segundo upsert: UPDATE por la clave única.
$repo::upsert( 999901, array(
	'role'         => 'b5-test',
	'label'        => 'Segunda',
	'printer_name' => 'Impresora B',
	'copies'       => 3,
	'enabled'      => false,
) );

// Tercero, inmediatamente después (sin SELECT intermedio en el código del plugin).
$repo::upsert( 999901, array(
	'role'         => 'b5-test',
	'label'        => 'Tercera',
	'printer_name' => 'Impresora C',
	'copies'       => 2,
	'enabled'      => true,
) );

// phpcs:ignore
$count = (int) $wpdb->get_var( $wpdb->prepare( "SELECT COUNT(*) FROM {$table} WHERE location_id = %d AND role = 'b5-test'", 999901 ) );
// phpcs:ignore
$row = $wpdb->get_row( $wpdb->prepare( "SELECT label, printer_name, copies, enabled FROM {$table} WHERE location_id = %d AND role = 'b5-test'", 999901 ) );

printf( "filas: %d | label=%s printer=%s copies=%d enabled=%d\n", $count, $row->label, $row->printer_name, (int) $row->copies, (int) $row->enabled );

$wpdb->query( $wpdb->prepare( "DELETE FROM {$table} WHERE location_id = %d AND role = 'b5-test'", 999901 ) );

$pass = ( 1 === $count && 'Tercera' === $row->label && 'Impresora C' === $row->printer_name && 2 === (int) $row->copies && 1 === (int) $row->enabled );
echo $pass ? "RESULTADO: PASS\n" : "RESULTADO: FAIL\n";
exit( $pass ? 0 : 1 );
