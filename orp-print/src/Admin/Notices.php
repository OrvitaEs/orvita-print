<?php
/**
 * Avisos del panel (Doc/03 §5.3): cuando un pedido acaba en el local por
 * defecto sin que ninguna regla coincida, se muestra un aviso una vez.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Admin;

use Orvita\PrintBridge\Domain\LocationResolver;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class Notices {

	/** Registra el hook de avisos. */
	public static function register(): void {
		add_action( 'admin_notices', array( self::class, 'render' ) );
	}

	/** Aviso de pedidos que cayeron al local por defecto sin regla coincidente. */
	public static function render(): void {
		if ( ! current_user_can( ORP_CAPABILITY ) ) {
			return;
		}

		// Resultado de la reimpresión manual (F1-04): se consume una sola vez.
		$reprint = OrderActions::take_result();

		if ( null !== $reprint ) {
			printf(
				'<div class="notice notice-%1$s is-dismissible"><p><strong>%2$s</strong> %3$s</p></div>',
				esc_attr( 'success' === $reprint['type'] ? 'success' : ( 'warning' === $reprint['type'] ? 'warning' : 'info' ) ),
				esc_html__( 'Orvita Print:', 'orp-print' ),
				esc_html( (string) $reprint['message'] )
			);
		}

		$entries = LocationResolver::take_fallback_notices();

		if ( array() !== $entries ) {
			$order_ids = array_column( $entries, 'order_id' );
			$codes     = array_unique( array_column( $entries, 'location_code' ) );

			printf(
				'<div class="notice notice-warning is-dismissible"><p><strong>%1$s</strong> ',
				esc_html__( 'Orvita Print:', 'orp-print' )
			);
			printf(
				/* translators: 1: lista de ids de pedido, 2: código del local por defecto. */
				esc_html__(
					'los pedidos %1$s no coinciden con ninguna regla de local y han ido al local por defecto (%2$s). Revisa las reglas de la pestaña «Locales y reglas» si este no es el comportamiento esperado.',
					'orp-print'
				),
				esc_html( implode( ', ', array_map( 'strval', $order_ids ) ) ),
				esc_html( implode( ', ', $codes ) )
			);
			echo '</p></div>';
		}

		// Pedidos que NO se encolaron por falta de local por defecto (F1-05).
		$missing = LocationResolver::take_missing_default_notices();

		if ( array() === $missing ) {
			return;
		}

		printf(
			'<div class="notice notice-error is-dismissible"><p><strong>%1$s</strong> ',
			esc_html__( 'Orvita Print:', 'orp-print' )
		);
		printf(
			/* translators: %s: lista de ids de pedido. */
			esc_html__(
				'los pedidos %s NO se han encolado: no hay local por defecto configurado y ninguna regla coincidía. Configura un local por defecto en Ajustes o revisa las reglas; mientras tanto esos pedidos no generan papel.',
				'orp-print'
			),
			esc_html( implode( ', ', array_map( 'strval', array_column( $missing, 'order_id' ) ) ) )
		);
		echo '</p></div>';
	}
}
