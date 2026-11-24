<?php
global $wpdb;
$created = \Orvita\PrintBridge\Domain\PairingCodes::create( 113, 1 );
if ( ! $created ) {
	WP_CLI::error( 'No se pudo generar el código.' );
}
echo 'CODE=' . $created['code'] . "\n";
echo 'EXPIRES=' . $created['expires_at'] . "\n";
