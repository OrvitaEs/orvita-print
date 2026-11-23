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

		if ( array() !== $missing ) {
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

		// Agentes activos sin heartbeat reciente (F1-08, Doc/03 §11): más de
		// 3 minutos sin latir, o emparejados y sin llegar a latir nunca.
		$offline = \Orvita\PrintBridge\Domain\Reaper::disconnected_agents();

		if ( array() !== $offline ) {
			printf(
				'<div class="notice notice-warning is-dismissible"><p><strong>%1$s</strong> ',
				esc_html__( 'Orvita Print:', 'orp-print' )
			);
			$names = array();
			foreach ( $offline as $agent ) {
				$names[] = sprintf(
					/* translators: 1: nombre del agente, 2: nombre del local. */
					__( '«%1$s» (local %2$s)', 'orp-print' ),
					(string) $agent->name,
					'' !== (string) ( $agent->location_name ?? '' ) ? (string) $agent->location_name : (string) $agent->id
				);
			}
			printf(
				/* translators: %s: lista de agentes. */
				esc_html__(
					'agentes sin heartbeat desde hace más de 3 minutos: %s. Si el TPV está apagado o sin red, los trabajos esperan en la cola y caducan según su configuración.',
					'orp-print'
				),
				esc_html( implode( ', ', $names ) )
			);
			echo '</p></div>';
		}

		// Trabajos caducados sin leer (F1-09): acumulados por el Reaper y
		// consumidos al abrir la página Cola (donde se pueden reencolar).
		$expired_count = \Orvita\PrintBridge\Domain\Reaper::expired_notice_count();

		if ( $expired_count > 0 ) {
			printf(
				'<div class="notice notice-warning is-dismissible"><p><strong>%1$s</strong> ',
				esc_html__( 'Orvita Print:', 'orp-print' )
			);
			printf(
				/* translators: %d: número de trabajos caducados. */
				esc_html(
					_n(
						'%d trabajo ha caducado sin imprimirse. Ábrelo en la Cola y reencólalo si lo necesitas.',
						'%d trabajos han caducado sin imprimirse. Ábrelos en la Cola y reencólalos si los necesitas.',
						$expired_count,
						'orp-print'
					)
				),
				$expired_count
			);
			echo '</p></div>';
		}
	}
}
