<?php
/**
 * Siembra el contador del rate limit a 119 para forzar el 429 en la siguiente
 * petición HTTP (verificación B1 end-to-end).
 *
 * Uso: npx @wordpress/env run cli wp eval-file .../_seed_rl.php <agent_id>
 */

$agent_id = isset( $args[0] ) ? (int) $args[0] : 0;
if ( $agent_id < 1 ) {
	echo "Uso: wp eval-file .../_seed_rl.php <agent_id>\n";
	exit( 1 );
}

global $wpdb;
$wpdb->query(
	"DELETE FROM {$wpdb->options}
	  WHERE option_name LIKE '\_transient\_orp\_rl\_%'
	     OR option_name LIKE '\_transient\_timeout\_orp\_rl\_%'"
);

$count = isset( $args[1] ) ? (int) $args[1] : 119;

update_option( '_transient_orp_rl_' . $agent_id, $count );
update_option( '_transient_timeout_orp_rl_' . $agent_id, time() + 60 );

echo 'contador del agente ' . $agent_id . ' sembrado a ' . $count . " (ventana 60 s)\n";
