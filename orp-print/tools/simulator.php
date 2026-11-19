<?php
/**
 * Orvita Print · Simulador de agente (herramienta de desarrollo, no el agente real).
 *
 * Recorre el contrato REST completo sin impresora (Doc/06 §9):
 *   1. heartbeat con las salidas de este equipo,
 *   2. poll con cursor por id,
 *   3. claim atómico,
 *   4. escribe el documento en var/jobs/<id>.json + versión de texto legible,
 *   5. ack delivered (o failed con --fail=<código>),
 *   6. tolera 409, 410 y 429 sin romperse.
 *
 * Requiere PHP 8.1+ con la extensión curl (o allow_url_fopen activado).
 *
 * Uso:
 *   php tools/simulator.php --url=http://localhost:8888 --token=TOKEN --once
 *   php tools/simulator.php --url=http://localhost:8888 --token=TOKEN --fail=orp_printer_not_found
 *   php tools/simulator.php --url=http://localhost:8888 --token=TOKEN --interval=5
 */

declare(strict_types=1);

error_reporting( E_ALL );

$opts = getopt( '', array( 'url::', 'token::', 'once', 'fail::', 'interval::' ) );

$base_url = isset( $opts['url'] ) ? rtrim( (string) $opts['url'], '/' ) : '';
$token    = isset( $opts['token'] ) ? trim( (string) $opts['token'] ) : '';
$once     = isset( $opts['once'] );
$fail_code = ( isset( $opts['fail'] ) && true !== $opts['fail'] ) ? (string) $opts['fail'] : null;
$interval = max( 2, isset( $opts['interval'] ) ? (int) $opts['interval'] : 10 );

if ( '' === $base_url || '' === $token ) {
	fwrite( STDERR, "Faltan parámetros. Uso:\n" );
	fwrite( STDERR, "  php tools/simulator.php --url=http://localhost:8888 --token=TOKEN [--once] [--fail=<código>] [--interval=10]\n" );
	exit( 1 );
}

$api_base = $base_url . '/wp-json/orp-print/v1';
$jobs_dir = __DIR__ . '/../var/jobs';
if ( ! is_dir( $jobs_dir ) && ! mkdir( $jobs_dir, 0775, true ) && ! is_dir( $jobs_dir ) ) {
	fwrite( STDERR, "No se pudo crear la carpeta de trabajos: {$jobs_dir}\n" );
	exit( 1 );
}

orp_say( 'Simulador de agente de Orvita Print' );
orp_say( 'Servidor: ' . $api_base );

// 0) Salud: comprobar que el plugin responde antes de autenticar.
[ $status ] = orp_call( 'GET', $api_base . '/health', null, null );
if ( 200 !== $status ) {
	orp_fail( "GET /health devolvió {$status}; el plugin no responde como se espera." );
	exit( 1 );
}
orp_say( '/health OK (200).' );

$cursor = 0;
$round  = 0;

do {
	++$round;
	orp_say( sprintf( '--- Ronda %d ---', $round ) );

	// 1) Heartbeat: salidas de este equipo (el plugin hace upsert por rol).
	[ $status, , $body ] = orp_call(
		'POST',
		$api_base . '/heartbeat',
		$token,
		array(
			'agent_version' => 'simulator-1.0.0',
			'pending_local' => null,
			'last_error'    => null,
			'destinations'  => array(
				array(
					'role'         => 'default',
					'label'        => 'Mostrador',
					'printer_name' => 'Simuladora Virtual',
					'copies'       => 1,
					'enabled'      => true,
				),
			),
		)
	);
	if ( 401 === $status ) {
		orp_exit_token_error();
	}
	orp_say(
		sprintf(
			'Heartbeat: %d%s',
			$status,
			200 === $status
				? sprintf( ' (pendientes: %s, config_version: %s)', (string) ( $body['pending_count'] ?? '?' ), (string) ( $body['config_version'] ?? '?' ) )
				: ''
		)
	);

	// 2) Poll con cursor por id (nunca por fecha).
	[ $status, , $body ] = orp_call( 'GET', $api_base . '/jobs?after=' . $cursor . '&limit=10', $token, null );
	if ( 401 === $status ) {
		orp_exit_token_error();
	}
	if ( 200 !== $status ) {
		orp_say( "Poll devolvió {$status}; se reintenta en la siguiente ronda." );
	}

	$jobs = (array) ( $body['jobs'] ?? array() );
	orp_say( sprintf( 'Poll: %d trabajo(s) desde el cursor %d.', count( $jobs ), $cursor ) );

	// 3) Claim + documento + ack por cada trabajo.
	foreach ( $jobs as $job ) {
		$job_id = (int) ( $job['id'] ?? 0 );
		if ( $job_id <= 0 ) {
			continue;
		}

		[ $claim_status, , $claim_body ] = orp_call( 'POST', $api_base . '/jobs/' . $job_id . '/claim', $token, array() );

		if ( 200 === $claim_status ) {
			$document = $claim_body['document'] ?? ( $job['document'] ?? null );
			$attempt  = (int) ( $claim_body['attempt'] ?? 0 );

			if ( ! is_array( $document ) ) {
				// Documento malformado: el agente lo marca failed con orp_invalid_payload.
				orp_say( "Trabajo {$job_id}: documento malformado; ack failed (orp_invalid_payload)." );
				orp_ack( $api_base, $token, $job_id, $attempt, 'orp_invalid_payload', 'El payload no contiene un documento válido.' );
				$cursor = max( $cursor, $job_id );
				continue;
			}

			// 4) Escribir el documento y su versión de texto legible.
			file_put_contents(
				$jobs_dir . '/' . $job_id . '.json',
				json_encode( $document, JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES )
			);
			file_put_contents( $jobs_dir . '/' . $job_id . '.txt', orp_render_text( $document ) );
			orp_say( "Trabajo {$job_id} reclamado (intento {$attempt}); documento en var/jobs/{$job_id}.json y .txt" );

			// 5) Ack: delivered normalmente, failed si --fail=<código>.
			if ( null === $fail_code ) {
				orp_ack_delivered( $api_base, $token, $job_id, $attempt );
			} else {
				orp_ack( $api_base, $token, $job_id, $attempt, $fail_code, 'Fallo simulado con --fail=' . $fail_code );
			}
		} elseif ( 409 === $claim_status ) {
			orp_say( "Trabajo {$job_id}: otro agente lo reclamó (409); se continúa." );
		} elseif ( 410 === $claim_status ) {
			orp_say( "Trabajo {$job_id}: caducado (410); se descarta y no se imprime." );
		} else {
			orp_say( "Trabajo {$job_id}: claim devolvió {$claim_status}; se descarta." );
		}

		// El cursor avanza aunque el trabajo falle (Doc/06 §10).
		$cursor = max( $cursor, $job_id );
	}

	if ( $once ) {
		break;
	}

	sleep( $interval );
} while ( true );

orp_say( 'Fin del simulador.' );
exit( 0 );

/* ------------------------------------------------------------------------- *
 * Funciones auxiliares (van al final para que el flujo se lea arriba).
 * ------------------------------------------------------------------------- */

/** Mensaje por stdout. */
function orp_say( string $message ): void {
	echo '[' . gmdate( 'H:i:s' ) . '] ' . $message . "\n";
}

/** Mensaje por stderr y salida con error. */
function orp_fail( string $message ): void {
	fwrite( STDERR, 'ERROR: ' . $message . "\n" );
}

/** 401: detener el poller y avisar (Doc/03 §6.3). */
function orp_exit_token_error(): void {
	orp_fail( '401: token inválido o revocado. Deteniendo el simulador.' );
	exit( 1 );
}

/** Petición HTTP JSON. Devuelve [status, headers (minúsculas), body decodificado]. */
function orp_http( string $method, string $url, ?string $token, ?array $body ): array {
	$headers = array( 'Accept: application/json' );
	if ( null !== $token ) {
		$headers[] = 'Authorization: Bearer ' . $token;
		// Respaldo para hostings que eliminan la cabecera Authorization (Doc/06 §11).
		$headers[] = 'X-Orp-Token: ' . $token;
	}

	$payload = null;
	if ( is_array( $body ) ) {
		$payload   = json_encode( $body, JSON_UNESCAPED_UNICODE );
		$headers[] = 'Content-Type: application/json';
	}

	if ( function_exists( 'curl_init' ) ) {
		$ch = curl_init( $url );
		curl_setopt_array(
			$ch,
			array(
				CURLOPT_CUSTOMREQUEST   => $method,
				CURLOPT_RETURNTRANSFER  => true,
				CURLOPT_HEADER          => true,
				CURLOPT_TIMEOUT         => 20,
				CURLOPT_HTTPHEADER      => $headers,
			)
		);
		if ( null !== $payload ) {
			curl_setopt( $ch, CURLOPT_POSTFIELDS, $payload );
		}

		$raw       = curl_exec( $ch );
		$status    = (int) curl_getinfo( $ch, CURLINFO_RESPONSE_CODE );
		$head_size = (int) curl_getinfo( $ch, CURLINFO_HEADER_SIZE );
		curl_close( $ch );

		if ( false === $raw || 0 === $status ) {
			return array( 0, array(), array() );
		}

		return array(
			$status,
			orp_parse_headers( substr( (string) $raw, 0, $head_size ) ),
			orp_parse_body( substr( (string) $raw, $head_size ) ),
		);
	}

	// Respaldo sin curl: stream context.
	$context = stream_context_create(
		array(
			'http' => array(
				'method'        => $method,
				'header'        => implode( "\r\n", $headers ),
				'content'       => $payload,
				'timeout'       => 20,
				'ignore_errors' => true,
			),
		)
	);
	$raw     = @file_get_contents( $url, false, $context );
	$status  = 0;
	$headers = array();
	if ( isset( $http_response_header ) && is_array( $http_response_header ) ) {
		$headers = orp_parse_headers( implode( "\r\n", $http_response_header ) );
		if ( preg_match( '/^HTTP\/\S+\s+(\d{3})/', $http_response_header[0] ?? '', $matches ) ) {
			$status = (int) $matches[1];
		}
	}

	return array( $status, $headers, orp_parse_body( (string) $raw ) );
}

/** Petición con reintentos ante 429 (lee Retry-After y no se rompe). */
function orp_call( string $method, string $url, ?string $token, ?array $body ): array {
	[ $status, $headers, $parsed ] = orp_http( $method, $url, $token, $body );

	if ( 429 === $status ) {
		$retry = max( 2, (int) ( $headers['retry-after'] ?? 5 ) );
		orp_say( "429 rate limit; esperando {$retry} s antes de reintentar…" );
		sleep( $retry );

		[ $status, $headers, $parsed ] = orp_http( $method, $url, $token, $body );
	}

	return array( $status, $headers, $parsed );
}

/** Convierte el texto de cabeceras en un array asociativo en minúsculas. */
function orp_parse_headers( string $raw ): array {
	$headers = array();
	foreach ( explode( "\r\n", $raw ) as $line ) {
		if ( str_contains( $line, ':' ) ) {
			[ $name, $value ] = explode( ':', $line, 2 );
			$headers[ strtolower( trim( $name ) ) ] = trim( $value );
		}
	}
	return $headers;
}

/** Decodifica el cuerpo JSON (o lo devuelve como texto crudo). */
function orp_parse_body( string $raw ): array {
	$decoded = json_decode( $raw, true );
	return is_array( $decoded ) ? $decoded : array( 'raw' => $raw );
}

/** Envía un ack de resultado (delivered o failed). */
function orp_ack( string $api_base, string $token, int $job_id, int $attempt, string $error_code, string $error ): void {
	[ $status, , $body ] = orp_call(
		'POST',
		$api_base . '/jobs/' . $job_id . '/ack',
		$token,
		array(
			'status'     => 'failed',
			'attempt'    => $attempt,
			'error_code' => $error_code,
			'error'      => $error,
		)
	);
	orp_say( sprintf( 'Trabajo %d: ack failed (%s) → %d %s', $job_id, $error_code, $status, (string) ( $body['job_status'] ?? '' ) ) );
}

/** Envía un ack delivered con el nombre de impresora y el tiempo empleado. */
function orp_ack_delivered( string $api_base, string $token, int $job_id, int $attempt ): void {
	[ $status, , $body ] = orp_call(
		'POST',
		$api_base . '/jobs/' . $job_id . '/ack',
		$token,
		array(
			'status'     => 'delivered',
			'attempt'    => $attempt,
			'printer'    => 'Simuladora Virtual',
			'elapsed_ms' => random_int( 50, 400 ),
		)
	);
	orp_say( sprintf( 'Trabajo %d: ack delivered → %d (estado: %s)', $job_id, $status, (string) ( $body['job_status'] ?? '?' ) ) );
}

/**
 * Renderiza el documento neutro v1 a texto legible (no ESC/POS): sirve para
 * revisar el contenido sin impresora. Ignora los bloques desconocidos.
 */
function orp_render_text( array $document ): string {
	$width = ( (int) ( $document['paper']['width_mm'] ?? 80 ) ) <= 58 ? 32 : 42;
	$lines = array();

	foreach ( (array) ( $document['blocks'] ?? array() ) as $block ) {
		$type = (string) ( $block['type'] ?? '' );

		switch ( $type ) {
			case 'text':
				$value = (string) ( $block['value'] ?? '' );
				$align = (string) ( $block['align'] ?? 'left' );
				$size  = (string) ( $block['size'] ?? 'normal' );
				$scale = 'double' === $size ? 2 : ( 'triple' === $size ? 3 : 1 );
				foreach ( explode( "\n", wordwrap( $value, max( 1, (int) ( $width / $scale ) ), "\n", true ) ) as $piece ) {
					$lines[] = orp_text_pad( $piece, $width, $align );
				}
				break;

			case 'divider':
				$lines[] = str_repeat( (string) ( $block['char'] ?? '-' ), $width );
				break;

			case 'kv':
				foreach ( (array) ( $block['items'] ?? array() ) as $item ) {
					$label = (string) ( $item['label'] ?? '' );
					$value = (string) ( $item['value'] ?? '' );
					$lines[] = orp_text_pad( $label, 12 ) . orp_text_pad( $value, max( 1, $width - 12 ) );
				}
				break;

			case 'items':
				$lines = array_merge( $lines, orp_render_items_table( $block, $width ) );
				break;

			case 'totals':
				foreach ( (array) ( $block['items'] ?? array() ) as $item ) {
					$label = (string) ( $item['label'] ?? '' );
					$value = (string) ( $item['value'] ?? '' );
					if ( ! empty( $item['bold'] ) ) {
						$label = '* ' . $label;
					}
					$lines[] = orp_text_pad( $label, max( 1, $width - 10 ) ) . orp_text_pad( $value, 10, 'right' );
				}
				break;

			case 'feed':
				$lines = array_merge( $lines, array_fill( 0, max( 1, (int) ( $block['lines'] ?? 1 ) ), '' ) );
				break;

			case 'cut':
				$lines[] = '(corte ' . (string) ( $block['mode'] ?? 'partial' ) . ')';
				break;

			default:
				// Bloque desconocido: se ignora con un aviso (Doc/04 §8).
				$lines[] = '[bloque ignorado: ' . $type . ']';
				break;
		}
	}

	return implode( "\n", $lines ) . "\n";
}

/** Tabla de productos para el bloque items. */
function orp_render_items_table( array $block, int $width ): array {
	$columns = (array) ( $block['columns'] ?? array() );
	$aligns  = (array) ( $block['align'] ?? array() );
	$rows    = (array) ( $block['rows'] ?? array() );
	$count   = count( $columns );

	if ( 0 === $count ) {
		return array();
	}

	// Reparto simple: última columna estrecha (importe), resto repartido.
	$last_width = max( 6, min( 10, (int) ( $width / 4 ) ) );
	$others     = max( 1, $count - 1 );
	$remaining  = $width - $last_width - $others - 1;
	$widths     = array();
	for ( $i = 0; $i < $count; $i++ ) {
		$widths[ $i ] = ( $count - 1 === $i ) ? $last_width : max( 3, (int) ( $remaining / $others ) );
	}

	$lines = array();
	foreach ( array( $columns ) as $header ) {
		$header = (array) $header;
		$line   = '';
		foreach ( $header as $i => $cell ) {
			$line .= orp_text_pad( (string) $cell, $widths[ $i ], (string) ( $aligns[ $i ] ?? 'left' ) ) . ' ';
		}
		$lines[] = rtrim( $line );
	}
	$lines[] = str_repeat( '-', $width );

	foreach ( $rows as $row ) {
		$line = '';
		foreach ( (array) $row as $i => $cell ) {
			if ( $i >= $count ) {
				break;
			}
			$line .= orp_text_pad( (string) $cell, $widths[ $i ], (string) ( $aligns[ $i ] ?? 'left' ) ) . ' ';
		}
		$lines[] = rtrim( $line );
	}

	return $lines;
}

/** Nº de caracteres multibyte con respaldo si falta mbstring. */
function orp_mb_strlen( string $value ): int {
	if ( function_exists( 'mb_strlen' ) ) {
		return (int) mb_strlen( $value );
	}
	$chars = preg_split( '//u', $value, -1, PREG_SPLIT_NO_EMPTY );
	return is_array( $chars ) ? count( $chars ) : strlen( $value );
}

/** Recorte multibyte con respaldo si falta mbstring. */
function orp_mb_substr( string $value, int $start, ?int $length = null ): string {
	if ( function_exists( 'mb_substr' ) ) {
		return mb_substr( $value, $start, $length );
	}
	$chars = preg_split( '//u', $value, -1, PREG_SPLIT_NO_EMPTY );
	if ( is_array( $chars ) ) {
		return implode( '', array_slice( $chars, $start, $length ) );
	}
	return $value;
}

/** Relleno con recuento multibyte (str_pad cuenta bytes y desajusta acentos). */
function orp_text_pad( string $value, int $width, string $align = 'left' ): string {
	$length = orp_mb_strlen( $value );
	if ( $length > $width ) {
		$value  = orp_mb_substr( $value, 0, $width );
		$length = $width;
	}
	$padding = str_repeat( ' ', max( 0, $width - $length ) );

	return match ( $align ) {
		'right'  => $padding . $value,
		'center' => str_repeat( ' ', intdiv( max( 0, $width - $length ), 2 ) ) . $value,
		default  => $value . $padding,
	};
}
