<?php
/**
 * Presets — las 4 combinaciones típicas de ajustes (Doc/03 §15).
 *
 * Principio: un preset es **configuración**, no un condicional de código.
 * Elegir un preset escribe los valores típicos de esa combinación en
 * orp_settings; después el cliente ajusta la capa de instancia a mano.
 *
 * Nota de alcance: las partes de plantilla (mínima, completa, dos plantillas)
 * y de destinos (1 o 2 salidas) se aplicarán en F1-03 y F1-06.
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
				'pending_in'      => __( 'Plantilla mínima (F1-03) y 1 salida (F1-06).', 'orp-print' ),
			),
			'completo'        => array(
				'label'       => __( 'Completo', 'orp-print' ),
				'description' => __( 'Ticket con todos los campos en una sola salida, al crear el pedido.', 'orp-print' ),
				'settings'    => array(
					'trigger_states' => array( 'created' ),
					'dedupe_scope'   => 'order',
					'paper_width_mm' => 80,
					'encoding'       => 'CP850',
					'cut_mode'       => 'partial',
					'open_drawer'    => false,
					'poll_interval'  => 10,
					'sound_alert'    => false,
				),
				'pending_in'  => __( 'Plantilla con todos los campos (F1-03) y 1 salida (F1-06).', 'orp-print' ),
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
				'pending_in'  => __( 'Las dos plantillas (F1-03) y las 2 salidas (F1-06).', 'orp-print' ),
			),
			'personalizado'   => array(
				'label'       => __( 'Personalizado', 'orp-print' ),
				'description' => __( 'Sin cambios automáticos: los ajustes de instancia mandan.', 'orp-print' ),
				'settings'    => array(),
				'pending_in'  => '',
			),
		);
	}

	/** Datos de un preset concreto, o null si no existe. */
	public static function get( string $key ): ?array {
		return self::all()[ $key ] ?? null;
	}
}
