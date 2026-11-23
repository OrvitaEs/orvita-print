<?php
/**
 * Página «Vista previa» (F1-04, Doc/03 §5.5).
 *
 * HTML imprimible generado por el MISMO DocumentBuilder que alimenta la cola:
 * por pedido real (botón «Ver ticket» de la ficha del pedido) o con datos de
 * demostración (desde la página de plantilla).
 *
 * La vista previa se sirve limpia (sin chrome de wp-admin, sin recursos
 * externos) para que «Imprimir» → window.print() salga a 80/58 mm sin romper
 * el diseño. NO crea trabajo en la cola: es acción del navegador.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Admin;

use Orvita\PrintBridge\Template\DocumentBuilder;
use Orvita\PrintBridge\Template\TicketHtml;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class PreviewPage {

	/** Registra la página y su render temprano (antes del chrome de wp-admin). */
	public static function register(): void {
		// add_submenu_page solo es válido dentro de admin_menu; el HTML sale
		// completo vía load-{hook}, sin chrome ni recursos externos.
		add_action( 'admin_menu', array( self::class, 'add_page' ) );
	}

	/** Añade el submenú y engancha el render limpio. */
	public static function add_page(): void {
		$hook = add_submenu_page(
			Menu::SLUG_SETTINGS,
			__( 'Vista previa del ticket', 'orp-print' ),
			__( 'Vista previa', 'orp-print' ),
			ORP_CAPABILITY,
			Menu::SLUG_PREVIEW,
			'__return_null' // El contenido lo pinta el hook load- con exit.
		);

		if ( is_string( $hook ) && '' !== $hook ) {
			add_action( 'load-' . $hook, array( self::class, 'render' ) );
		}
	}

	/**
	 * Renderiza el HTML completo y termina. WooCommerce no es necesario para
	 * la demo: el plugin es útil sin agente y sin pedidos (Doc/03 §5.5).
	 */
	public static function render(): void {
		if ( ! current_user_can( ORP_CAPABILITY ) ) {
			wp_die( esc_html__( 'No tienes permisos para ver esta página.', 'orp-print' ) );
		}

		$order_id = isset( $_GET['order_id'] ) ? absint( wp_unslash( $_GET['order_id'] ) ) : 0; // phpcs:ignore WordPress.Security.NonceVerification.Recommended -- Solo lectura y con control de capacidad.

		if ( $order_id > 0 && class_exists( \WC_Order::class ) ) {
			$order = \wc_get_order( $order_id );

			if ( $order instanceof \WC_Order ) {
				$document = DocumentBuilder::build_from_order(
					$order,
					array(
						'order_id'     => $order->get_id(),
						'printer_role' => 'preview',
					)
				);
				/* translators: %s: número de pedido. */
				$title = sprintf( __( 'Ticket · Pedido %s', 'orp-print' ), $order->get_order_number() );

				self::output( $document, $title );
			}

			self::output_error(
				sprintf(
					/* translators: %d: id de pedido no encontrado. */
					__( 'No se encuentra el pedido #%d.', 'orp-print' ),
					$order_id
				)
			);
		}

		// Datos de ejemplo: misma plantilla, mismos bloques, sin pedido real.
		self::output(
			DocumentBuilder::build_demo(
				array(
					'order_id'     => 0,
					'printer_role' => 'preview',
				)
			),
			__( 'Ticket · Vista previa de ejemplo', 'orp-print' )
		);
	}

	/** Envia el HTML del ticket y termina la ejecución. */
	private static function output( array $document, string $title ): void {
		// phpcs:ignore WordPress.Security.EscapeOutput.OutputNotEscaped -- TicketHtml escapa todo el contenido dinámico.
		echo TicketHtml::render( $document, $title );
		exit;
	}

	/** Página de error mínima (también sin recursos externos). */
	private static function output_error( string $message ): void {
		printf(
			'<!doctype html><html lang="es"><head><meta charset="utf-8"><title>%s</title></head><body style="font-family:monospace;padding:2em"><p>%s</p><p><a href="javascript:history.back()">« volver</a></p></body></html>',
			esc_html__( 'Vista previa del ticket', 'orp-print' ),
			esc_html( $message )
		);
		exit;
	}
}
