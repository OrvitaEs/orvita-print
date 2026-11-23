<?php
/**
 * Pruebas del DocumentBuilder completo (F1-03):
 * bloques según la selección de campos de la plantilla, acceso al pedido por
 * la API CRUD (HPOS), logo como PNG base64 (máx. 384 px) y documento demo.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Tests;

use Orvita\PrintBridge\Template\DocumentBuilder;
use Orvita\PrintBridge\Template\TemplateSettings;

final class DocumentBuilderTest extends \WP_UnitTestCase {

	/** Todos los campos activados (configuración de la plantilla «Completo»). */
	private array $all_fields;

	public function set_up(): void {
		parent::set_up();

		$this->all_fields = array();
		foreach ( array_keys( TemplateSettings::FIELDS ) as $field ) {
			$this->all_fields[ $field ] = true;
		}

		// Plantilla limpia: todos los campos, sin logo.
		TemplateSettings::save(
			array(
				'fields'        => $this->all_fields,
				'footer_text'   => 'Gracias por su pedido',
				'logo_media_id' => 0,
			)
		);
	}

	public function tear_down(): void {
		delete_option( TemplateSettings::OPTION );
		delete_option( 'orp_template_version' );
		parent::tear_down();
	}

	/** ¿Está WooCommerce activo en el entorno de pruebas? Crea sus tablas si faltan. */
	private function woocommerce_or_skip(): void {
		if ( ! class_exists( \WC_Order::class ) ) {
			$this->markTestSkipped( 'WooCommerce no está activo en la suite de pruebas.' );
		}

		if ( ! get_option( 'woocommerce_db_version' ) ) {
			\WC_Install::install();
		}
	}

	/** Pedido real con un producto y envío, creado por la API CRUD. */
	private function make_order(): \WC_Order {
		$product = new \WC_Product_Simple();
		$product->set_name( 'Pizza Margarita · jamón, piña, ñ, €, áéíóú' );
		$product->set_regular_price( '12.50' );
		$product_id = $product->save();

		$order = \wc_create_order();
		$this->assertInstanceOf( \WC_Order::class, $order );

		$order->add_product( \wc_get_product( $product_id ), 2 );
		$order->set_billing_first_name( 'María' );
		$order->set_billing_last_name( 'López' );
		$order->set_billing_phone( '600 000 000' );
		$order->set_shipping_address_1( 'Calle Mayor 3' );
		$order->set_shipping_city( 'Alcalá' );
		$order->calculate_totals();
		$order->save();

		return $order;
	}

	/** Tipos de bloque presentes en un documento. */
	private function block_types( array $document ): array {
		$types = array();
		foreach ( $document['blocks'] as $block ) {
			$types[] = (string) $block['type'];
		}

		return $types;
	}

	/** JSON legible de los bloques (unicode sin escapar, para las aserciones). */
	private function blocks_json( array $document ): string {
		$json = json_encode( $document['blocks'], JSON_UNESCAPED_UNICODE );
		$this->assertIsString( $json );

		return (string) $json;
	}

	/* ---------------------------------------------------------------------
	 * Pedido real → bloques según la selección de campos.
	 * ------------------------------------------------------------------- */

	/** Con todos los campos: cabecera, kv, items, totals, qr, pie y nota. */
	public function test_all_fields_enabled_emits_expected_blocks(): void {
		$this->woocommerce_or_skip();

		$order    = $this->make_order();
		$document = DocumentBuilder::build_from_order(
			$order,
			array( 'order_id' => $order->get_id(), 'location_code' => 'local-centro' )
		);

		$types = $this->block_types( $document );

		$this->assertContains( 'text', $types );
		$this->assertContains( 'kv', $types );
		$this->assertContains( 'items', $types );
		$this->assertContains( 'totals', $types );
		$this->assertContains( 'qr', $types );
		$this->assertContains( 'feed', $types );
		$this->assertContains( 'cut', $types );
		// Sin logo configurado: el bloque image no se emite (Doc/04 §7.2).
		$this->assertNotContains( 'image', $types );

		// Datos del pedido (API CRUD): nombre del producto, nº de pedido y total.
		$json = $this->blocks_json( $document );
		$this->assertStringContainsString( 'Pizza Margarita · jamón, piña, ñ, €, áéíóú', $json );
		$this->assertStringContainsString( '#' . $order->get_order_number(), $json );
		$this->assertStringContainsString( 'María López', $json );
		$this->assertStringContainsString( 'Calle Mayor 3', $json );

		// El total va en negrita.
		$totals = null;
		foreach ( $document['blocks'] as $block ) {
			if ( 'totals' === $block['type'] ) {
				$totals = $block;
			}
		}
		$this->assertNotNull( $totals );
		$last = end( $totals['items'] );
		$this->assertTrue( (bool) $last['bold'] );
	}

	/** Sin logo configurado y sin campos de datos: kv vacío no se emite. */
	public function test_disabled_fields_are_not_emitted(): void {
		$this->woocommerce_or_skip();

		TemplateSettings::save(
			array(
				'fields'        => array(
					'business_name' => true,
					'items'         => true,
					'cut'           => true,
				),
				'footer_text'   => 'Texto de pie que NO debe salir',
				'logo_media_id' => 0,
			)
		);

		$order    = $this->make_order();
		$document = DocumentBuilder::build_from_order( $order, array() );
		$types    = $this->block_types( $document );

		$this->assertContains( 'text', $types );
		$this->assertContains( 'items', $types );
		$this->assertNotContains( 'kv', $types, 'Sin nº pedido/cliente/teléfono/dirección no hay bloque kv.' );
		$this->assertNotContains( 'totals', $types );
		$this->assertNotContains( 'qr', $types );

		$json = $this->blocks_json( $document );
		$this->assertStringNotContainsString( 'Texto de pie que NO debe salir', $json );
		$this->assertStringNotContainsString( 'No válido como factura', $json );
	}

	/** El pie solo se imprime si hay texto (campo activo). */
	public function test_empty_footer_skips_footer_block(): void {
		$this->woocommerce_or_skip();

		TemplateSettings::save(
			array(
				'fields'        => array( 'footer' => true, 'not_invoice' => true ),
				'footer_text'   => '',
				'logo_media_id' => 0,
			)
		);

		$order    = $this->make_order();
		$document = DocumentBuilder::build_from_order( $order, array() );

		$json = $this->blocks_json( $document );
		$this->assertStringContainsString( 'No válido como factura', $json );

		// Solo queda el texto de la nota: el pie vacío no añade bloque.
		$texts = 0;
		foreach ( $document['blocks'] as $block ) {
			if ( 'text' === $block['type'] ) {
				$texts++;
			}
		}
		$this->assertSame( 1, $texts );
	}

	/** El ancho de papel viaja en el documento (Doc/04 §7.1). */
	public function test_paper_width_from_settings(): void {
		$this->woocommerce_or_skip();

		$previous_settings = get_option( 'orp_settings', array() );
		$order             = $this->make_order();

		$document = DocumentBuilder::build_from_order( $order, array() );
		$this->assertSame( 80, (int) $document['paper']['width_mm'] );

		update_option( 'orp_settings', array( 'paper_width_mm' => 58 ), false );
		$document = DocumentBuilder::build_from_order( $order, array() );
		$this->assertSame( 58, (int) $document['paper']['width_mm'] );

		update_option( 'orp_settings', $previous_settings, false );
	}

	/* ---------------------------------------------------------------------
	 * Logo: PNG base64 decodificado en el agente (máx. 384 px).
	 * ------------------------------------------------------------------- */

	/** Adjunto de prueba con un PNG mínimo. */
	private function make_logo_attachment( string $png_base64 ): int {
		$uploads = \wp_upload_dir();
		$path    = $uploads['basedir'] . '/orp-test-logo-' . wp_rand( 1000, 9999 ) . '.png';
		file_put_contents( $path, (string) base64_decode( $png_base64, true ) ); // phpcs:ignore WordPress.PHP.DiscouragedPHPFunctions.obfuscation_base64_decode

		$attachment_id = \wp_insert_attachment(
			array(
				'post_mime_type' => 'image/png',
				'post_title'     => 'Logo de pruebas Orvita',
				'post_status'    => 'inherit',
			),
			$path
		);

		\update_attached_file( $attachment_id, $path );

		return (int) $attachment_id;
	}

	/** Con logo configurado se emite image con PNG base64 dentro del ancho máximo. */
	public function test_logo_configured_emits_png_image_block(): void {
		$this->woocommerce_or_skip();

		if ( ! function_exists( 'imagecreatefromstring' ) ) {
			$this->markTestSkipped( 'GD no está disponible en el entorno de pruebas.' );
		}

		// PNG 1x1 (firma válida: \x89PNG).
		$media_id = $this->make_logo_attachment(
			'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=='
		);
		$this->assertGreaterThan( 0, $media_id );

		TemplateSettings::save(
			array(
				'fields'        => array( 'logo' => true ),
				'footer_text'   => '',
				'logo_media_id' => $media_id,
			)
		);

		$order    = $this->make_order();
		$document = DocumentBuilder::build_from_order( $order, array() );

		$image = null;
		foreach ( $document['blocks'] as $block ) {
			if ( 'image' === $block['type'] ) {
				$image = $block;
			}
		}

		$this->assertNotNull( $image, 'Con logo configurado el bloque image se emite.' );
		$this->assertLessThanOrEqual( 384, (int) $image['width_dots'] );

		// El dato es un PNG real decodificable por el agente (.NET puro).
		$bytes = base64_decode( (string) $image['data'], true ); // phpcs:ignore WordPress.PHP.DiscouragedPHPFunctions.obfuscation_base64_decode
		$this->assertNotFalse( $bytes );
		$this->assertSame( "\x89PNG\r\n\x1a\n", substr( (string) $bytes, 0, 8 ) );

		// Sin logo: el bloque no se emite.
		TemplateSettings::save(
			array(
				'fields'        => array( 'logo' => true ),
				'footer_text'   => '',
				'logo_media_id' => 0,
			)
		);
		$document = DocumentBuilder::build_from_order( $order, array() );
		$this->assertNotContains( 'image', $this->block_types( $document ) );

		\wp_delete_attachment( $media_id, true );
	}

	/* ---------------------------------------------------------------------
	 * Documento de demostración (vista previa sin pedido).
	 * ------------------------------------------------------------------- */

	public function test_demo_document_respects_template_and_contract(): void {
		$document = DocumentBuilder::build_demo(
			array( 'order_id' => 0, 'location_code' => 'local-centro' )
		);

		$this->assertSame( DocumentBuilder::DOCUMENT_VERSION, (int) $document['version'] );
		$this->assertSame( 'local-centro', (string) $document['meta']['location_code'] );

		$json = $this->blocks_json( $document );

		// Cadena obligatoria de codificación (Doc/05 §4.3).
		$this->assertStringContainsString( 'Pizza Margarita · jamón, piña, ñ, €, áéíóú', $json );
		$this->assertStringContainsString( 'No válido como factura', $json );
	}

	public function test_demo_document_honors_disabled_fields(): void {
		TemplateSettings::save(
			array(
				'fields'        => array( 'items' => true ),
				'footer_text'   => '',
				'logo_media_id' => 0,
			)
		);

		$document = DocumentBuilder::build_demo( array() );

		$types = $this->block_types( $document );
		$this->assertContains( 'items', $types );
		$this->assertNotContains( 'totals', $types );
		$this->assertNotContains( 'qr', $types );
	}
}
