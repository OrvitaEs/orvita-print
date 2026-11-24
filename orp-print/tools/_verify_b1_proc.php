<?php
/**
 * Verificación B1 in-process: fuerza el 429 con 121 dispatches rápidos y
 * comprueba la cabecera Retry-After que el servidor emite (servidor espía
 * que graba send_header). Nota WP 6.8: rest_post_dispatch se aplica en
 * serve_request(), no en dispatch(), así que la aserción va sobre lo que
 * el servidor envía (lo que llega por HTTP real).
 *
 * Uso: npx @wordpress/env run cli wp eval-file .../_verify_b1_proc.php <TOKEN>
 */

if ( 'cli' !== php_sapi_name() ) {
	echo "Uso: wp eval-file .../_verify_b1_proc.php <TOKEN>\n";
	exit( 1 );
}

// wp eval-file pasa los argumentos posicionales tras el fichero (no usa $argc).
$token = isset( $args[0] ) ? (string) $args[0] : '';
if ( '' === $token ) {
	echo "Uso: wp eval-file .../_verify_b1_proc.php <TOKEN>\n";
	exit( 1 );
}

/* Servidor espía: graba las cabeceras en vez de emitirlas. */
class Orp_Spy_Server extends WP_REST_Server {
	/** Cabeceras "enviadas". */
	public array $sent = array();

	public function send_header( $header, $value = null ) {
		$this->sent[ $header ] = $value;
	}
}

$spy = new Orp_Spy_Server();
$GLOBALS['wp_rest_server'] = $spy;
do_action( 'rest_api_init', $spy );

// Ventana limpia.
global $wpdb;
$wpdb->query(
	"DELETE FROM {$wpdb->options}
	  WHERE option_name LIKE '\_transient\_orp\_rl\_%'
	     OR option_name LIKE '\_transient\_timeout\_orp\_rl\_%'"
);
wp_cache_flush();

$n200 = 0;
$n429 = 0;
$first_429 = 0;

for ( $i = 1; $i <= 125; $i++ ) {
	// En-process: cada dispatch sería una petición HTTP nueva; limpia la estática.
	Orvita\PrintBridge\Rest\Auth::reset();

	$request = new WP_REST_Request( 'GET', '/orp-print/v1/config' );
	$request->set_header( 'Authorization', 'Bearer ' . $token );

	$result = $spy->dispatch( $request );
	$status = $result instanceof WP_REST_Response ? $result->get_status() : 0;

	if ( 200 === $status ) {
		++$n200;
	} elseif ( 429 === $status ) {
		if ( 0 === $first_429 ) {
			$first_429 = $i;
		}
		++$n429;
	}
}

printf( "dispatches: 200 = %d, 429 = %d\n", $n200, $n429 );

$pass = true;

if ( 0 === $n429 ) {
	echo "FAIL: no se alcanzó el 429 en 125 dispatches.\n";
	$pass = false;
} else {
	if ( 121 === $first_429 ) {
		printf( "PASS: el 429 llega desde la petición %d (límite 120).\n", $first_429 );
	} else {
		printf( "AVISO: el primer 429 llegó en la petición %d (esperado 121).\n", $first_429 );
	}

	// La cabecera que el servidor enviaría por HTTP (serve_request = capa de envío real).
	Orvita\PrintBridge\Rest\Auth::reset();
	$_SERVER['HTTP_AUTHORIZATION'] = 'Bearer ' . $token;
	$spy->sent = array();
	ob_start();
	$spy->serve_request( '/orp-print/v1/config' );
	$body = ob_get_clean();

	$status_line = '';
	foreach ( array_keys( $spy->sent ) as $k ) {
		// Solo nos interesa Retry-After; el estado no se graba en sent.
	}
	$sent = $spy->sent['Retry-After'] ?? null;
	printf( "cabecera Retry-After que el servidor envía: %s\n", $sent ?? '(NO ENCONTRADA)' );

	if ( null === $sent || ! ctype_digit( (string) $sent ) || (int) $sent < 1 || (int) $sent > 60 ) {
		echo "FAIL: Retry-After ausente o fuera de rango.\n";
		$pass = false;
	}

	// El cuerpo del 429 mantiene el envoltorio estándar de WP (el contrato no lo define).
	$decoded = json_decode( $body, true );
	printf(
		"cuerpo del 429: code=%s status=%s\n",
		(string) ( $decoded['code'] ?? '?' ),
		(string) ( $decoded['data']['status'] ?? '?' )
	);
	if ( 'orp_rate_limited' !== ( $decoded['code'] ?? '' ) ) {
		echo "FAIL: el código del 429 no es orp_rate_limited.\n";
		$pass = false;
	}
}

echo $pass ? "RESULTADO: PASS\n" : "RESULTADO: FAIL\n";
exit( $pass ? 0 : 1 );
