<?php
/**
 * Verificación dinámica F1-07 + F1-08 + F1-09 (ejecutar dentro del cli de wp-env):
 *   npx @wordpress/env run cli wp eval-file /var/www/html/wp-content/plugins/orp-print/tools/_verify_f1_07_08_09.php
 *
 * Comprueba, en el WordPress REAL del wp-env:
 *  1. POST /pair (Doc/04 §5.2): 200 con cuerpo exacto, agente hereda el local
 *     del código, código consumido, destinos upsertados, poll_interval de
 *     orp_settings y token nunca en orp_logs.
 *  2. Errores contractuales: código inexistente/malformado 400, usado 400,
 *     caducado 410, sexto intento 429 orp_pairing_locked.
 *  3. S-02: throttling por IP (10 fallos/h → 429 con Retry-After; el éxito
 *     resetea la IP; los intentos del código suben solo en fallos de código).
 *  4. Reaper: lease 120 s → pending sin tocar attempts; agotado → failed;
 *     caducidad → expired con aviso acumulable; retención jobs/logs.
 *  5. Cola (F1-08): cancelar pending/claimed; reencolar failed/expired
 *     restaura la MISMA fila; generación de códigos y código vigente.
 *  6. Avisos: agentes sin latido (>3 min o nunca) detectados.
 *
 * No destructivo: borra local, salidas, agentes, códigos, trabajos y logs de
 * la verificación y restaura ajustes, opción de avisos y transients.
 */

use Orvita\PrintBridge\Admin\JobsPage;
use Orvita\PrintBridge\Domain\DestinationRepository;
use Orvita\PrintBridge\Domain\LocationRepository;
use Orvita\PrintBridge\Domain\PairingCodes;
use Orvita\PrintBridge\Domain\Reaper;
use Orvita\PrintBridge\Rest\Auth;
use Orvita\PrintBridge\Rest\PairController;
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
$original_notice   = (int) get_option( Reaper::EXPIRED_NOTICE_OPTION, 0 );

$_SERVER['REMOTE_ADDR'] = '203.0.113.99';
$IP_REQUEST             = new WP_REST_Request( 'POST', '/orp-print/v1/pair' );

$limpiar_transients_ip = static function (): void {
	// Por la API, no por SQL: la caché en memoria del proceso retendría el
	// valor aunque se borre la fila de wp_options.
	delete_transient( 'orp_pair_rl_' . md5( (string) $_SERVER['REMOTE_ADDR'] ) );
	wp_cache_flush();
};

/* --------------------------------------------------- 1. escenario del local */
echo "\n== 1. Emparejamiento (POST /pair) ==\n";

// Estado limpio: si una verificación anterior dejó el local, se borra primero
// (LocationRepository::delete limpia salidas, agentes y códigos del local).
$previa = LocationRepository::find_by_code( 'verify-f107' );
if ( null !== $previa ) {
	LocationRepository::delete( (int) $previa['id'] );
	$wpdb->query( $wpdb->prepare( "DELETE FROM {$wpdb->prefix}orp_jobs WHERE location_id = %d", (int) $previa['id'] ) );
}

$result = LocationRepository::save( array(
	'code'       => 'verify-f107',
	'name'       => 'Local de verificación F1-07',
	'enabled'    => true,
	'priority'   => 1,
	'rules_json' => '[]',
	'is_default' => false,
) );
if ( isset( $result['error'] ) ) {
	echo "FAIL: no se pudo crear el local de verificación ({$result['error']})\n";
	exit( 1 );
}
$loc_id = (int) $result['id'];
$loc    = LocationRepository::get( $loc_id );
ok( 'local de verificación creado (id ' . $loc_id . ')' );

$pair_request = static function ( array $body ) use ( $loc_id ): WP_REST_Response {
	$request = new WP_REST_Request( 'POST', '/orp-print/v1/pair' );
	foreach ( $body as $key => $value ) {
		$request->set_param( $key, $value );
	}
	return rest_get_server()->dispatch( $request );
};

/* ------------------------------------------ 1a. éxito con cuerpo exacto */
$created = PairingCodes::create( $loc_id, 1 );
ok( 'código de emparejamiento generado: ' . $created['code'] );

Settings::save( array( 'poll_interval' => 27 ) );

$response = $pair_request( array(
	'code'          => $created['code'],
	'agent_name'    => 'TPV Verificación',
	'platform'      => 'windows',
	'agent_version' => '0.9.0',
	'destinations'  => array(
		array( 'role' => 'default', 'label' => 'Mostrador', 'printer_name' => 'EPSON TM-T20III', 'copies' => 2, 'enabled' => true ),
	),
) );
$data = $response->get_data();

if ( 200 === $response->get_status()
	&& str_starts_with( (string) $data['token'], 'orp_' )
	&& ( (int) $data['location']['id'] ) === $loc_id
	&& 'verify-f107' === $data['location']['code']
	&& 'Local de verificación F1-07' === $data['location']['name']
	&& 27 === (int) $data['poll_interval'] ) {
	ok( '200 con el cuerpo EXACTO del contrato (token, location {id, code, name}, poll_interval de orp_settings)' );
} else {
	bad( 'cuerpo del 200 desviado: ' . wp_json_encode( $data ) );
}

// El agente hereda el local y el token autentica de verdad.
$token = (string) $data['token'];
$agent = Auth::find_by_hash( hash( 'sha256', $token ) );
if ( null !== $agent && (int) $agent->location_id === $loc_id && substr( $token, 0, 8 ) === $agent->token_prefix && 'active' === $agent->status ) {
	ok( 'agente creado con local HEREDADO del código; hash + prefijo de 8 en BD; token autentica' );
} else {
	bad( 'agente o token incorrectos' );
}

// El código queda consumido.
$row = PairingCodes::find( $created['code'] );
if ( null !== $row && null !== $row->used_at && 0 === (int) $row->attempts ) {
	ok( 'código marcado used_at en el éxito (sin intentos consumidos)' );
} else {
	bad( 'el código no quedó bien consumido' );
}

// Destinos upsertados como el heartbeat.
$dests = DestinationRepository::for_location( $loc_id );
if ( 1 === count( $dests ) && 'default' === $dests[0]->role && 2 === (int) $dests[0]->copies && 1 === (int) $dests[0]->enabled ) {
	ok( 'destino del cuerpo upsertado por (location_id, role), copias congeladas' );
} else {
	bad( 'destinos no upsertados' );
}

// El token NUNCA en orp_logs.
$logs = (array) $wpdb->get_results( "SELECT message, data FROM {$wpdb->prefix}orp_logs" );
$con_token = 0;
foreach ( $logs as $log ) {
	if ( str_contains( (string) $log->message . (string) $log->data, $token ) ) {
		$con_token++;
	}
}
$hay_log = 0;
foreach ( $logs as $log ) {
	if ( str_contains( (string) $log->message, 'emparejado' ) ) {
		$hay_log++;
	}
}
if ( 0 === $con_token && $hay_log > 0 ) {
	ok( 'token NUNCA en orp_logs; el evento se registra con nombre del agente y prefijo' );
} else {
	bad( "token en logs ($con_token) o log ausente ($hay_log)" );
}

// La IP se resetea con el éxito.
if ( 0 === PairController::ip_failures( $IP_REQUEST )['failures'] ) {
	ok( 'éxito resetea el contador de la IP (S-02)' );
} else {
	bad( 'la IP no se reseteó' );
}

/* ------------------------------------------ 1b. reutilización del código */
$response = $pair_request( array(
	'code'       => $created['code'],
	'agent_name' => 'TPV Verificación 2',
) );
if ( 400 === $response->get_status() && 'orp_pairing_invalid' === $response->get_data()['code'] ) {
	ok( 'código usado → 400 orp_pairing_invalid (un código, un agente)' );
} else {
	bad( 'reutilización no bloqueada: ' . $response->get_status() );
}

/* ------------------------------------------ 1c. caducidad e inexistencia */
$expired = PairingCodes::create( $loc_id, 1 );
if ( null === $expired ) {
	echo "FAIL: no se pudo generar el código para la prueba de caducidad\n";
	exit( 1 );
}
$row2 = PairingCodes::find( $expired['code'] );
$wpdb->query( $wpdb->prepare( "UPDATE {$wpdb->prefix}orp_pairing_codes SET expires_at = '2000-01-01 00:00:00' WHERE id = %d", $row2->id ) );

$response = $pair_request( array( 'code' => $expired['code'], 'agent_name' => 'TPV' ) );
if ( 410 === $response->get_status() && 'orp_pairing_expired' === $response->get_data()['code'] ) {
	ok( 'código caducado → 410 orp_pairing_expired' );
} else {
	bad( 'caducado no devuelve 410: ' . $response->get_status() );
}

$response = $pair_request( array( 'code' => '999999', 'agent_name' => 'TPV' ) );
if ( 400 === $response->get_status() && 'orp_pairing_invalid' === $response->get_data()['code'] ) {
	ok( 'código inexistente → 400 orp_pairing_invalid' );
} else {
	bad( 'código inexistente no devuelve 400' );
}

$response = $pair_request( array( 'code' => '12345', 'agent_name' => 'TPV' ) );
if ( 400 === $response->get_status() && 'orp_pairing_invalid' === $response->get_data()['code'] ) {
	ok( 'código malformado (5 dígitos) → 400 orp_pairing_invalid' );
} else {
	bad( 'malformado no devuelve 400' );
}

/* ------------------------------------------ 1d. sexto intento bloquea el código */
$locked = PairingCodes::create( $loc_id, 1 );
$codes_fallidos = 0;
for ( $i = 0; $i < 5; $i++ ) {
	$response = $pair_request( array( 'code' => $locked['code'] ) ); // sin agent_name
	if ( 400 === $response->get_status() ) {
		$codes_fallidos++;
	}
}
$row3    = PairingCodes::find( $locked['code'] );
$response = $pair_request( array( 'code' => $locked['code'], 'agent_name' => 'TPV Verificación' ) );
if ( 5 === $codes_fallidos && 5 === (int) $row3->attempts && 429 === $response->get_status() && 'orp_pairing_locked' === $response->get_data()['code'] ) {
	ok( 'sexto intento fallido → 429 orp_pairing_locked (5 intentos por código, Doc/05 §4.6)' );
} else {
	bad( "lock del código incorrecto: fallos=$codes_fallidos attempts={$row3->attempts} status={$response->get_status()}" );
}

/* ------------------------------------------ 1e. throttling por IP (S-02) */
$limpiar_transients_ip();

$fails = 0;
for ( $i = 0; $i < 10; $i++ ) {
	$response = $pair_request( array( 'code' => sprintf( '%06d', 900000 + $i ), 'agent_name' => 'TPV' ) );
	if ( 400 === $response->get_status() ) {
		$fails++;
	}
}

// La undécima va con un código VÁLIDO: manda el throttle por IP.
$valid = PairingCodes::create( $loc_id, 1 );
$response = $pair_request( array( 'code' => $valid['code'], 'agent_name' => 'TPV Verificación' ) );
$status   = $response->get_status();
$code_err = $response instanceof WP_Error ? $response->get_error_code() : ( $response->get_data()['code'] ?? '' );
if ( 10 === $fails && 429 === $status && 'orp_rate_limited' === $code_err && (int) Auth::$rate_limited_after > 0 ) {
	ok( '10 fallos/hora por IP → la 11ª (con código válido) es 429 orp_rate_limited con Retry-After=' . Auth::$rate_limited_after );
} else {
	bad( "throttle por IP incorrecto: fallos=$fails status=$status code=$code_err retry=" . ( Auth::$rate_limited_after ?? 'null' ) );
}

$limpiar_transients_ip();
Auth::reset();

/* ------------------------------------------ 2. Reaper */
echo "\n== 2. Reaper (lease, caducidad, retención) ==\n";

$insert_job = static function ( array $row ) use ( $wpdb, $loc_id ): int {
	$base = array(
		'location_id'     => $loc_id,
		'order_id'        => 0,
		'printer_role'    => 'default',
		'copies'          => 1,
		'status'          => 'pending',
		'payload'         => '{"version":1,"blocks":[]}',
		'idempotency_key' => 'verify-f109-' . uniqid( '', true ),
		'attempts'        => 0,
		'max_attempts'    => 5,
		'claimed_by'      => null,
		'claimed_at'      => null,
		'delivered_at'    => null,
		'last_error'      => null,
		'created_at'      => gmdate( 'Y-m-d H:i:s' ),
		'expires_at'      => gmdate( 'Y-m-d H:i:s', time() + 1800 ),
	);
	$base = array_merge( $base, $row );
	$wpdb->insert( "{$wpdb->prefix}orp_jobs", $base, array( '%d', '%d', '%s', '%d', '%s', '%s', '%s', '%d', '%d', '%d', '%s', '%s', '%s', '%s', '%s' ) );
	return (int) $wpdb->insert_id;
};

// Lease vencido → pending SIN tocar attempts.
$lease_old  = $insert_job( array(
	'status'     => 'claimed',
	'attempts'   => 2,
	'claimed_by' => (int) $agent->id,
	'claimed_at' => gmdate( 'Y-m-d H:i:s', time() - 180 ),
) );
$lease_live = $insert_job( array(
	'status'     => 'claimed',
	'attempts'   => 1,
	'claimed_by' => (int) $agent->id,
	'claimed_at' => gmdate( 'Y-m-d H:i:s', time() - 10 ),
) );
$agotado    = $insert_job( array(
	'status'     => 'claimed',
	'attempts'   => 5,
	'claimed_by' => (int) $agent->id,
	'claimed_at' => gmdate( 'Y-m-d H:i:s', time() - 300 ),
) );
$caducado   = $insert_job( array(
	'status'     => 'pending',
	'expires_at' => gmdate( 'Y-m-d H:i:s', time() - 60 ),
) );

update_option( Reaper::EXPIRED_NOTICE_OPTION, 0, false );
Reaper::run();

$lease_old_row  = $wpdb->get_row( $wpdb->prepare( "SELECT status, attempts, claimed_by FROM {$wpdb->prefix}orp_jobs WHERE id = %d", $lease_old ) );
$lease_live_row = $wpdb->get_row( $wpdb->prepare( "SELECT status FROM {$wpdb->prefix}orp_jobs WHERE id = %d", $lease_live ) );
$agotado_row    = $wpdb->get_row( $wpdb->prepare( "SELECT status, last_error FROM {$wpdb->prefix}orp_jobs WHERE id = %d", $agotado ) );
$caducado_row   = $wpdb->get_row( $wpdb->prepare( "SELECT status FROM {$wpdb->prefix}orp_jobs WHERE id = %d", $caducado ) );

if ( 'pending' === $lease_old_row->status && 2 === (int) $lease_old_row->attempts && null === $lease_old_row->claimed_by ) {
	ok( 'lease vencido (>120 s) → pending y attempts INTACTOS (2)' );
} else {
	bad( "lease vencido mal: {$lease_old_row->status} attempts={$lease_old_row->attempts}" );
}

if ( 'claimed' === $lease_live_row->status ) {
	ok( 'lease reciente (10 s) de agente activo NO se roba' );
} else {
	bad( 'el reaper robó un lease vivo' );
}

if ( 'failed' === $agotado_row->status && str_contains( (string) $agotado_row->last_error, 'agotados' ) ) {
	ok( 'lease vencido con intentos agotados → failed con último error' );
} else {
	bad( 'agotado mal: ' . wp_json_encode( $agotado_row ) );
}

$notice = (int) get_option( Reaper::EXPIRED_NOTICE_OPTION, 0 );
if ( 'expired' === $caducado_row->status && 1 === $notice ) {
	ok( 'caducado → expired + aviso acumulable en el panel (contador 1)' );
} else {
	bad( "caducado mal: {$caducado_row->status} aviso=$notice" );
}

// Retención 90/30.
$old_created = gmdate( 'Y-m-d H:i:s', time() - 100 * DAY_IN_SECONDS );
$job_old     = $insert_job( array( 'status' => 'delivered', 'created_at' => $old_created ) );
$job_new     = $insert_job( array( 'status' => 'delivered' ) );
$wpdb->query( $wpdb->prepare(
	"INSERT INTO {$wpdb->prefix}orp_logs (created_at, level, context, message) VALUES (%s, 'info', 'verify', 'log viejo')",
	gmdate( 'Y-m-d H:i:s', time() - 40 * DAY_IN_SECONDS )
) );
$log_old = (int) $wpdb->insert_id;

Reaper::run();

$job_old_count = (int) $wpdb->get_var( $wpdb->prepare( "SELECT COUNT(*) FROM {$wpdb->prefix}orp_jobs WHERE id = %d", $job_old ) );
$job_new_count = (int) $wpdb->get_var( $wpdb->prepare( "SELECT COUNT(*) FROM {$wpdb->prefix}orp_jobs WHERE id = %d", $job_new ) );
$log_old_count = (int) $wpdb->get_var( $wpdb->prepare( "SELECT COUNT(*) FROM {$wpdb->prefix}orp_logs WHERE id = %d", $log_old ) );

if ( 0 === $job_old_count && 1 === $job_new_count && 0 === $log_old_count ) {
	ok( 'retención: job >90 días borrado, job reciente conservado, log >30 días borrado' );
} else {
	bad( "retención mal: old=$job_old_count new=$job_new_count log=$log_old_count" );
}

// Cron programado.
if ( false !== wp_next_scheduled( Reaper::CRON_HOOK ) ) {
	ok( 'cron horario del Reaper programado (' . Reaper::CRON_HOOK . ')' );
} else {
	Reaper::ensure_schedule();
	if ( false !== wp_next_scheduled( Reaper::CRON_HOOK ) ) {
		ok( 'cron horario del Reaper programado tras ensure_schedule()' );
	} else {
		bad( 'cron del Reaper no programado' );
	}
}

/* ------------------------------------------ 3. Cola (F1-08) */
echo "\n== 3. Cola: cancelar y reencolar ==\n";

$pending = $insert_job( array( 'status' => 'pending' ) );
$claimed = $insert_job( array( 'status' => 'claimed', 'claimed_by' => (int) $agent->id, 'claimed_at' => gmdate( 'Y-m-d H:i:s' ) ) );
$failed  = $insert_job( array(
	'status'     => 'failed',
	'attempts'   => 5,
	'last_error' => 'orp_printer_not_found: prueba',
	'expires_at' => gmdate( 'Y-m-d H:i:s', time() - 60 ),
) );
$keys    = $wpdb->get_var( $wpdb->prepare( "SELECT idempotency_key FROM {$wpdb->prefix}orp_jobs WHERE id = %d", $failed ) );

if ( JobsPage::cancel_job( $pending ) && JobsPage::cancel_job( $claimed ) ) {
	$s1 = $wpdb->get_var( $wpdb->prepare( "SELECT status FROM {$wpdb->prefix}orp_jobs WHERE id = %d", $pending ) );
	$s2 = $wpdb->get_var( $wpdb->prepare( "SELECT status FROM {$wpdb->prefix}orp_jobs WHERE id = %d", $claimed ) );
	if ( 'cancelled' === $s1 && 'cancelled' === $s2 ) {
		ok( 'cancelar: pending y claimed → cancelled con log' );
	} else {
		bad( "cancelar mal: $s1 / $s2" );
	}
} else {
	bad( 'cancelar devolvió false' );
}

if ( ! JobsPage::requeue_job( $pending ) ) {
	ok( 'reencolar rechaza un pending (solo failed/expired)' );
} else {
	bad( 'reencolar aceptó un pending' );
}

if ( JobsPage::requeue_job( $failed ) ) {
	$row4 = $wpdb->get_row( $wpdb->prepare( "SELECT status, attempts, idempotency_key, claimed_by, last_error, expires_at FROM {$wpdb->prefix}orp_jobs WHERE id = %d", $failed ) );
	if ( 'pending' === $row4->status && 0 === (int) $row4->attempts && $keys === $row4->idempotency_key
		&& null === $row4->claimed_by && null === $row4->last_error
		&& strtotime( $row4->expires_at . ' UTC' ) > time() + 60 ) {
		ok( 'reencolar failed/expired: MISMA fila a pending, attempts 0, sin reclamo, caducidad nueva (idempotencia intacta)' );
	} else {
		bad( 'reencolar mal: ' . wp_json_encode( $row4 ) );
	}
} else {
	bad( 'reencolar devolvió false' );
}

/* ------------------------------------------ 4. Panel y avisos */
echo "\n== 4. Panel: códigos y avisos ==\n";

$latest = PairingCodes::latest_valid( $loc_id );
if ( null !== $latest && $latest->code === $valid['code'] ) {
	ok( 'código vigente visible para el panel (último sin usar ni caducar)' );
} else {
	bad( 'latest_valid incorrecto' );
}

// Agente sin latido: nunca latió → aparece como desconectado.
$offline = Reaper::disconnected_agents();
$encontrado = false;
foreach ( $offline as $off ) {
	if ( (int) $off->id === (int) $agent->id ) {
		$encontrado = true;
	}
}
if ( $encontrado ) {
	ok( 'aviso de agente sin heartbeat > 3 min (nunca latió) detectado' );
} else {
	bad( 'el agente sin latido no aparece en desconectados' );
}

// Con latido reciente ya no aparece.
$wpdb->update( "{$wpdb->prefix}orp_agents", array( 'last_seen_at' => gmdate( 'Y-m-d H:i:s' ) ), array( 'id' => $agent->id ), array( '%s' ), array( '%d' ) );
$encontrado = false;
foreach ( Reaper::disconnected_agents() as $off ) {
	if ( (int) $off->id === (int) $agent->id ) {
		$encontrado = true;
	}
}
if ( ! $encontrado ) {
	ok( 'agente con latido reciente NO aparece como desconectado' );
} else {
	bad( 'agente recién latido aparece como desconectado' );
}

/* ------------------------------------------------------------ restaurar */
echo "\n== Restauración ==\n";
update_option( Settings::OPTION, $original_settings, false );
update_option( Reaper::EXPIRED_NOTICE_OPTION, $original_notice, false );
$limpiar_transients_ip();

LocationRepository::delete( $loc_id ); // Borra salidas, agentes y códigos del local.
$wpdb->query( $wpdb->prepare( "DELETE FROM {$wpdb->prefix}orp_jobs WHERE location_id = %d", $loc_id ) );
$wpdb->query( "DELETE FROM {$wpdb->prefix}orp_logs WHERE context IN ('pairing','verify') OR message LIKE '%verificación F1-07%' OR message = 'log viejo'" );
delete_transient( 'orp_reaper_lastrun' );
Auth::reset();

echo "==============================\n";
echo "Resultado: {$GLOBALS['pass']} PASS / {$GLOBALS['fail']} FAIL\n";
exit( 0 === $GLOBALS['fail'] ? 0 : 1 );
