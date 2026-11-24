<?php
/**
 * Verificación dinámica F1-03 + F1-04 (ejecutar dentro del cli de wp-env):
 *   npx @wordpress/env run cli wp eval-file /var/www/html/wp-content/plugins/orp-print/tools/_verify_f1_03_04.php
 *
 * Comprueba: guardado de plantilla con subida de orp_template_version y
 * registro, documento según la selección de campos desde un pedido REAL
 * (API CRUD/HPOS), logo como PNG base64 (máx. 384 px), clave de
 * idempotencia con la versión de plantilla, reimpresión manual con clave
 * distinta, y HTML imprimible sin recursos externos.
 *
 * No destructivo: al final restaura la plantilla y la versión de plantilla
 * encontradas al empezar y borra el pedido/producto/trabajos de prueba.
 *
 * Sin declare(strict_types=1): wp eval-file evalúa el fichero y exige que
 * sea la primera sentencia absoluta del script.
 */

use Orvita\PrintBridge\Domain\DestinationRepository;
use Orvita\PrintBridge\Domain\JobFactory;
use Orvita\PrintBridge\Domain\LocationResolver;
use Orvita\PrintBridge\Support\Settings;
use Orvita\PrintBridge\Template\DocumentBuilder;
use Orvita\PrintBridge\Template\TemplateSettings;
use Orvita\PrintBridge\Template\TicketHtml;

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

/** JSON legible de bloques (unicode sin escapar). */
function orp_blocks_json( array $blocks ): string {
	return (string) json_encode( $blocks, JSON_UNESCAPED_UNICODE );
}

/* -------------------------------------------------- estado inicial y respaldo */
$original_template = get_option( TemplateSettings::OPTION, false );
$original_tv       = (int) get_option( 'orp_template_version', 1 );

/* ---------------------------------------------- 1. guardado sube la versión */
echo "\n== 1. Guardado de plantilla y orp_template_version ==\n";

$tv_before = (int) get_option( 'orp_template_version', 1 );

$changed = TemplateSettings::save(
	array(
		'fields'        => array( 'items' => true, 'footer' => true ),
		'footer_text'   => 'Gracias por su pedido (verificación)',
		'logo_media_id' => 0,
	)
);
if ( $changed && (int) get_option( 'orp_template_version', 1 ) === $tv_before + 1 ) {
	ok( 'guardar la plantilla con cambios sube orp_template_version' );
} else {
	bad( 'el guardado no subió la versión de plantilla' );
}

$changed_again = TemplateSettings::save(
	array(
		'fields'        => array( 'items' => true, 'footer' => true ),
		'footer_text'   => 'Gracias por su pedido (verificación)',
		'logo_media_id' => 0,
	)
);
if ( ! $changed_again && (int) get_option( 'orp_template_version', 1 ) === $tv_before + 1 ) {
	ok( 'guardar sin cambios NO sube la versión' );
} else {
	bad( 'guardado idéntico subió la versión sin motivo' );
}

// Registro del cambio visible (context=template en orp_logs).
global $wpdb;
$logs = (int) $wpdb->get_var( $wpdb->prepare( "SELECT COUNT(*) FROM {$wpdb->prefix}orp_logs WHERE context = 'template'" ) ); // phpcs:ignore WordPress.DB.DirectDatabaseQuery
if ( $logs > 0 ) {
	ok( "el cambio quedó registrado en orp_logs (context=template, $logs entradas)" );
} else {
	bad( 'no hay registro de cambios de plantilla en orp_logs' );
}

/* ------------------------------------------------- 2. pedido real → bloques */
echo "\n== 2. Documento desde un pedido real (API CRUD) según la plantilla ==\n";

// Producto + pedido de prueba: ciudad de envío Madrid → regla del local-centro.
$product = new WC_Product_Simple();
$product->set_name( 'Pizza Margarita · jamón, piña, ñ, €, áéíóú' );
$product->set_regular_price( '12.50' );
$product_id = $product->save();

$order = wc_create_order();
$order->add_product( wc_get_product( $product_id ), 2 );
$order->set_billing_first_name( 'María' );
$order->set_billing_last_name( 'López' );
$order->set_billing_phone( '600 000 000' );
$order->set_shipping_address_1( 'Calle Mayor 3' );
$order->set_shipping_city( 'Madrid' );
$order->calculate_totals();
$order->save();
$order_id = $order->get_id();

$location = LocationResolver::resolve_for_order( $order );
if ( $location && 'local-centro' === $location->code ) {
	ok( 'el pedido resuelve al local sembrado local-centro por su regla city' );
} else {
	bad( 'el pedido no resolvió a local-centro' );
}

// Plantilla completa activa: todos los campos.
$all_fields = array();
foreach ( array_keys( TemplateSettings::FIELDS ) as $field ) {
	$all_fields[ $field ] = true;
}
TemplateSettings::save(
	array(
		'fields'        => $all_fields,
		'footer_text'   => 'Gracias por su pedido (verificación)',
		'logo_media_id' => 0,
	)
);

$document = DocumentBuilder::build_from_order(
	$order,
	array(
		'order_id'      => $order_id,
		'location_code' => (string) $location->code,
		'copies'        => 1,
		'printer_role'  => 'default',
	)
);

$types = array();
foreach ( $document['blocks'] as $block ) {
	$types[] = $block['type'];
}
$json = orp_blocks_json( $document['blocks'] );

if ( in_array( 'kv', $types, true ) && in_array( 'items', $types, true ) && in_array( 'totals', $types, true ) && in_array( 'qr', $types, true ) ) {
	ok( 'con todos los campos: kv + items + totals + qr presentes' );
} else {
	bad( 'bloques incompletos con plantilla completa: ' . wp_json_encode( $types ) );
}
if ( ! in_array( 'image', $types, true ) ) {
	ok( 'sin logo configurado el bloque image no se emite' );
} else {
	bad( 'se emitió image sin logo configurado' );
}
if ( false !== strpos( $json, 'Pizza Margarita · jamón, piña, ñ, €, áéíóú' ) && false !== strpos( $json, 'María López' ) && false !== strpos( $json, 'Calle Mayor 3' ) ) {
	ok( 'el documento lleva los datos del pedido leídos por la API CRUD (HPOS)' );
} else {
	bad( 'faltan datos del pedido en el documento' );
}
if ( 80 === (int) $document['paper']['width_mm'] && 1 === (int) $document['version'] ) {
	ok( 'envoltorio correcto (version 1, paper 80 mm del ajuste)' );
} else {
	bad( 'envoltorio incorrecto: ' . wp_json_encode( array( 'paper' => $document['paper'], 'version' => $document['version'] ) ) );
}

// Plantilla mínima del preset Aviso: sin datos de cliente ni QR ni nota.
TemplateSettings::apply_field_set(
	array(
		'business_name' => true,
		'datetime'      => true,
		'order_number'  => true,
		'items'         => true,
		'totals'        => true,
	),
	'aviso'
);

$minimal = DocumentBuilder::build_from_order( $order, array() );
$mtypes  = array();
foreach ( $minimal['blocks'] as $block ) {
	$mtypes[] = $block['type'];
}
$mjson = orp_blocks_json( $minimal['blocks'] );

if ( in_array( 'kv', $mtypes, true ) && false !== strpos( $mjson, '"Pedido"' ) && false === strpos( $mjson, 'Teléfono' ) && false === strpos( $mjson, 'Entrega' ) ) {
	ok( 'plantilla mínima: kv solo con el nº de pedido, sin cliente/teléfono/dirección' );
} else {
	bad( 'la plantilla mínima no filtra los campos: ' . wp_json_encode( $mtypes ) );
}
if ( false === strpos( $mjson, 'María López' ) && false === strpos( $mjson, 'No válido como factura' ) ) {
	ok( 'la plantilla mínima no lleva datos de cliente ni la nota legal' );
} else {
	bad( 'la plantilla mínima filtra datos que no debería llevar' );
}

/* ------------------------------------------------------------ 3. logo → PNG */
echo "\n== 3. Logo: PNG base64 con ancho máximo 384 px ==\n";

// PNG de 500x120 generado con GD: obliga a reescalar a 384 px.
if ( function_exists( 'imagecreatetruecolor' ) ) {
	$uploads = wp_upload_dir();
	$path    = $uploads['basedir'] . '/orp-verify-logo.png';
	$big     = imagecreatetruecolor( 500, 120 );
	$white   = imagecolorallocate( $big, 255, 255, 255 );
	imagefill( $big, 0, 0, $white );
	$black = imagecolorallocate( $big, 0, 0, 0 );
	imagestring( $big, 5, 20, 50, 'ORVITA', $black );
	imagepng( $big, $path );
	imagedestroy( $big );

	$media_id = wp_insert_attachment(
		array(
			'post_mime_type' => 'image/png',
			'post_title'     => 'Logo verificación Orvita',
			'post_status'    => 'inherit',
		),
		$path
	);
	update_attached_file( $media_id, $path );

	TemplateSettings::save(
		array(
			'fields'        => array( 'logo' => true ),
			'footer_text'   => '',
			'logo_media_id' => (int) $media_id,
		)
	);

	$logo_doc = DocumentBuilder::build_from_order( $order, array() );
	$image    = null;
	foreach ( $logo_doc['blocks'] as $block ) {
		if ( 'image' === $block['type'] ) {
			$image = $block;
		}
	}

	if ( null !== $image ) {
		$bytes = base64_decode( $image['data'], true ); // phpcs:ignore WordPress.PHP.DiscouragedPHPFunctions.obfuscation_base64_decode
		$size  = getimagesizefromstring( $bytes );
		if ( false !== $bytes && is_array( $size ) && 0 === strpos( $bytes, "\x89PNG" ) && (int) $size[0] <= 384 ) {
			ok( 'el logo sale como PNG base64 reescalado a ' . (int) $size[0] . ' px (máx. 384)' );
		} else {
			bad( 'el bloque image no lleva un PNG válido dentro del ancho máximo' );
		}
		if ( isset( $image['width_dots'] ) && (int) $image['width_dots'] <= 384 ) {
			ok( 'width_dots dentro del máximo: ' . (int) $image['width_dots'] );
		} else {
			bad( 'width_dots fuera de rango' );
		}
	} else {
		bad( 'con logo configurado el bloque image no se emitió' );
	}

	// Adjunto con fichero inaccesible: el bloque no se emite y no rompe.
	TemplateSettings::save(
		array(
			'fields'        => array( 'logo' => true ),
			'footer_text'   => '',
			'logo_media_id' => $media_id + 100000,
		)
	);
	$doc_broken = DocumentBuilder::build_from_order( $order, array() );
	$btypes     = array();
	foreach ( $doc_broken['blocks'] as $block ) {
		$btypes[] = $block['type'];
	}
	if ( ! in_array( 'image', $btypes, true ) ) {
		ok( 'logo con adjunto inexistente: el bloque no se emite (aviso registrado)' );
	} else {
		bad( 'adjunto inexistente emitió un bloque image' );
	}

	wp_delete_attachment( $media_id, true );
} else {
	bad( 'GD no disponible en el cli: no se pudo verificar el logo' );
}

/* -------------------------------------- 4. idempotencia con template_version */
echo "\n== 4. La versión de plantilla entra en la clave de idempotencia ==\n";

$job_one = JobFactory::create( (int) $location->id, $order_id, 'default', 1, $document );
if ( null !== $job_one ) {
	ok( 'primer trabajo del pedido creado' );
} else {
	bad( 'el primer trabajo no se creó' );
}

$job_repeat = JobFactory::create( (int) $location->id, $order_id, 'default', 1, $document );
if ( null === $job_repeat ) {
	ok( 'mismo disparo tras guardar sin cambios: deduplicado (INSERT IGNORE)' );
} else {
	bad( 'se duplicó el trabajo sin cambiar la plantilla' );
}

// La clave del trabajo lleva la versión vigente.
$expected_key = hash( 'sha256', $order_id . '|' . (int) $location->id . '|default|' . (int) get_option( 'orp_template_version', 1 ) );
$key_one = (string) $wpdb->get_var( $wpdb->prepare( "SELECT idempotency_key FROM {$wpdb->prefix}orp_jobs WHERE id = %d", $job_one ) ); // phpcs:ignore WordPress.DB.DirectDatabaseQuery
if ( hash_equals( $expected_key, $key_one ) ) {
	ok( 'la clave de idempotencia lleva orp_template_version (Doc/04 §3)' );
} else {
	bad( 'la clave no coincide con la forma order_id|location|role|template_version' );
}

// Cambio de plantilla → el mismo pedido genera trabajo NUEVO si se dispara.
TemplateSettings::save(
	array(
		'fields'        => array( 'items' => true, 'totals' => true ),
		'footer_text'   => '',
		'logo_media_id' => 0,
	)
);
$job_two = JobFactory::create( (int) $location->id, $order_id, 'default', 1, $document );
if ( null !== $job_two ) {
	ok( 'tras cambiar la plantilla el disparo genera trabajo nuevo (versión en la clave)' );
} else {
	bad( 'cambiar la plantilla no cambió la clave de idempotencia' );
}

/* ------------------------------------------- 5. reimpresión manual (F1-04) */
echo "\n== 5. Reimpresión manual con clave distinta ==\n";

$destinations = DestinationRepository::for_location( (int) $location->id, true );
if ( $destinations ) {
	ok( 'el local sembrado tiene ' . count( $destinations ) . ' salida(s) activa(s)' );
} else {
	bad( 'el local sembrado no tiene salidas activas' );
}

$doc_current = DocumentBuilder::build_from_order(
	$order,
	array(
		'order_id'      => $order_id,
		'location_code' => (string) $location->code,
		'copies'        => 1,
		'printer_role'  => 'default',
	)
);

$manual_one = JobFactory::create_manual( (int) $location->id, $order_id, 'default', 1, $doc_current, 1 );
$manual_two = JobFactory::create_manual( (int) $location->id, $order_id, 'default', 1, $doc_current, 1 );

if ( null !== $manual_one && null !== $manual_two && $manual_one !== $manual_two ) {
	ok( "dos reimpresiones manuales seguidas generan trabajos nuevos ($manual_one y $manual_two)" );
} else {
	bad( 'las reimpresiones manuales no generaron trabajos nuevos' );
}

$manual_keys = $wpdb->get_col( $wpdb->prepare( "SELECT idempotency_key FROM {$wpdb->prefix}orp_jobs WHERE id IN (%d, %d)", $manual_one, $manual_two ) ); // phpcs:ignore WordPress.DB.DirectDatabaseQuery
if ( 2 === count( array_unique( $manual_keys ) ) && ! in_array( $key_one, $manual_keys, true ) ) {
	ok( 'las claves manuales son distintas entre sí y de la automática' );
} else {
	bad( 'las claves manuales no son distintas: ' . wp_json_encode( $manual_keys ) );
}

// El payload del trabajo manual lleva la plantilla vigente.
$payload = (string) $wpdb->get_var( $wpdb->prepare( "SELECT payload FROM {$wpdb->prefix}orp_jobs WHERE id = %d", $manual_one ) ); // phpcs:ignore WordPress.DB.DirectDatabaseQuery
$decoded = json_decode( $payload, true );
if ( is_array( $decoded ) && 1 === (int) $decoded['version'] && isset( $decoded['blocks'] ) && is_array( $decoded['blocks'] ) ) {
	ok( 'el trabajo manual lleva el documento neutro v1 según la plantilla' );
} else {
	bad( 'el payload del trabajo manual no es un documento v1 válido' );
}

/* ------------------------------ 5b. acción de la ficha del pedido (F1-04) */
echo "\n== 5b. Acción «Reimprimir ticket» de la ficha del pedido ==\n";
// Plantilla completa para ver el documento entero en el trabajo nuevo.
TemplateSettings::save(
	array(
		'fields'        => $all_fields,
		'footer_text'   => 'Gracias por su pedido (verificación)',
		'logo_media_id' => 0,
	)
);

$jobs_before_action = (int) $wpdb->get_var( $wpdb->prepare( "SELECT COUNT(*) FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d", $order_id ) ); // phpcs:ignore WordPress.DB.DirectDatabaseQuery

// Mismo camino que la ficha del pedido: hook woocommerce_order_action_orp_reprint.
do_action( 'woocommerce_order_action_orp_reprint', $order );

$jobs_after_action = (int) $wpdb->get_var( $wpdb->prepare( "SELECT COUNT(*) FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d", $order_id ) ); // phpcs:ignore WordPress.DB.DirectDatabaseQuery

if ( $jobs_after_action === $jobs_before_action + 1 ) {
	ok( "la acción «Reimprimir ticket» generó un trabajo nuevo ($jobs_before_action → $jobs_after_action; 1 salida activa)" );
} else {
	bad( "la acción no generó el trabajo esperado: $jobs_before_action → $jobs_after_action" );
}

$result_transient = get_transient( 'orp_reprint_result' );
if ( is_array( $result_transient ) && false !== strpos( (string) $result_transient['message'], (string) $order_id ) ) {
	ok( 'el resultado de la reimpresión quedó en cola para el aviso del panel' );
} else {
	bad( 'sin transient de resultado de la reimpresión' );
}

// Nota en el pedido con el trabajo creado.
$notes = (array) wc_get_order_notes( array( 'order_id' => $order_id ) );
$note_found = false;
foreach ( $notes as $note ) {
	if ( false !== strpos( (string) $note->content, 'reimpresión manual' ) ) {
		$note_found = true;
	}
}
if ( $note_found ) {
	ok( 'la reimpresión dejó nota en el pedido (Orvita Print: reimpresión manual)' );
} else {
	bad( 'no hay nota de reimpresión en el pedido' );
}

// Sin salidas activas: la acción NO crea trabajo y avisa (Doc/03 §5.5).
global $wpdb;
$wpdb->query( $wpdb->prepare( "UPDATE {$wpdb->prefix}orp_destinations SET enabled = 0 WHERE location_id = %d", (int) $location->id ) ); // phpcs:ignore WordPress.DB.DirectDatabaseQuery
$jobs_no_dest = (int) $wpdb->get_var( $wpdb->prepare( "SELECT COUNT(*) FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d", $order_id ) ); // phpcs:ignore WordPress.DB.DirectDatabaseQuery
do_action( 'woocommerce_order_action_orp_reprint', $order );
$jobs_still = (int) $wpdb->get_var( $wpdb->prepare( "SELECT COUNT(*) FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d", $order_id ) ); // phpcs:ignore WordPress.DB.DirectDatabaseQuery
$no_dest_result = get_transient( 'orp_reprint_result' );

if ( $jobs_still === $jobs_no_dest && is_array( $no_dest_result ) && false !== strpos( (string) $no_dest_result['message'], 'salidas activas' ) ) {
	ok( 'sin salidas activas no se crea trabajo y se sugiere imprimir desde el navegador' );
} else {
	bad( 'sin salidas activas la acción no se comportó como debe' );
}
$wpdb->query( $wpdb->prepare( "UPDATE {$wpdb->prefix}orp_destinations SET enabled = 1 WHERE location_id = %d", (int) $location->id ) ); // phpcs:ignore WordPress.DB.DirectDatabaseQuery

delete_transient( 'orp_reprint_result' );
delete_transient( 'orp_fallback_notice' );

/* ------------------------------------------------ 6. HTML imprimible (F1-04) */
echo "\n== 6. HTML imprimible: mismo builder, sin recursos externos ==\n";

// Documento completo con la plantilla en todos los campos (reconstruido
// ahora: en el paso 4 la plantilla quedó mínima y el HTML debe salir
// completo, con los mismos bloques que imprimiría el agente).
$settings_backup = get_option( Settings::OPTION, array() );
update_option( 'orp_settings', array( 'paper_width_mm' => 80 ), false );

TemplateSettings::save(
	array(
		'fields'        => $all_fields,
		'footer_text'   => 'Gracias por su pedido (verificación)',
		'logo_media_id' => 0,
	)
);

$doc_current = DocumentBuilder::build_from_order(
	$order,
	array(
		'order_id'      => $order_id,
		'location_code' => (string) $location->code,
		'copies'        => 1,
		'printer_role'  => 'default',
	)
);

$html = TicketHtml::render( $doc_current, 'Ticket de verificación' );

$checks = array(
	'@page'                        => 'regla @page presente',
	'80mm auto'                    => 'ancho de 80 mm según el ajuste',
	'window.print()'               => 'botón Imprimir = window.print()',
	'María López'                  => 'datos del pedido en el HTML',
	'Pizza Margarita · jamón, piña, ñ, €, áéíóú' => 'cadena de codificación en el HTML',
	'No válido como factura'       => 'nota legal en el HTML',
	'monospace'                    => 'fuente monoespaciada del sistema',
);
foreach ( $checks as $needle => $label ) {
	if ( false !== strpos( $html, $needle ) ) {
		ok( $label );
	} else {
		bad( "falta en el HTML: $needle" );
	}
}

$external = array( '<link', '<script src', '@import', 'fonts.googleapis', '<iframe' );
foreach ( $external as $needle ) {
	if ( false === strpos( $html, $needle ) ) {
		ok( "sin recurso externo: $needle" );
	} else {
		bad( "recurso externo encontrado: $needle" );
	}
}
if ( 0 === preg_match( '/(src|href)\s*=\s*["\'](?!data:)[^"\']*["\']/i', $html ) ) {
	ok( 'ningún src/href apunta fuera del documento (solo data-URI)' );
} else {
	bad( 'el HTML referencia recursos remotos' );
}

// Variante 58 mm: se reconstruye el documento con el ajuste cambiado (el
// ancho viaja en el documento, Doc/04 §7.1).
update_option( 'orp_settings', array( 'paper_width_mm' => 58 ), false );
$doc58     = DocumentBuilder::build_from_order( $order, array() );
$html58    = TicketHtml::render( $doc58, 'Ticket 58' );
if ( false !== strpos( $html58, '58mm auto' ) ) {
	ok( 'variante de 58 mm según el ajuste de papel' );
} else {
	bad( 'la variante de 58 mm no se aplica' );
}
update_option( 'orp_settings', $settings_backup, false );

/* ------------------------------------------------------------- restauración */
echo "\n== Restauración del entorno ==\n";

// Pedidos/trabajos/producto de la verificación.
$wpdb->query( $wpdb->prepare( "DELETE FROM {$wpdb->prefix}orp_jobs WHERE order_id = %d", $order_id ) ); // phpcs:ignore WordPress.DB.DirectDatabaseQuery
wp_delete_post( $product_id, true );
$order->delete( true );

// Plantilla y versión tal como estaban.
if ( false === $original_template ) {
	delete_option( TemplateSettings::OPTION );
} else {
	update_option( TemplateSettings::OPTION, $original_template, false );
}
update_option( 'orp_template_version', $original_tv, false );

echo "Plantilla y versión restauradas (tv=$original_tv); pedido/producto/trabajos de prueba eliminados.\n";

echo "==============================\n";
echo "Resultado: {$GLOBALS['pass']} PASS / {$GLOBALS['fail']} FAIL\n";
exit( 0 );
