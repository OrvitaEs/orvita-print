<?php
/**
 * QA temporal (_t_): restaura paper_width_mm=80 tras la prueba de config_version.
 */

namespace Orvita\PrintBridge\Tools;

use Orvita\PrintBridge\Support\Settings;

$bumped = Settings::save( array( 'paper_width_mm' => 80 ) );
$s      = Settings::all();
echo 'paper_width_mm=' . $s['paper_width_mm'] . ' bumped=' . var_export( $bumped, true ) . ' config_version=' . (int) get_option( 'orp_config_version', 1 ) . "\n";
