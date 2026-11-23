<?php
/**
 * QA temporal (_t_): verificación dinámica de F1-01/F1-02 — pedidos, reglas,
 * normalización, local por defecto y aviso de caída. Solo crea datos propios
 * (códigos con prefijo t-) y los limpia en el modo «cleanup».
 *
 * Uso (dentro del cli de wp-env):
 *   wp eval-file tools/_t_verify_rules_orders.php run <code_in> <code_contains>
 *   wp eval-file tools/_t_verify_rules_orders.php cleanup <code_in> <code_contains> <username>
 */

namespace Orvita\PrintBridge\Tools;

use Orvita\PrintBridge\Domain\LocationRepository;
use Orvita\PrintBridge\Domain\LocationResolver;
use Orvita\PrintBridge\Domain\RuleMatcher;
use Orvita\PrintBridge\Support\Settings;

$GLOBALS['ORP_PASS'] = 0;
$GLOBALS['ORP_FAIL'] = 0;

// wp eval-file pasa los argumentos como la variable local $args del include.
$args = $args ?? array();
$mode = $args[0] ?? '';

/** Afirmación mínima. */
function check( bool $cond, string $label ): void {
	if ( $cond ) {
		echo "PASS: $label\n";
		++$GLOBALS['ORP_PASS'];
	} else {
		echo "FAIL: $label\n";
		++$GLOBALS['ORP_FAIL'];
	}
}

function codes_ok(): int {
	$pass = $GLOBALS['ORP_PASS'];
	$fail = $GLOBALS['ORP_FAIL'];
	echo "---- resumen: $pass PASS / $fail FAIL\n";
	return 0 === $fail ? 0 : 1;
}

if ( 'setup' === $mode ) {
	// Crea los dos locales de prueba por la API del repositorio.
	$code_in       = (string) ( $args[1] ?? '' );
	$code_contains = (string) ( $args[2] ?? '' );

	$in = LocationRepository::save(
		array(
			'code'       => $code_in,
			'name'       => 'QA in',
			'enabled'    => true,
			'priority'   => 1,
			'rules_json' => wp_json_encode(
				array(
					array(
						'type'     => 'city',
						'operator' => 'in',
						'values'   => array( 'Alcalá' ),
					),
				)
			),
			'is_default' => false,
		)
	);

	$ct = LocationRepository::save(
		array(
			'code'       => $code_contains,
			'name'       => 'QA contains',
			'enabled'    => true,
			'priority'   => 2,
			'rules_json' => wp_json_encode(
				array(
					array(
						'type'     => 'city',
						'operator' => 'contains',
						'values'   => array( 'Alcalá' ),
					),
				)
			),
			'is_default' => false,
		)
	);

	echo wp_json_encode( array( 'in' => $in, 'contains' => $ct ) ) . "\n";
	exit( ( isset( $in['id'] ) && isset( $ct['id'] ) ) ? 0 : 1 );
}

if ( 'cleanup' === $mode ) {
	$code_in       = (string) ( $args[1] ?? '' );
	$code_contains = (string) ( $args[2] ?? '' );
	$username      = (string) ( $args[3] ?? '' );

	foreach ( array( $code_in, $code_contains ) as $code ) {
		$loc = LocationRepository::find_by_code( $code );
		if ( null !== $loc ) {
			LocationRepository::delete( (int) $loc['id'] );
			echo "cleanup: local $code borrado\n";
		}
	}

	$user = get_user_by( 'login', $username );
	if ( $user ) {
		wp_delete_user( (int) $user->ID );
		echo "cleanup: usuario $username borrado\n";
	}

	// Restaura el estado previo verificado: settings sin local por defecto y
	// local-centro marcado como is_default en la tabla (así estaba).
	$centro = LocationRepository::find_by_code( 'local-centro' );
	if ( null !== $centro ) {
		LocationRepository::make_sole_default( (int) $centro['id'] );
	}
	$s = \Orvita\PrintBridge\Support\Settings::all();
	$s['default_location_id'] = 0;
	update_option( \Orvita\PrintBridge\Support\Settings::OPTION, $s, false );
	delete_transient( 'orp_fallback_notice' );
	echo "cleanup: settings.default_location_id=0, tabla default=local-centro, transient de aviso borrado\n";
	exit( 0 );
}

if ( 'run' !== $mode ) {
	echo "modo desconocido\n";
	exit( 1 );
}

$code_in       = (string) ( $args[1] ?? '' );
$code_contains = (string) ( $args[2] ?? '' );

$loc_in       = LocationRepository::find_by_code( $code_in );
$loc_contains = LocationRepository::find_by_code( $code_contains );
check( null !== $loc_in, "local regla in existe ($code_in)" );
check( null !== $loc_contains, "local regla contains existe ($code_contains)" );

if ( null === $loc_in || null === $loc_contains ) {
	exit( 1 );
}

$default_before = LocationResolver::default_location();
echo 'default actual en tabla: ' . ( $default_before ? $default_before->code . ' #' . $default_before->id : '(ninguno)' ) . "\n";
$settings = Settings::all();
echo 'settings.default_location_id=' . $settings['default_location_id'] . "\n";

// ------------------------------------------------------------ T1: coincidencia
$order = new \WC_Order();
$order->set_shipping_city( 'Alcalá' );
$res = LocationResolver::resolve_for_order( $order );
check( null !== $res && (int) $res->id === (int) $loc_in['id'], "T1 shipping «Alcalá» resuelve al local de regla in (id {$loc_in['id']})" );

// --------------------------------------- T2: sin coincidencia → default + aviso
delete_transient( 'orp_fallback_notice' );
$order2 = new \WC_Order();
$order2->set_shipping_city( 'Barcelona' );
$res2 = LocationResolver::resolve_for_order( $order2 );
check( null !== $res2 && null !== $default_before && (int) $res2->id === (int) $default_before->id, 'T2 shipping «Barcelona» sin regla → local por defecto (' . ( $default_before ? $default_before->code : '?' ) . ')' );
$notice = get_transient( 'orp_fallback_notice' );
check( is_array( $notice ) && 1 === count( $notice ) && (int) $notice[0]['order_id'] === $order2->get_id(), 'T2 transient orp_fallback_notice registra el pedido caído' );
delete_transient( 'orp_fallback_notice' );

// ------------------------- T3: «ALCALÁ DE HENARES» — in NO, contains SÍ (orden)
$order3 = new \WC_Order();
$order3->set_shipping_city( 'ALCALÁ DE HENARES' );
$res3 = LocationResolver::resolve_for_order( $order3 );
check( null !== $res3 && (int) $res3->id === (int) $loc_contains['id'], 'T3 «ALCALÁ DE HENARES» no iguala con in pero sí con contains (prioridad 1ª in, 2ª contains)' );
$rules_in = json_decode( (string) $loc_in['rules'], true );
check( 'alcala de henares' !== $rules_in[0]['values'][0] && ! RuleMatcher::matches_facts( array( 'city' => RuleMatcher::normalize( 'ALCALÁ DE HENARES' ) ), $rules_in ), 'T3 operador in NO iguala «ALCALÁ DE HENARES» (igualdad normalizada, decisión documentada)' );

// -------------------------- T4: sin shipping, billing como respaldo («alcala»)
$order4 = new \WC_Order();
$order4->set_billing_city( 'alcala' );
$res4 = LocationResolver::resolve_for_order( $order4 );
check( null !== $res4 && (int) $res4->id === (int) $loc_in['id'], 'T4 shipping vacío → billing «alcala» como respaldo, con normalización minúsculas/sin acentos' );

// ------------------- T5: shipping definido manda aunque billing coincida
$order5 = new \WC_Order();
$order5->set_shipping_city( 'Barcelona' );
$order5->set_billing_city( 'Alcalá' );
$res5 = LocationResolver::resolve_for_order( $order5 );
check( null !== $res5 && null !== $default_before && (int) $res5->id === (int) $default_before->id, 'T5 shipping «Barcelona» manda sobre billing «Alcalá» (no enruta por billing)' );
check( null === $res5 || (int) $res5->id !== (int) $loc_in['id'], 'T5 y no resolvió al local de la regla in' );
delete_transient( 'orp_fallback_notice' );

// ------------------------------------------- T6: ñ → n vía remove_accents (puro)
$rules_coruna = array(
	array(
		'type'     => 'city',
		'operator' => 'in',
		'values'   => array( 'A Coruña' ),
	),
);
check( RuleMatcher::matches_facts( array( 'city' => RuleMatcher::normalize( 'a coruna' ) ), $rules_coruna ), 'T6 «A Coruña» ≡ «a coruna» (ñ→n, lógica pura)' );
check( ! RuleMatcher::matches_facts( array( 'city' => RuleMatcher::normalize( 'Coruna del Norte' ) ), $rules_coruna ), 'T6 in no iguala variantes largas (igualdad, no contains)' );

// ------------------------------------- T7: is_default único y sincronización
$before = LocationRepository::all();
$defaults_before = array_filter( $before, static fn( $l ) => 1 === (int) $l['is_default'] );
check( 1 === count( $defaults_before ), 'T7 hay exactamente un local por defecto antes del cambio: ' . wp_json_encode( array_column( $defaults_before, 'code' ) ) );

check( LocationRepository::set_default( (int) $loc_in['id'] ), 'T7 set_default devuelve true' );
$after = LocationRepository::all();
$defaults_after = array_values( array_filter( $after, static fn( $l ) => 1 === (int) $l['is_default'] ) );
check( 1 === count( $defaults_after ) && (int) $defaults_after[0]['id'] === (int) $loc_in['id'], 'T7 tras set_default solo hay un local por defecto y es el elegido' );
$settings_after = Settings::all();
check( (int) $settings_after['default_location_id'] === (int) $loc_in['id'], 'T7 orp_settings.default_location_id sincronizado con el local elegido (' . $settings_after['default_location_id'] . ')' );

// ------------------------------------------- T8: Settings::save no toca is_default
// Guardar ajustes con default_location_id=0 (el selector «Ninguno») NO limpia
// la tabla: se documenta la divergencia observable.
$conf_before = (int) get_option( 'orp_config_version', 1 );
Settings::save( array( 'default_location_id' => 0 ) );
$conf_after = (int) get_option( 'orp_config_version', 1 );
check( $conf_after === $conf_before, 'T8 default_location_id no sube orp_config_version (no es clave del agente)' );
$row_now = LocationRepository::get( (int) $loc_in['id'] );
check( null !== $row_now && 1 === (int) $row_now['is_default'], 'T8 guardar default_location_id=0 deja is_default=1 en la tabla (divergencia observada: settings=0, tabla=1)' );
// Restaura para no dejar residuos distintos del estado encontrado.
LocationRepository::set_default( (int) $loc_in['id'] );

exit( codes_ok() );
