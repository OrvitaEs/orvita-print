<?php
/**
 * Presets — las 4 combinaciones típicas de ajustes (Doc/03 §15).
 *
 * Principio: un preset es **configuración**, no un condicional de código.
 * Elegir un preset escribe los valores típicos de esa combinación en
 * orp_settings; después el cliente ajusta la capa de instancia a mano.
 *
 * Nota de alcance: los destinos (1 o 2 salidas) se aplican en F1-06. Las
 * plantillas por rol («Cocina + cliente») quedan fuera de V1 (Doc/04 §2.8):
 * en V1 hay un documento por pedido y la plantilla única vigente.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Support;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class Presets {

	/** Identificadores válidos. */
	public const KEYS = array( 'aviso', 'completo', 'cocina_cliente', 'personalizado' );

	/** Definición de cada preset: ajustes que sobrescribe al aplicarlo. */
	public static function all(): array {
		return array(
			'aviso'           => array(
				'label'           => __( 'Aviso', 'orp-print' ),
				'description'     => __( 'Lo más simple: un aviso por pedido en una salida, al crear el pedido. Preset por defecto de una instalación nueva.', 'orp-print' ),
				'settings'        => array(
					'trigger_states' => array( 'created' ),
					'dedupe_scope'   => 'order',
					'paper_width_mm' => 80,
					'encoding'       => 'CP850',
					'cut_mode'       => 'partial',
					'open_drawer'    => false,
					'poll_interval'  => 10,
					'sound_alert'    => false,
				),
				'template_fields' => array(
					'business_name' => true,
					'datetime'      => true,
					'order_number'  => true,
					'items'         => true,
					'totals'        => true,
				),
				'pending_in'      => __( '1 salida (F1-06).', 'orp-print' ),
			),
			'completo'        => array(
				'label'           => __( 'Completo', 'orp-print' ),
				'description'     => __( 'Ticket con todos los campos en una sola salida, al crear el pedido.', 'orp-print' ),
				'settings'        => array(
					'trigger_states' => array( 'created' ),
					'dedupe_scope'   => 'order',
					'paper_width_mm' => 80,
					'encoding'       => 'CP850',
					'cut_mode'       => 'partial',
					'open_drawer'    => false,
					'poll_interval'  => 10,
					'sound_alert'    => false,
				),
				'template_fields' => array(
					'logo'          => true,
					'business_name' => true,
					'datetime'      => true,
					'order_number'  => true,
					'customer'      => true,
					'phone'         => true,
					'address'       => true,
					'items'         => true,
					'totals'        => true,
					'qr'            => true,
					'footer'        => true,
					'not_invoice'   => true,
				),
				'pending_in'  => __( '1 salida (F1-06).', 'orp-print' ),
			),
			'cocina_cliente'  => array(
				'label'       => __( 'Cocina + cliente', 'orp-print' ),
				'description' => __( 'Dos salidas y un trabajo por cada disparo: la comanda se reimprime al pasar el pedido a preparación.', 'orp-print' ),
				'settings'    => array(
					'trigger_states' => array( 'created', 'processing' ),
					'dedupe_scope'   => 'trigger',
					'paper_width_mm' => 80,
					'encoding'       => 'CP850',
					'cut_mode'       => 'full',
					'open_drawer'    => false,
					'poll_interval'  => 10,
					'sound_alert'    => true,
				),
				'pending_in'  => __( 'Las dos plantillas por rol (fuera de V1, Doc/04 §2.8) y las 2 salidas (F1-06): en V1 se aplica la plantilla vigente única.', 'orp-print' ),
			),
			'personalizado'   => array(
				'label'       => __( 'Personalizado', 'orp-print' ),
				'description' => __( 'Sin cambios automáticos: los ajustes de instancia mandan.', 'orp-print' ),
				'settings'    => array(),
				'pending_in'  => '',
			),		);
	}

	/** Datos de un preset concreto, o null si no existe. */
	public static function get( string $key ): ?array {
		return self::all()[ $key ] ?? null;
	}
}
