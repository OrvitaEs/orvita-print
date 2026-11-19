<?php
/**
 * DocumentBuilder — documento neutro v1 (Doc/04 §7).
 *
 * En F0 genera los bloques base (text, kv, items, totals, divider, feed, cut)
 * a partir de un pedido de WooCommerce o un documento de demostración para la
 * siembra. La plantilla configurable completa llega en F1-03.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Domain;

use Orvita\PrintBridge\Support\Clock;
use Orvita\PrintBridge\Support\Settings;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class DocumentBuilder {

	/** Versión del documento neutro (el agente rechaza versiones desconocidas). */
	public const DOCUMENT_VERSION = 1;

	/**
	 * Construye el documento a partir de un pedido (Doc/04 §7.3).
	 * Todo el acceso al pedido es por la API CRUD, nunca get_post_meta.
	 *
	 * @param \WC_Order $order Pedido.
	 * @param array     $meta  meta del documento: job_id, order_id, location_code, copies, printer_role.
	 */
	public static function build_from_order( \WC_Order $order, array $meta ): array {
		$meta   = self::build_meta( $meta, $order );
		$blocks = array();

		$blocks[] = self::block_text( (string) get_bloginfo( 'name' ), 'center', true, 'double' );
		$blocks[] = self::block_text( wp_date( 'd/m/Y H:i' ), 'center' );
		$blocks[] = self::block_divider();

		$blocks[] = array(
			'type' => 'kv',
			'items' => array(
				self::kv_item( __( 'Pedido', 'orp-print' ), '#' . $order->get_order_number() ),
				self::kv_item(
					__( 'Cliente', 'orp-print' ),
					trim( $order->get_billing_first_name() . ' ' . $order->get_billing_last_name() )
				),
				self::kv_item( __( 'Teléfono', 'orp-print' ), (string) $order->get_billing_phone() ),
				self::kv_item( __( 'Entrega', 'orp-print' ), self::address_one_line( $order ) ),
			),
		);
		$blocks[] = self::block_divider();

		// Líneas de pedido.
		$rows = array();
		foreach ( $order->get_items() as $item ) {
			$rows[] = array(
				(string) $item->get_quantity(),
				(string) $item->get_name(),
				number_format_i18n( (float) $item->get_total(), 2 ),
			);
		}
		if ( $rows ) {
			$blocks[] = array(
				'type'    => 'items',
				'columns' => array( __( 'Cant', 'orp-print' ), __( 'Producto', 'orp-print' ), __( 'Importe', 'orp-print' ) ),
				'align'   => array( 'left', 'left', 'right' ),
				'rows'    => $rows,
			);
		}

		// Totales.
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
		$blocks[] = array( 'type' => 'totals', 'items' => $totals );

		$blocks[] = self::block_text( __( 'Gracias por su pedido', 'orp-print' ), 'center' );
		$blocks[] = array( 'type' => 'feed', 'lines' => 3 );
		$blocks[] = array( 'type' => 'cut', 'mode' => (string) Settings::get( 'cut_mode', 'partial' ) );

		return self::envelope( $meta, $blocks );
	}

	/**
	 * Documento de demostración para `wp orp seed` y las pruebas: mismo
	 * formato que Doc/04 §7.3 sin pedido real detrás.
	 *
	 * @param array $meta meta del documento (order_id normalmente 0).
	 */
	public static function build_demo( array $meta ): array {
		$meta   = self::build_meta( $meta, null );
		$blocks = array(
			self::block_text( __( 'ORVITA PRINT · DEMO', 'orp-print' ), 'center', true, 'double' ),
			self::block_text( __( 'Trabajo de prueba de la siembra', 'orp-print' ), 'center' ),
			self::block_divider(),
			array(
				'type' => 'kv',
				'items' => array(
					self::kv_item( __( 'Pedido', 'orp-print' ), __( '(demo, sin pedido real)', 'orp-print' ) ),
					self::kv_item( __( 'Cliente', 'orp-print' ), 'María López' ),
					self::kv_item( __( 'Teléfono', 'orp-print' ), '600 000 000' ),
					self::kv_item( __( 'Entrega', 'orp-print' ), __( 'Calle Mayor 3', 'orp-print' ) ),
				),
			),
			array(
				'type'    => 'items',
				'columns' => array( __( 'Cant', 'orp-print' ), __( 'Producto', 'orp-print' ), __( 'Importe', 'orp-print' ) ),
				'align'   => array( 'left', 'left', 'right' ),
				'rows'    => array(
					array( '1', 'Pizza Margarita grande', '12,00' ),
					array( '2', 'Pizza Barbacoa', '22,00' ),
				),
			),
			array(
				'type'  => 'totals',
				'items' => array(
					self::total_item( __( 'Subtotal', 'orp-print' ), 34.0 ),
					self::total_item( __( 'Envío', 'orp-print' ), 2.0 ),
					array( 'label' => __( 'TOTAL', 'orp-print' ), 'value' => '36,00', 'bold' => true ),
				),
			),
			self::block_text( __( 'No válido como factura', 'orp-print' ), 'center' ),
			self::block_text( __( 'Gracias por su pedido', 'orp-print' ), 'center' ),
			array( 'type' => 'feed', 'lines' => 3 ),
			array( 'type' => 'cut', 'mode' => (string) Settings::get( 'cut_mode', 'partial' ) ),
		);

		return self::envelope( $meta, $blocks );
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
