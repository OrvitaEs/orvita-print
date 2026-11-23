<?php
/**
 * Verificación dinámica F1-01 + F1-02 (ejecutar dentro del cli de wp-env):
 *   npx wp-env run cli wp eval-file /var/www/html/wp-content/plugins/orp-print/tools/_verify_f1_01_02.php
 *
 * Comprueba: preset por defecto «aviso», guardado con subida de
 * orp_config_version, CRUD de locales con validación de JSON, resolución por
 * reglas (shipping primero, prioridad, acentos) y aviso de fallback al local
 * por defecto.
 *
 * Sin declare(strict_types=1): wp eval-file evalúa el fichero y exige que
 * sea la primera sentencia absoluta del script.
 */

use Orvita\PrintBridge\Admin\SettingsPage;
use Orvita\PrintBridge\Domain\LocationRepository;
use Orvita\PrintBridge\Domain\LocationResolver;
use Orvita\PrintBridge\Support\Settings;

$pass = 0;
$fail = 0;
$GLOBALS['pass'] = 0;
$GLOBALS['fail'] = 0;

function ok( string $msg ): void {
	$GLOBALS['pass']++;
	echo "PASS: $msg\n";
}

function bad( string $msg ): void {
	$GLOBALS['fail']++;
	echo "FAIL: $msg\n";
}

/** Local de prueba. */
function orp_insert_location( string $code, string $name, int $priority, string $rules, int $is_default ): int {
	global $wpdb;
	$now = gmdate( 'Y-m-d H:i:s' );
	$wpdb->insert(
		$wpdb->prefix . 'orp_locations',
		array(
			'code'       => $code,
			'name'       => $name,
			'enabled'    => 1,
			'priority'   => $priority,
			'rules'      => $rules,
			'is_default' => $is_default,
			'created_at' => $now,
			'updated_at' => $now,
		),
		array( '%s', '%s', '%d', '%d', '%s', '%d', '%s', '%s' )
	);
	return (int) $wpdb->insert_id;
}

/* ------------------------------------------------------------ preset por defecto */
update_option( 'orp_settings', array(), false );

$defaults = Settings::all();
if ( 'aviso' === $defaults['preset'] && array( 'created' ) === $defaults['trigger_states'] && 'order' === $defaults['dedupe_scope'] ) {
	ok( 'instalación nueva: preset «Aviso» (el más simple) con trigger created y dedupe order' );
} else {
	bad( 'preset por defecto: ' . wp_json_encode( $defaults ) );
}

/* ------------------------------------------------- guardado sube config_version */
$config_before = (int) get_option( 'orp_config_version', 1 );

$cfg = Settings::all();
$cfg['poll_interval'] = 15;
Settings::save( $cfg );

if ( (int) get_option( 'orp_config_version', 1 ) === $config_before + 1 ) {
	ok( 'cambiar poll_interval (afecta al agente) incrementa orp_config_version' );
} else {
	bad( 'poll_interval no subió orp_config_version' );
}

$config_before = (int) get_option( 'orp_config_version', 1 );
$cfg            = Settings::all();
$cfg['job_expiry_minutes'] = 45;
Settings::save( $cfg );

if ( (int) get_option( 'orp_config_version', 1 ) === $config_before ) {
	ok( 'cambiar job_expiry_minutes (no afecta al agente) NO incrementa orp_config_version' );
} else {
	bad( 'job_expiry_minutes subió orp_config_version sin motivo' );
}

/* ------------------------------------------------------ CRUD de locales + JSON */

// Limpia restos de ejecuciones anteriores del propio guion.
global $wpdb;
$wpdb->query( "DELETE FROM {$wpdb->prefix}orp_locations WHERE code IN ('v-alcala','v-madrid','v-bilbao','v-meta','local-alcala','local-toledo')" );

$duplicate = LocationRepository::save(
	array(
		'code'       => 'local-centro',
		'name'       => 'Duplicado',
		'enabled'    => true,
		'priority'   => 100,
		'rules_json' => '',
		'is_default' => false,
	)
);
if ( isset( $duplicate['error'] ) && 'duplicate_code' === $duplicate['error'] ) {
	ok( 'código de local duplicado rechazado' );
} else {
	bad( 'código duplicado no detectado' );
}

$bad_json = LocationRepository::save(
	array(
		'code'       => 'local-xx',
		'name'       => 'XX',
		'enabled'    => true,
		'priority'   => 100,
		'rules_json' => '[{ invalido',
		'is_default' => false,
	)
);
if ( isset( $bad_json['error'] ) && 'invalid_json' === $bad_json['error'] ) {
	ok( 'JSON de reglas inválido rechazado' );
} else {
	bad( 'JSON inválido aceptado' );
}

$created = LocationRepository::save(
	array(
		'code'       => 'local-toledo',
		'name'       => 'Pizzería Toledo',
		'enabled'    => true,
		'priority'   => 40,
		'rules_json' => '[{"type":"city","operator":"in","values":["Toledo"]}]',
		'is_default' => false,
	)
);
if ( isset( $created['id'] ) && $created['id'] > 0 ) {
	ok( 'local creado con reglas JSON válidas (id ' . $created['id'] . ')' );
} else {
	bad( 'local válido no se creó: ' . wp_json_encode( $created ) );
}

/* ------------------------------------------ resolución: shipping primero + prioridad */
$id_alcala = orp_insert_location( 'v-alcala', 'Alcalá (verificación)', 10, wp_json_encode( array( array( 'type' => 'city', 'operator' => 'in', 'values' => array( 'Alcalá' ) ) ) ), 0 );
orp_insert_location( 'v-bilbao', 'Bilbao (verificación)', 30, wp_json_encode( array( array( 'type' => 'city', 'operator' => 'in', 'values' => array( 'Bilbao' ) ) ) ), 0 );
$id_bilbao = (int) LocationRepository::find_by_code( 'v-bilbao' )['id'];

delete_transient( 'orp_fallback_notice' );

// Shipping «Alcalá» con acento coincide con la regla «alcala» (normalización);
// el billing «Sevilla» no debe intervenir: hay shipping y basta con él.
// Nota F1-05: wc_create_order dispara el trigger con los datos aún vacíos
// (cae al default y deja SU aviso); se limpia para comprobar esta resolución.
$order = wc_create_order();
$order->set_shipping_city( 'Alcalá' );
$order->set_billing_city( 'Sevilla' );
$order->save();
delete_transient( 'orp_fallback_notice' );

$resolved = LocationResolver::resolve_for_order( $order );
if ( null !== $resolved && 'v-alcala' === $resolved->code ) {
	ok( 'shipping primero + acentos: «Alcalá» resuelve al local de Alcalá aunque billing sea Sevilla' );
} else {
	bad( 'resolución shipping/acentos falló: ' . ( $resolved ? $resolved->code : 'null' ) );
}

// El aviso de fallback no debe existir: hubo regla coincidente.
if ( false === get_transient( 'orp_fallback_notice' ) ) {
	ok( 'sin aviso de fallback cuando una regla coincide' );
} else {
	bad( 'apareció aviso de fallback sin motivo' );
}

// Ciudad única sin reglas: debe caer al local por defecto (el que esté
// marcado, que en la siembra base es local-centro). La creación ya disparó
// el trigger con datos vacíos: se limpia antes de la resolución explícita
// (el aviso por pedido entra UNA vez en la cola desde el cambio F1-05).
$expected_default = LocationResolver::default_location();
$order2 = wc_create_order();
$order2->set_shipping_city( 'Xirivella' );
$order2->save();
delete_transient( 'orp_fallback_notice' );

$resolved2 = LocationResolver::resolve_for_order( $order2 );
if ( null !== $expected_default && null !== $resolved2 && (int) $expected_default->id === (int) $resolved2->id ) {
	ok( 'sin regla coincidente: cae al local por defecto (' . $resolved2->code . ')' );
} else {
	bad( 'fallback al local por defecto falló: ' . ( $resolved2 ? $resolved2->code : 'null' ) );
}

$notices = LocationResolver::take_fallback_notices();
if ( 1 === count( $notices ) && (int) $notices[0]['order_id'] === $order2->get_id() ) {
	ok( 'aviso admin_notices en cola para el pedido ' . $order2->get_id() . ' (y se consume una vez)' );
} else {
	bad( 'aviso de fallback mal registrado: ' . wp_json_encode( $notices ) );
}

/* ------------------------------------------------ regla order_meta (código canónico) */
orp_insert_location( 'v-meta', 'Meta (verificación)', 5, wp_json_encode( array( array( 'type' => 'order_meta', 'operator' => 'in', 'values' => array( 'orp_local_code', 'local-a' ) ) ) ), 0 );

$order3 = wc_create_order();
$order3->set_shipping_city( 'Sevilla' );
$order3->update_meta_data( 'orp_local_code', 'local-a' );
$order3->save();
delete_transient( 'orp_fallback_notice' );

$resolved3 = LocationResolver::resolve_for_order( $order3 );
if ( null !== $resolved3 && 'v-meta' === $resolved3->code ) {
	ok( 'regla order_meta por la API CRUD (get_meta) con prioridad 5 gana a city' );
} else {
	bad( 'regla order_meta falló: ' . ( $resolved3 ? $resolved3->code : 'null' ) );
}

/* --------------------------------------------- locales inactivos no participan */
$wpdb->update( $wpdb->prefix . 'orp_locations', array( 'enabled' => 0 ), array( 'id' => $id_alcala ), array( '%d' ), array( '%d' ) );

$order4 = wc_create_order();
$order4->set_shipping_city( 'Alcalá' );
$order4->save();
delete_transient( 'orp_fallback_notice' );

$resolved4 = LocationResolver::resolve_for_order( $order4 );
if ( null !== $resolved4 && 'v-alcala' !== $resolved4->code ) {
	ok( 'local inactivo no participa en la resolución (cayó a ' . $resolved4->code . ')' );
} else {
	bad( 'local inactivo participó: ' . ( $resolved4 ? $resolved4->code : 'null' ) );
}
$wpdb->update( $wpdb->prefix . 'orp_locations', array( 'enabled' => 1 ), array( 'id' => $id_alcala ), array( '%d' ), array( '%d' ) );

/* --------------------------------------- sincronía local por defecto ↔ settings */
LocationRepository::set_default( $id_bilbao );
$row_settings = (int) Settings::get( 'default_location_id' );
if ( $id_bilbao === $row_settings ) {
	ok( 'set_default sincroniza orp_settings.default_location_id' );
} else {
	bad( "default_location_id=$row_settings, esperaba $id_bilbao" );
}

/* ------------------------------------------------------------ página de ajustes */
SettingsPage::register();
if ( has_action( 'admin_init', array( SettingsPage::class, 'maybe_save' ) ) ) {
	ok( 'SettingsPage registrada en admin_init (procesado antes de renderizar)' );
} else {
	bad( 'SettingsPage no está registrada' );
}

/* ---------------------------------------------------------------------- resumen */

// Deja el entorno como estaba: borra los locales de verificación y devuelve
// el local por defecto de la siembra.
$wpdb->query( "DELETE FROM {$wpdb->prefix}orp_locations WHERE code LIKE 'v-%'" );
$centro = LocationRepository::find_by_code( 'local-centro' );
if ( null !== $centro ) {
	LocationRepository::set_default( (int) $centro['id'] );
}

echo "==============================\n";
echo 'Resultado: ' . $GLOBALS['pass'] . " PASS / " . $GLOBALS['fail'] . " FAIL\n";
