<?php
/**
 * Verificación dinámica F1-05 + F1-06 (ejecutar dentro del cli de wp-env):
 *   npx @wordpress/env run cli wp eval-file /var/www/html/wp-content/plugins/orp-print/tools/_verify_f1_05_06.php
 *
 * Comprueba con WooCommerce REAL (pedidos vía API CRUD/HPOS):
 * 1. Pedido creado → un trabajo por salida activa del local (fan-out) con
 *    copias congeladas.
 * 2. Doble disparo del checkout (Store API + clásico + new_order): la dedupe
 *    deja exactamente los mismos trabajos.
 * 3. dedupe_scope=trigger: cambio al estado configurado → trabajos nuevos;
 *    scope=order → ninguno.
 * 4. Cancelación del pedido → pendientes a cancelled (Doc/05 §4.1).
 * 5. Desactivar una salida (heartbeat del agente): pendientes cancelled y los
 *    pedidos nuevos ya no generan trabajo para ella.
 * 6. Cambiar las copias de una salida no altera los pendientes (congeladas).
 * 7. Cambiar la regla del local no altera los trabajos ya encolados.
 * 8. Sin local por defecto: no se crean trabajos, log + aviso (nunca en
 *    silencio); el aviso entra en cola para el panel.
 *
 * No destructivo: restaura ajustes, local por defecto y borra el local,
 * salidas, pedidos, productos y trabajos de la verificación.
 *
 * Sin declare(strict_types=1): wp eval-file evalúa el fichero y exige que
 * sea la primera sentencia absoluta del script.
 */

use Orvita\PrintBridge\Domain\DestinationRepository;
use Orvita\PrintBridge\Domain\LocationRepository;
use Orvita\PrintBridge\Support\Settings;

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

global $wpdb;

/* ---------------------------------------------------------------- respaldo */
$original_settings = get_option( Settings::OPTION, array() );
// El default tiene dos lados (tabla y opción): se respaldan ambos para
// devolver el entorno exacto (la siembra puede tener la opción a 0 con el
// is_default marcado en la tabla).
$original_default_rows = array_map( 'intval', (array) $wpdb->get_col(
	"SELECT id FROM {$wpdb->prefix}orp_locations WHERE is_default = 1"
) );
$original_logs     = (int) $wpdb->get_var( "SELECT COUNT(*) FROM {$wpdb->prefix}orp_logs" );

/* ------------------------------------------------- 1. escenario del piloto */
echo "\n== 1. Fan-out: un trabajo por salida activa, copias congeladas ==\n";

// Local propio de la verificación con una ciudad que no usan los locales
// sembrados (aún sin regla: se añade vía save() en el paso 7).
$location_result = LocationRepository::save(
	array(
		'code'       => 'verify-f105',
		'name'       => 'Verificación F1-05/06',
		'enabled'    => true,
		'priority'   => 1,
		'rules_json' => wp_json_encode(
			array(
				array(
					'type'     => 'city',
					'operator' => 'in',
					'values'   => array( 'Verificópolis' ),
				),
			)
		),
		'is_default' => false,
	)
);
if ( ! isset( $location_result['error'] ) ) {
	ok( 'local de verificación creado (regla city = Verificópolis)' );
} else {
	bad( 'no se pudo crear el local de verificación: ' . $location_result['error'] );
	exit( 1 );
}
$location_id = (int) $location_result['id'];

// Dos salidas como las reportaría el agente (heartbeat).
DestinationRepository::upsert(
	$location_id,
	array( 'role' => 'default', 'label' => 'Mostrador', 'printer_name' => 'EPSON-VERIF-A', 'copies' => 1, 'enabled' => true )
);
DestinationRepository::upsert(
	$location_id,
	array( 'role' => 'cocina', 'label' => 'Cocina', 'printer_name' => 'XPRINTER-VERIF', 'copies' => 2, 'enabled' => true )
);

Settings::save(
	array(
		'trigger_states'      => array( 'created', 'processing' ),
		'dedupe_scope'        => 'order',
		'default_location_id' => (int) $location_id,
	)
);

// Pedido con los datos ya puestos al crearlo (como en el checkout real).
$order = new WC_Order();
$order->set_shipping_city( 'Verificópolis' );
$order->set_billing_first_name( 'María' );
$order->set_billing_last_name( 'López' );
$order->set_billing_phone( '600 000 000' );
$order->set_shipping_address_1( 'Calle Verificación 1' );
$order->set_status( 'pending' );
$order->save();
$order_id = $order->get_id();

$jobs = (array) $wpdb->get_results( $wpdb->prepare(
	"SELECT id, printer_role, copies, status FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d AND location_id = %d ORDER BY id",
	$order_id,
	$location_id
) );

if ( 2 === count( $jobs ) ) {
	ok( 'pedido con dos salidas activas → dos trabajos (fan-out F1-06)' );
} else {
	bad( 'se esperaban 2 trabajos, hay ' . count( $jobs ) );
}

$copies = array();
foreach ( $jobs as $job ) {
	$copies[ $job->printer_role ] = (int) $job->copies;
}
if ( 1 === ( $copies['default'] ?? 0 ) && 2 === ( $copies['cocina'] ?? 0 ) ) {
	ok( 'cada trabajo lleva las copias de SU salida congeladas (1 y 2)' );
} else {
	bad( 'las copias congeladas no corresponden: ' . wp_json_encode( $copies ) );
}

$all_pending = true;
foreach ( $jobs as $job ) {
	if ( 'pending' !== $job->status ) {
		$all_pending = false;
	}
}
if ( $all_pending ) {
	ok( 'los trabajos nacen pending' );
} else {
	bad( 'algún trabajo nació en un estado inesperado' );
}

// El payload de cada salida lleva su meta (copies, printer_role) y el mismo
// documento base (V1: todas las salidas imprimen el mismo documento).
$payloads = $wpdb->get_col( $wpdb->prepare(
	"SELECT payload FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d AND location_id = %d ORDER BY id",
	$order_id,
	$location_id
) );
$roles_in_meta = 0;
foreach ( $payloads as $payload ) {
	$decoded = json_decode( (string) $payload, true );
	if ( is_array( $decoded ) && isset( $decoded['meta']['printer_role'], $decoded['meta']['copies'] ) ) {
		$roles_in_meta++;
	}
}
if ( 2 === $roles_in_meta ) {
	ok( 'el payload lleva meta.printer_role y meta.copies de la salida' );
} else {
	bad( 'los payload no llevan la meta de la salida' );
}

// Nota en el pedido del disparo automático.
$notes = (array) wc_get_order_notes( array( 'order_id' => $order_id ) );
$note_found = false;
foreach ( $notes as $note ) {
	if ( false !== strpos( (string) $note->content, 'Orvita Print: trabajos' ) ) {
		$note_found = true;
	}
}
if ( $note_found ) {
	ok( 'el disparo dejó nota en el pedido con los trabajos encolados' );
} else {
	bad( 'sin nota del disparo automático en el pedido' );
}

/* -------------------------------------------------- 2. doble disparo blocks */
echo "\n== 2. Doble disparo del checkout de bloques: la dedupe lo funde ==\n";

do_action( 'woocommerce_store_api_checkout_order_processed', $order );
do_action( 'woocommerce_checkout_order_processed', $order_id, array(), $order );

$jobs_after_double = (int) $wpdb->get_var( $wpdb->prepare(
	"SELECT COUNT(*) FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d AND location_id = %d",
	$order_id,
	$location_id
) );
if ( 2 === $jobs_after_double ) {
	ok( 'Store API + checkout clásico + creación: sigue habiendo 2 trabajos (no 6)' );
} else {
	bad( "el doble disparo duplicó trabajos: hay $jobs_after_double (se esperaban 2)" );
}

/* ------------------------------------- 3. dedupe_scope trigger vs order */
echo "\n== 3. dedupe_scope = trigger: cambio de estado dispara; scope = order no ==\n";

Settings::save( array( 'dedupe_scope' => 'trigger' ) );
$order->update_status( 'processing' );

$jobs_processing = (array) $wpdb->get_results( $wpdb->prepare(
	"SELECT printer_role, idempotency_key FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d AND location_id = %d ORDER BY id",
	$order_id,
	$location_id
) );

if ( 4 === count( $jobs_processing ) ) {
	ok( 'scope=trigger: pasar a processing (estado configurado) crea 2 trabajos nuevos' );
} else {
	bad( 'scope=trigger no añadió los trabajos del disparo por estado: ' . count( $jobs_processing ) );
}

// Las claves nuevas llevan el trigger del estado (Doc/04 §3).
$tv        = (int) get_option( 'orp_template_version', 1 );
$expected  = hash( 'sha256', $order_id . '|processing|' . $location_id . '|default|' . $tv );
$found_key = false;
foreach ( $jobs_processing as $job ) {
	if ( hash_equals( $expected, (string) $job->idempotency_key ) ) {
		$found_key = true;
	}
}
if ( $found_key ) {
	ok( 'la clave del disparo por estado lleva el trigger (processing) en la clave' );
} else {
	bad( 'ninguna clave nueva corresponde a sha256(order|processing|local|role|tv)' );
}

Settings::save( array( 'dedupe_scope' => 'order' ) );

// Pedido B para demostrar el scope=order con un estado CONFIGURADO: la
// creación ya puso 2 trabajos y el cambio a processing no añade ninguno.
$order_b = new WC_Order();
$order_b->set_shipping_city( 'Verificópolis' );
$order_b->save();
$order_b_id = $order_b->get_id();

$jobs_b = (int) $wpdb->get_var( $wpdb->prepare(
	"SELECT COUNT(*) FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d AND location_id = %d",
	$order_b_id,
	$location_id
) );
if ( 2 === $jobs_b ) {
	ok( 'pedido B: 2 trabajos (uno por salida)' );
} else {
	bad( "pedido B esperaba 2 trabajos: hay $jobs_b" );
}

$order_b->update_status( 'processing' );

$jobs_b_after = (int) $wpdb->get_var( $wpdb->prepare(
	"SELECT COUNT(*) FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d AND location_id = %d",
	$order_b_id,
	$location_id
) );
if ( 2 === $jobs_b_after ) {
	ok( 'scope=order: pasar a processing (estado configurado) NO crea trabajos nuevos (siguen 2)' );
} else {
	bad( "scope=order añadió trabajos: hay $jobs_b_after" );
}

/* ----------------------------------------------- 4. cancelación del pedido */
echo "\n== 4. Pedido anulado antes de imprimir → pending a cancelled ==\n";

$order->update_status( 'cancelled' );

$jobs_cancelled = (array) $wpdb->get_col( $wpdb->prepare(
	"SELECT status FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d AND location_id = %d",
	$order_id,
	$location_id
) );
$all_cancelled = ( 4 === count( $jobs_cancelled ) && 0 === count( array_diff( $jobs_cancelled, array( 'cancelled' ) ) ) );
if ( $all_cancelled ) {
	ok( 'los 4 pendientes del pedido anulado quedan cancelled, sin papel' );
} else {
	bad( 'estados tras la cancelación: ' . wp_json_encode( $jobs_cancelled ) );
}

/* --------------------------------- 5. desactivar una salida (heartbeat) */
echo "\n== 5. Desactivar una salida: pendientes cancelled y sin trabajos nuevos ==\n";

// (pedido B ya está en cola con las dos salidas activas)

// El agente reporta la salida de cocina desactivada en su heartbeat.
DestinationRepository::upsert(
	$location_id,
	array( 'role' => 'cocina', 'label' => 'Cocina', 'printer_name' => 'XPRINTER-VERIF', 'copies' => 2, 'enabled' => false )
);

$states_b = (array) $wpdb->get_results( $wpdb->prepare(
	"SELECT printer_role, status FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d AND location_id = %d ORDER BY id",
	$order_b_id,
	$location_id
) );
$by_role = array();
foreach ( $states_b as $row ) {
	$by_role[ $row->printer_role ] = $row->status;
}
if ( ( $by_role['default'] ?? '' ) === 'pending' && ( $by_role['cocina'] ?? '' ) === 'cancelled' ) {
	ok( 'desactivar la salida cancela SOLO sus pendientes; la otra salida sigue pending' );
} else {
	bad( 'estados por rol tras desactivar: ' . wp_json_encode( $by_role ) );
}

// Pedidos nuevos ya no generan trabajo para la salida desactivada.
$order_c = new WC_Order();
$order_c->set_shipping_city( 'Verificópolis' );
$order_c->save();
$order_c_id = $order_c->get_id();

$jobs_c = (array) $wpdb->get_col( $wpdb->prepare(
	"SELECT printer_role FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d AND location_id = %d",
	$order_c_id,
	$location_id
) );
if ( 1 === count( $jobs_c ) && 'default' === $jobs_c[0] ) {
	ok( 'pedido C: solo 1 trabajo (la salida desactivada ya no recibe)' );
} else {
	bad( 'pedido C tras desactivar la salida: ' . wp_json_encode( $jobs_c ) );
}

/* -------------------------------------- 6. copias congeladas vs pendientes */
echo "\n== 6. Cambiar copias de la salida: pendientes intactos, nuevos usan el valor ==\n";

DestinationRepository::upsert(
	$location_id,
	array( 'role' => 'default', 'label' => 'Mostrador', 'printer_name' => 'EPSON-VERIF-A', 'copies' => 5, 'enabled' => true )
);

$pending_copies = (int) $wpdb->get_var( $wpdb->prepare(
	"SELECT copies FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d AND location_id = %d AND printer_role = 'default'",
	$order_b_id,
	$location_id
) );
if ( 1 === $pending_copies ) {
	ok( 'el trabajo pendiente conserva sus copias originales (1) tras subir la salida a 5' );
} else {
	bad( "las copias del pendiente cambiaron: $pending_copies" );
}

$order_d = new WC_Order();
$order_d->set_shipping_city( 'Verificópolis' );
$order_d->save();
$order_d_id = $order_d->get_id();

$new_copies = (int) $wpdb->get_var( $wpdb->prepare(
	"SELECT copies FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d AND location_id = %d AND printer_role = 'default'",
	$order_d_id,
	$location_id
) );
if ( 5 === $new_copies ) {
	ok( 'un pedido nuevo usa las copias vigentes (5)' );
} else {
	bad( "el pedido nuevo no usó las copias vigentes: $new_copies" );
}

/* ------------------------------------- 7. cambiar la regla del local después */
echo "\n== 7. Cambiar la regla del local no altera los trabajos ya encolados ==\n";

$before_jobs = (array) $wpdb->get_results( $wpdb->prepare(
	"SELECT id, idempotency_key, payload FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d AND location_id = %d ORDER BY id",
	$order_b_id,
	$location_id
) );

// La misma vía que el panel: UPDATE del local (y de paso ejercita el fix del
// default único en UPDATE marcando este local como default).
LocationRepository::save(
	array(
		'id'         => $location_id,
		'code'       => 'verify-f105',
		'name'       => 'Verificación F1-05/06 (renombrado)',
		'enabled'    => true,
		'priority'   => 1,
		'rules_json' => wp_json_encode(
			array(
				array(
					'type'     => 'city',
					'operator' => 'in',
					'values'   => array( 'Pueblo Imposible' ),
				),
			)
		),
		'is_default' => true,
	)
);

$after_jobs = (array) $wpdb->get_results( $wpdb->prepare(
	"SELECT id, idempotency_key, payload FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d AND location_id = %d ORDER BY id",
	$order_b_id,
	$location_id
) );

$same_jobs = ( count( $before_jobs ) === count( $after_jobs ) );
foreach ( $before_jobs as $index => $before_job ) {
	$after = $after_jobs[ $index ] ?? null;
	if ( null === $after || $after->id !== $before_job->id || $after->idempotency_key !== $before_job->idempotency_key ) {
		$same_jobs = false;
	}
}
if ( $same_jobs ) {
	ok( 'los trabajos encolados quedan intactos tras editar la regla del local' );
} else {
	bad( 'los trabajos encolados cambiaron al editar la regla' );
}

// El default único también quedó sincronizado (fix del tester en UPDATE).
if ( (int) Settings::get( 'default_location_id' ) === $location_id ) {
	ok( 'save() en UPDATE sincroniza orp_settings.default_location_id (fix del tester)' );
} else {
	bad( 'la opción de default no se sincronizó en el UPDATE' );
}

/* ---------------------------------- 8. sin local por defecto: nunca silencio */
echo "\n== 8. Sin local por defecto: log + aviso, sin trabajos ==\n";

// Guardar «Ninguno» limpia el is_default huérfano de TODA la tabla (incluido
// el local-centro sembrado): eso es lo que pedía el hallazgo del tester.
LocationRepository::clear_default();

$orphans = (int) $wpdb->get_var( "SELECT COUNT(*) FROM {$wpdb->prefix}orp_locations WHERE is_default = 1" );
if ( 0 === $orphans && 0 === (int) Settings::get( 'default_location_id' ) ) {
	ok( '«Ninguno» limpia is_default en la tabla y la opción (sincronía completa)' );
} else {
	bad( "tras «Ninguno» quedan $orphans locales marcados como default" );
}

// Cambia la regla a una ciudad imposible: el pedido ya no resuelve.
LocationRepository::save(
	array(
		'id'         => $location_id,
		'code'       => 'verify-f105',
		'name'       => 'Verificación F1-05/06 (renoruteado)',
		'enabled'    => true,
		'priority'   => 1,
		'rules_json' => wp_json_encode(
			array(
				array(
					'type'     => 'city',
					'operator' => 'in',
					'values'   => array( 'Pueblo Imposible' ),
				),
			)
		),
		'is_default' => false,
	)
);

$order_e = new WC_Order();
$order_e->set_shipping_city( 'Verificópolis' );
$order_e->save();
$order_e_id = $order_e->get_id();

$jobs_e = (int) $wpdb->get_var( $wpdb->prepare(
	"SELECT COUNT(*) FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d",
	$order_e_id
) );
if ( 0 === $jobs_e ) {
	ok( 'pedido sin local asignable y sin default: 0 trabajos (no se encola a ciegas)' );
} else {
	bad( "el pedido sin local generó $jobs_e trabajos" );
}

$warned = (int) $wpdb->get_var( $wpdb->prepare(
	"SELECT COUNT(*) FROM {$wpdb->prefix}orp_logs WHERE context = 'locations' AND level = 'warning' AND message LIKE %s AND id > %d",
	'%' . $wpdb->esc_like( 'sin local por defecto' ) . '%',
	$original_logs
) );
if ( $warned >= 1 ) {
	ok( 'el aviso quedó registrado en orp_logs (nunca en silencio)' );
} else {
	bad( 'no hay registro de la caída sin local por defecto' );
}

$missing = get_transient( 'orp_missing_default_notice' );
if ( is_array( $missing ) && count( $missing ) >= 1 ) {
	ok( 'el aviso entró en cola para el panel (transient de pedidos sin default)' );
} else {
	bad( 'sin transient de aviso para el panel' );
}

// Sin salidas activas: tampoco se crean trabajos (y queda registro).
$wpdb->query( $wpdb->prepare( "UPDATE {$wpdb->prefix}orp_destinations SET enabled = 0 WHERE location_id = %d", $location_id ) );
$order_f = new WC_Order();
$order_f->set_shipping_city( 'Pueblo Imposible' );
$order_f->save();
$jobs_f = (int) $wpdb->get_var( $wpdb->prepare( "SELECT COUNT(*) FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d", $order_f->get_id() ) );
if ( 0 === $jobs_f ) {
	ok( 'local sin salidas activas: ningún trabajo (la cola no crece sin agente)' );
} else {
	bad( "el pedido en un local sin salidas generó $jobs_f trabajos" );
}

/* ------------------------------------------------------------- restauración */
echo "\n== Restauración del entorno ==\n";

foreach ( array( $order_id, $order_b_id, $order_c_id, $order_d_id, $order_e_id, (int) $order_f->get_id() ) as $delete_id ) {
	$order_to_delete = wc_get_order( $delete_id );
	if ( $order_to_delete instanceof WC_Order ) {
		$order_to_delete->delete( true );
	}
}

$wpdb->query( $wpdb->prepare( "DELETE FROM {$wpdb->prefix}orp_jobs WHERE location_id = %d", $location_id ) );
$wpdb->query( $wpdb->prepare( "DELETE FROM {$wpdb->prefix}orp_destinations WHERE location_id = %d", $location_id ) );
$wpdb->query( $wpdb->prepare( "DELETE FROM {$wpdb->prefix}orp_locations WHERE id = %d", $location_id ) );

// Local por defecto y ajustes tal como estaban (tabla y opción por separado).
update_option( Settings::OPTION, $original_settings, false );
if ( array() !== $original_default_rows ) {
	$in = implode( ',', $original_default_rows );
	$wpdb->query( "UPDATE {$wpdb->prefix}orp_locations SET is_default = CASE WHEN id IN ({$in}) THEN 1 ELSE 0 END" );
} else {
	$wpdb->query( "UPDATE {$wpdb->prefix}orp_locations SET is_default = 0" );
}

delete_transient( 'orp_missing_default_notice' );
delete_transient( 'orp_fallback_notice' );

echo "Ajustes, local por defecto, locales/salidas/pedidos de la verificación restaurados.\n";

echo "==============================\n";
echo "Resultado: {$GLOBALS['pass']} PASS / {$GLOBALS['fail']} FAIL\n";
exit( 0 );
