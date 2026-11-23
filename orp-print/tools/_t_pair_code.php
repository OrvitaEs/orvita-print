<?php
/**
 * Auxiliar del humo cruzado (desde el ANFITRIÓN, como el agente .NET):
 * imprime un código de emparejamiento vigente del local verify-f107.
 *   npx @wordpress/env run cli wp eval-file /var/www/html/wp-content/plugins/orp-print/tools/_t_pair_code.php
 */

use Orvita\PrintBridge\Domain\LocationRepository;
use Orvita\PrintBridge\Domain\PairingCodes;

if ( null === LocationRepository::find_by_code( 'verify-f107' ) ) {
	LocationRepository::save( array(
		'code'       => 'verify-f107',
		'name'       => 'Local de verificación F1-07',
		'enabled'    => true,
		'priority'   => 1,
		'rules_json' => '[]',
		'is_default' => false,
	) );
}

$loc  = LocationRepository::find_by_code( 'verify-f107' );
$code = PairingCodes::create( (int) $loc['id'], 1 );

echo $code['code'] . "\n";
