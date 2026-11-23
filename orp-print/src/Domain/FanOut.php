<?php
/**
 * FanOut — un trabajo por cada salida activa del local (F1-06, Doc/04 §2.4 y §2.8):
 *
 * - Cada salida (orp_destinations, enabled = 1) recibe SU propio trabajo, con
 *   estado, ack y reintento independientes (una impresora caída no arrastra a
 *   las demás).
 * - printer_role y copies quedan CONGELADOS en la fila del trabajo al crearlo:
 *   cambiar las copias de la salida después no altera los trabajos pendientes.
 * - Las salidas desactivadas no llegan aquí: el llamador consulta con
 *   only_enabled = true (una salida desactivada tampoco puede generar trabajo
 *   nuevo y sus pendientes se cancelan en DestinationRepository::upsert()).
 *
 * V1: todas las salidas imprimen el MISMO documento (Doc/04 §2.8); el payload
 * solo difiere en meta.copies y meta.printer_role. Lo usan el disparo
 * automático (OrderTrigger, F1-05) y la reimpresión manual (OrderActions).
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Domain;

use Orvita\PrintBridge\Template\DocumentBuilder;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class FanOut {

	/**
	 * Crea un trabajo por cada salida activa del local con copias congeladas.
	 *
	 * @param \WC_Order     $order        Pedido.
	 * @param object        $location     Local resuelto (fila de orp_locations).
	 * @param array<object> $destinations Salidas activas del local (DestinationRepository::for_location).
	 * @param string        $trigger      «created», el slug del estado o «seed» (solo modo automático).
	 * @param bool          $manual       Modo reimpresión manual: clave con timestamp y usuario.
	 * @param int           $user_id      Usuario que lanza la reimpresión (solo modo manual).
	 * @return array<int>      Ids de los trabajos NUEVOS creados (los deduplicados no se listan).
	 */
	public static function create_jobs(
		\WC_Order $order,
		object $location,
		array $destinations,
		string $trigger = 'created',
		bool $manual = false,
		int $user_id = 0
	): array {
		$created = array();

		foreach ( $destinations as $destination ) {
			// Copias y rol CONGELADOS de la salida al crear el trabajo (Doc/04 §2.4).
			$copies = max( 1, (int) $destination->copies );
			$role   = (string) $destination->role;

			$document = DocumentBuilder::build_from_order(
				$order,
				array(
					'order_id'      => (int) $order->get_id(),
					'location_code' => (string) $location->code,
					'copies'        => $copies,
					'printer_role'  => $role,
				)
			);

			$job_id = $manual
				? JobFactory::create_manual( (int) $location->id, (int) $order->get_id(), $role, $copies, $document, $user_id )
				: JobFactory::create( (int) $location->id, (int) $order->get_id(), $role, $copies, $document, $trigger );

			if ( null !== $job_id ) {
				$created[] = $job_id;
			}
		}

		return $created;
	}
}
