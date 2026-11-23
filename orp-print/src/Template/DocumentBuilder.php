<?php
/**
 * DocumentBuilder — documento neutro v1 (Doc/04 §7).
 *
 * Genera los bloques del documento a partir de un pedido de WooCommerce
 * (API CRUD siempre: HPOS) y de la selección de campos de la plantilla
 * (TemplateSettings, F1-03). La misma plantilla alimenta las dos salidas
 * (Doc/03 §5.4-§5.5): documento neutro → orp_jobs y HTML imprimible.
 *
 * Bloques emitidos: image (logo, PNG base64 máx. 384 px), text, kv, items,
 * totals, qr, feed y cut. El bloque image solo se emite si hay logo
 * configurado y se puede producir un PNG real (Doc/04 §7.2).
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Template;

use Orvita\PrintBridge\Support\Clock;
use Orvita\PrintBridge\Support\Logger;
use Orvita\PrintBridge\Support\Settings;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class DocumentBuilder {

	/** Versión del documento neutro (el agente rechaza versiones desconocidas). */
	public const DOCUMENT_VERSION = 1;

	/** Ancho máximo del logo en px (Doc/04 §7.2: máximo 384 px). */
	private const MAX_IMAGE_WIDTH = 384;

	/**
	 * Construye el documento a partir de un pedido (Doc/04 §7.3).
	 * Todo el acceso al pedido es por la API CRUD, nunca get_post_meta (HPOS).
	 *
	 * @param \WC_Order $order Pedido.
	 * @param array     $meta  meta del documento: job_id, order_id, location_code, copies, printer_role.
	 */
	public static function build_from_order( \WC_Order $order, array $meta ): array {
		$meta   = self::build_meta( $meta, $order );
		$blocks = self::build_blocks( self::order_data( $order ) );

		return self::envelope( $meta, $blocks );
	}

	/**
	 * Documento de demostración para `wp orp seed`, la vista previa con datos
	 * de ejemplo (F1-04) y las pruebas: respeta la plantilla activa y usa la
	 * cadena de codificación obligatoria de Doc/05 §4.3.
	 *
	 * @param array $meta meta del documento (order_id normalmente 0).
	 */
	public static function build_demo( array $meta ): array {
		$meta   = self::build_meta( $meta, null );
		$blocks = self::build_blocks( self::demo_data() );

		return self::envelope( $meta, $blocks );
	}

	/* ---------------------------------------------------------------------
	 * Datos de origen.
	 * ------------------------------------------------------------------- */

	/**
	 * Extrae del pedido (API CRUD) los datos que la plantilla necesita.
	 *
	 * @return array<string, mixed> Datos estructurados para build_blocks().
	 */
	private static function order_data( \WC_Order $order ): array {
		return array(
			'business_name' => (string) get_bloginfo( 'name' ),
			'datetime'      => wp_date( 'd/m/Y H:i' ),
			'order_number'  => '#' . $order->get_order_number(),
			'customer'      => trim( $order->get_billing_first_name() . ' ' . $order->get_billing_last_name() ),
			'phone'         => (string) $order->get_billing_phone(),
			'address'       => self::address_one_line( $order ),
			'item_rows'     => self::order_item_rows( $order ),
			'totals'        => self::order_totals( $order ),
			'footer_text'   => TemplateSettings::footer_text(),
			'not_invoice'   => __( 'No válido como factura', 'orp-print' ),
			'qr_value'      => self::qr_value( $order ),
			'image'         => self::image_block(),
		);
	}

	/** Datos de ejemplo para el documento de demostración. */
	private static function demo_data(): array {
		return array(
			'business_name' => __( 'ORVITA PRINT · DEMO', 'orp-print' ),
			'datetime'      => wp_date( 'd/m/Y H:i' ),
			'order_number'  => '#' . (string) ( 1000 + (int) ( time() % 1000 ) ),
			'customer'      => __( 'María López', 'orp-print' ),
			'phone'         => '600 000 000',
			'address'       => __( 'C/ Cañada 3, 2º B — 28801 Alcalá', 'orp-print' ),
			'item_rows'     => array(
				array( '1', __( 'Pizza Margarita · jamón, piña, ñ, €, áéíóú', 'orp-print' ), '12,50' ),
				array( '2', __( 'Pizza Barbacoa', 'orp-print' ), '22,00' ),
			),
			'totals'        => array(
				self::total_item( __( 'Subtotal', 'orp-print' ), 34.0 ),
				self::total_item( __( 'Envío', 'orp-print' ), 2.0 ),
				array( 'label' => __( 'TOTAL', 'orp-print' ), 'value' => '36,00', 'bold' => true ),
			),
			'footer_text'   => TemplateSettings::footer_text(),
			'not_invoice'   => __( 'No válido como factura', 'orp-print' ),
			'qr_value'      => '#demo',
			'image'         => self::image_block(),
		);
	}

	/* ---------------------------------------------------------------------
	 * Construcción de bloques según la selección de campos.
	 * ------------------------------------------------------------------- */

	/**
	 * Ensambla los bloques del documento respetando los campos activados de
	 * la plantilla (Doc/03 §5.4: TemplateSettings → DocumentBuilder).
	 *
	 * @param array<string, mixed> $data Datos estructurados (order_data o demo_data).
	 */
	private static function build_blocks( array $data ): array {
		$fields = TemplateSettings::fields();

		$blocks = array();

		// Logo (image): solo si hay un PNG real que emitir.
		if ( ! empty( $fields['logo'] ) && ! empty( $data['image'] ) ) {
			$blocks[] = $data['image'];
		}

		// Cabecera: título del negocio y fecha/hora.
		$header = false;
		if ( ! empty( $fields['business_name'] ) && '' !== (string) $data['business_name'] ) {
			$blocks[] = self::block_text( (string) $data['business_name'], 'center', true, 'double' );
			$header   = true;
		}
		if ( ! empty( $fields['datetime'] ) && '' !== (string) $data['datetime'] ) {
			$blocks[] = self::block_text( (string) $data['datetime'], 'center' );
			$header   = true;
		}
		if ( $header ) {
			$blocks[] = self::block_divider();
		}

		// Datos del pedido: cada kv se emite solo si su campo está activo y tiene valor.
		$kv_items = array();
		if ( ! empty( $fields['order_number'] ) && '' !== (string) $data['order_number'] ) {
			$kv_items[] = self::kv_item( __( 'Pedido', 'orp-print' ), (string) $data['order_number'] );
		}
		if ( ! empty( $fields['customer'] ) && '' !== (string) $data['customer'] ) {
			$kv_items[] = self::kv_item( __( 'Cliente', 'orp-print' ), (string) $data['customer'] );
		}
		if ( ! empty( $fields['phone'] ) && '' !== (string) $data['phone'] ) {
			$kv_items[] = self::kv_item( __( 'Teléfono', 'orp-print' ), (string) $data['phone'] );
		}
		if ( ! empty( $fields['address'] ) && '' !== (string) $data['address'] ) {
			$kv_items[] = self::kv_item( __( 'Entrega', 'orp-print' ), (string) $data['address'] );
		}

		if ( $kv_items ) {
			$blocks[] = array( 'type' => 'kv', 'items' => $kv_items );
			$blocks[] = self::block_divider();
		}

		// Líneas de producto.
		$rows = array_values( (array) ( $data['item_rows'] ?? array() ) );
		if ( ! empty( $fields['items'] ) && $rows ) {
			$blocks[] = array(
				'type'    => 'items',
				'columns' => array( __( 'Cant', 'orp-print' ), __( 'Producto', 'orp-print' ), __( 'Importe', 'orp-print' ) ),
				'align'   => array( 'left', 'left', 'right' ),
				'rows'    => $rows,
			);
		}

		// Totales.
		$totals = array_values( (array) ( $data['totals'] ?? array() ) );
		if ( ! empty( $fields['totals'] ) && $totals ) {
			$blocks[] = array( 'type' => 'totals', 'items' => $totals );
		}

		// QR (Doc/04 §7.2: value + size; el agente lo dibuja en el papel).
		if ( ! empty( $fields['qr'] ) && '' !== (string) ( $data['qr_value'] ?? '' ) ) {
			$blocks[] = array(
				'type'  => 'qr',
				'value' => (string) $data['qr_value'],
				'size'  => 6,
			);
		}

		// Pie personalizado (solo si el campo está activo y hay texto).
		if ( ! empty( $fields['footer'] ) && '' !== (string) ( $data['footer_text'] ?? '' ) ) {
			$blocks[] = self::block_text( (string) $data['footer_text'], 'center' );
		}

		// Nota legal: el documento no es una factura (Doc/03 §6.7).
		if ( ! empty( $fields['not_invoice'] ) && '' !== (string) ( $data['not_invoice'] ?? '' ) ) {
			$blocks[] = self::block_text( (string) $data['not_invoice'], 'center' );
		}

		$blocks[] = array( 'type' => 'feed', 'lines' => 3 );
		$blocks[] = array( 'type' => 'cut', 'mode' => (string) Settings::get( 'cut_mode', 'partial' ) );

		return $blocks;
	}

	/* ---------------------------------------------------------------------
	 * Logo: PNG base64 (Doc/04 §7.2, ratificado: PNG decodificado en el
	 * agente con .NET puro; ancho máximo 384 px).
	 * ------------------------------------------------------------------- */

	/**
	 * Bloque image del logo configurado, o null si no se puede emitir.
	 *
	 * El PNG se produce aquí (no en el agente): si el adjunto no es PNG o
	 * excede 384 px de ancho, se aplana sobre fondo blanco (la transparencia
	 * no existe en papel térmico) y se reescala con GD. Sin GD, se pasa el
	 * PNG original tal cual si ya cumple el ancho; en cualquier otro caso el
	 * bloque no se emite y el aviso queda registrado.
	 */
	private static function image_block(): ?array {
		$media_id = TemplateSettings::logo_media_id();

		if ( $media_id <= 0 ) {
			return null;
		}

		$file = (string) get_attached_file( $media_id );

		if ( '' === $file || ! is_file( $file ) ) {
			Logger::log(
				'warning',
				'template',
				'El logo configurado no tiene fichero accesible; el bloque image no se emite.',
				null,
				null,
				array( 'media_id' => $media_id )
			);
			return null;
		}

		$bytes = (string) file_get_contents( $file ); // phpcs:ignore WordPress.WP.AlternativeFunctions.file_system_read -- Lectura del adjunto propio.

		if ( '' === $bytes ) {
			return null;
		}

		$is_png   = str_starts_with( $bytes, "\x89PNG\r\n" );
		$size     = getimagesize( $file );
		$width    = is_array( $size ) ? (int) $size[0] : 0;

		// Con GD: aplana sobre blanco y reescala al ancho máximo.
		if ( function_exists( 'imagecreatefromstring' ) ) {
			$png = self::flatten_and_scale_png( $bytes );

			if ( null !== $png ) {
				return $png;
			}

			Logger::log(
				'warning',
				'template',
				'No se pudo procesar el logo con GD; el bloque image no se emite.',
				null,
				null,
				array( 'media_id' => $media_id )
			);
			return null;
		}

		// Sin GD: solo passthrough de un PNG que ya cumpla el ancho máximo.
		if ( $is_png && $width > 0 && $width <= self::MAX_IMAGE_WIDTH ) {
			return array(
				'type'        => 'image',
				'data'        => base64_encode( $bytes ), // phpcs:ignore WordPress.PHP.DiscouragedPHPFunctions.obfuscation_base64_encode -- El contrato exige base64.
				'width_dots'  => $width,
			);
		}

		Logger::log(
			'warning',
			'template',
			'GD no está disponible y el logo no es un PNG dentro del ancho máximo; el bloque image no se emite.',
			null,
			null,
			array( 'media_id' => $media_id )
		);

		return null;
	}

	/**
	 * Aplana la imagen sobre fondo blanco, la reescala a 384 px de ancho
	 * máximo y devuelve el bloque image con el PNG resultante en base64.
	 *
	 * @return array|null Bloque image o null si falla el procesado.
	 */
	private static function flatten_and_scale_png( string $bytes ): ?array {
		$src = imagecreatefromstring( $bytes );

		if ( false === $src ) {
			return null;
		}

		$width  = imagesx( $src );
		$height = imagesy( $src );

		if ( $width > self::MAX_IMAGE_WIDTH ) {
			$scaled = imagescale( $src, self::MAX_IMAGE_WIDTH );

			if ( false !== $scaled ) {
				$src    = $scaled;
				$width  = imagesx( $src );
				$height = imagesy( $src );
			}
		}

		// La transparencia no existe en el papel térmico: fondo blanco.
		$flat  = imagecreatetruecolor( $width, $height );
		$white = imagecolorallocate( $flat, 255, 255, 255 );
		imagefill( $flat, 0, 0, $white );
		imagecopy( $flat, $src, 0, 0, 0, 0, $width, $height );

		ob_start();
		imagepng( $flat, null, 6 );
		$png = (string) ob_get_clean();

		imagedestroy( $flat );
		imagedestroy( $src );

		if ( '' === $png ) {
			return null;
		}

		return array(
			'type'        => 'image',
			'data'        => base64_encode( $png ), // phpcs:ignore WordPress.PHP.DiscouragedPHPFunctions.obfuscation_base64_encode -- El contrato exige base64.
			'width_dots'  => min( self::MAX_IMAGE_WIDTH, $width ),
		);
	}

	/* ---------------------------------------------------------------------
	 * Internos.
	 * ------------------------------------------------------------------- */

	/** Envoltorio del documento (Doc/04 §7.1). */
	private static function envelope( array $meta, array $blocks ): array {
		return array(
			'version' => self::DOCUMENT_VERSION,
			'meta'    => $meta,
			'paper'   => array( 'width_mm' => (int) Settings::get( 'paper_width_mm', 80 ) ),
			'blocks'  => $blocks,
		);
	}

	/** Completa el meta con valores por defecto. */
	private static function build_meta( array $meta, ?\WC_Order $order ): array {
		return array(
			'job_id'        => (int) ( $meta['job_id'] ?? 0 ),
			'order_id'      => (int) ( $meta['order_id'] ?? ( $order ? $order->get_id() : 0 ) ),
			'location_code' => (string) ( $meta['location_code'] ?? '' ),
			'created_at'    => (string) ( $meta['created_at'] ?? Clock::now_iso() ),
			'copies'        => (int) ( $meta['copies'] ?? 1 ),
			'printer_role'  => (string) ( $meta['printer_role'] ?? 'default' ),
		);
	}

	/** Bloque text (Doc/04 §7.2). */
	private static function block_text( string $value, string $align = 'left', bool $bold = false, string $size = 'normal' ): array {
		return array(
			'type'  => 'text',
			'value' => $value,
			'align' => $align,
			'bold'  => $bold,
			'size'  => $size,
		);
	}

	/** Bloque divider con el guion por defecto. */
	private static function block_divider(): array {
		return array( 'type' => 'divider', 'char' => '-' );
	}

	/** Par etiqueta/valor para el bloque kv. */
	private static function kv_item( string $label, string $value ): array {
		return array( 'label' => $label, 'value' => $value );
	}

	/** Línea de total para el bloque totals. */
	private static function total_item( string $label, float $amount ): array {
		return array( 'label' => $label, 'value' => number_format_i18n( $amount, 2 ) );
	}

	/** Filas del bloque items: cant, producto e importe por línea de pedido. */
	private static function order_item_rows( \WC_Order $order ): array {
		$rows = array();

		foreach ( $order->get_items() as $item ) {
			$rows[] = array(
				(string) $item->get_quantity(),
				(string) $item->get_name(),
				number_format_i18n( (float) $item->get_total(), 2 ),
			);
		}

		return $rows;
	}

	/** Totales del pedido: subtotal, envío (si lo hay) y TOTAL en negrita. */
	private static function order_totals( \WC_Order $order ): array {
		$totals   = array( self::total_item( __( 'Subtotal', 'orp-print' ), (float) $order->get_subtotal() ) );
		$shipping = (float) $order->get_shipping_total();

		if ( $shipping > 0.0 ) {
			$totals[] = self::total_item( __( 'Envío', 'orp-print' ), $shipping );
		}

		$totals[] = array(
			'label' => __( 'TOTAL', 'orp-print' ),
			'value' => number_format_i18n( (float) $order->get_total(), 2 ),
			'bold'  => true,
		);

		return $totals;
	}

	/**
	 * Valor del QR: URL de seguimiento del pedido si existe; si no, el nº de
	 * pedido (el ticket de cocina necesita localizar el pedido, no navegar).
	 */
	private static function qr_value( \WC_Order $order ): string {
		$url = (string) $order->get_view_order_url();

		if ( '' !== $url && filter_var( $url, FILTER_VALIDATE_URL ) ) {
			return $url;
		}

		return '#' . $order->get_order_number();
	}

	/** Dirección de entrega en una línea (envío si existe, facturación si no). */
	private static function address_one_line( \WC_Order $order ): string {
		$parts = array_filter(
			array(
				$order->get_shipping_address_1(),
				$order->get_shipping_address_2(),
				$order->get_shipping_city(),
			)
		);

		if ( ! $parts ) {
			$parts = array_filter(
				array(
					$order->get_billing_address_1(),
					$order->get_billing_city(),
				)
			);
		}

		return implode( ', ', array_map( 'strval', $parts ) );
	}
}
