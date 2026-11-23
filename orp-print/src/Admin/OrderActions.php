<?php
/**
 * Acciones sobre el pedido (F1-04, Doc/03 §5.3):
 *
 * - Botón «Ver ticket» (woocommerce_admin_order_actions): abre la vista
 *   previa imprimible. NO crea trabajo.
 * - Acción «Reimprimir ticket» (woocommerce_order_actions +
 *   woocommerce_order_action_orp_reprint): reimpresión manual que SÍ genera
 *   trabajo nuevo con clave sha256(order_id | manual | role | timestamp |
 *   user_id), Doc/04 §3.
 *
 * V1: todas las salidas imprimen el mismo documento (Doc/04 §2.8); la
 * reimpresión crea un trabajo por cada salida activa del local del pedido.
 * Sin salidas activas no se crea ninguno (Doc/03 §5.5: la cola no crece sin
 * agente) y se sugiere imprimir desde el navegador.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Admin;

use Orvita\PrintBridge\Domain\DestinationRepository;
use Orvita\PrintBridge\Domain\JobFactory;
use Orvita\PrintBridge\Domain\LocationResolver;
use Orvita\PrintBridge\Support\Logger;
use Orvita\PrintBridge\Template\DocumentBuilder;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class OrderActions {

	/** Transient con el resultado de la reimpresión para el aviso del panel. */
	private const RESULT_TRANSIENT = 'orp_reprint_result';

	/** Registra los hooks. */
	public static function register(): void {
		// Botón «Ver ticket» en la tabla de pedidos (fila de acciones).
		add_filter( 'woocommerce_admin_order_actions', array( self::class, 'add_view_ticket_button' ), 10, 2 );
		// «Ver ticket» y «Reimprimir ticket» en el desplegable de la ficha.
		add_filter( 'woocommerce_order_actions', array( self::class, 'add_order_actions' ) );
		add_action( 'woocommerce_order_action_orp_view_ticket', array( self::class, 'handle_view_ticket' ) );
		add_action( 'woocommerce_order_action_orp_reprint', array( self::class, 'handle_reprint' ) );
	}

	/**
	 * Botón «Ver ticket» en la tabla de pedidos (solo con capacidad Orvita).
	 *
	 * @param array<string, array<string, mixed>> $actions Acciones existentes.
	 * @param \WC_Order|null                      $order   Pedido de la fila (Woo lo pasa desde 6.7).
	 */
	public static function add_view_ticket_button( array $actions, $order = null ): array {
		if ( ! current_user_can( ORP_CAPABILITY ) || ! current_user_can( 'edit_shop_orders' ) ) {
			return $actions;
		}

		if ( ! $order instanceof \WC_Order ) {
			return $actions;
		}

		$actions['orp_view_ticket'] = array(
			'url'    => admin_url( 'admin.php?page=' . Menu::SLUG_PREVIEW . '&order_id=' . $order->get_id() ),
			'name'   => __( 'Ver ticket', 'orp-print' ),
			'action' => 'orp_view_ticket',
		);

		return $actions;
	}

	/**
	 * Acciones en el desplegable de la ficha del pedido: «Ver ticket» (abre
	 * la vista previa; NO crea trabajo) y «Reimprimir ticket» (crea trabajo).
	 */
	public static function add_order_actions( array $actions ): array {
		if ( ! current_user_can( ORP_CAPABILITY ) ) {
			return $actions;
		}

		$actions['orp_view_ticket'] = __( 'Ver ticket (Orvita Print)', 'orp-print' );
		$actions['orp_reprint']     = __( 'Reimprimir ticket (Orvita Print)', 'orp-print' );

		return $actions;
	}

	/** «Ver ticket»: redirige a la vista previa imprimible y termina. */
	public static function handle_view_ticket( \WC_Order $order ): void {
		if ( ! current_user_can( ORP_CAPABILITY ) ) {
			return;
		}

		wp_safe_redirect(
			admin_url( 'admin.php?page=' . Menu::SLUG_PREVIEW . '&order_id=' . $order->get_id() )
		);
		exit;
	}

	/**
	 * Ejecuta la reimpresión manual: documento con la plantilla vigente y un
	 * trabajo por salida activa del local (copias congeladas).
	 */
	public static function handle_reprint( \WC_Order $order ): void {
		$order_id = $order->get_id();

		$location = LocationResolver::resolve_for_order( $order );

		if ( null === $location ) {
			Logger::log(
				'warning',
				'jobs',
				'Reimpresión manual sin local asignable: no se creó ningún trabajo.',
				null,
				null,
				array( 'order_id' => $order_id )
			);

			self::store_result(
				sprintf(
					/* translators: %d: id de pedido. */
					__( 'Reimpresión del pedido #%d cancelada: el pedido no tiene local asignable (revisa Locales y reglas o el local por defecto).', 'orp-print' ),
					$order_id
				),
				'warning'
			);
			return;
		}

		$destinations = DestinationRepository::for_location( (int) $location->id, true );

		if ( array() === $destinations ) {
			Logger::log(
				'info',
				'jobs',
				'Reimpresión manual sin salidas activas: la cola no crece sin agente.',
				null,
				null,
				array( 'order_id' => $order_id, 'location_id' => (int) $location->id )
			);

			self::store_result(
				sprintf(
					/* translators: %1$d: id de pedido, %2$s: nombre del local. */
					__( 'El local «%2$s» no tiene salidas activas: no se creó ningún trabajo para el pedido #%1$d. Usa la vista previa e imprime desde el navegador.', 'orp-print' ),
					$order_id,
					(string) $location->name
				),
				'warning'
			);
			return;
		}

		$user_id = (int) get_current_user_id();
		$created = array();

		foreach ( $destinations as $destination ) {
			$document = DocumentBuilder::build_from_order(
				$order,
				array(
					'order_id'      => $order_id,
					'location_code' => (string) $location->code,
					'copies'        => (int) $destination->copies,
					'printer_role'  => (string) $destination->role,
				)
			);

			$job_id = JobFactory::create_manual(
				(int) $location->id,
				$order_id,
				(string) $destination->role,
				(int) $destination->copies,
				$document,
				$user_id
			);

			if ( null !== $job_id ) {
				$created[] = $job_id;
			}
		}

		// Nota en el pedido: el registro queda en la ficha, visible siempre.
		$job_list = array() !== $created ? implode( ', ', $created ) : __( 'ninguno nuevo (idempotencia)', 'orp-print' );
		$order->add_order_note(
			sprintf(
				/* translators: 1: lista de ids de trabajo, 2: nombre del local. */
				__( 'Orvita Print: reimpresión manual → trabajos #%1$s (local %2$s).', 'orp-print' ),
				$job_list,
				(string) $location->name
			)
		);

		Logger::log(
			'info',
			'jobs',
			'Reimpresión manual completada.',
			null,
			null,
			array(
				'order_id'   => $order_id,
				'location'   => (string) $location->code,
				'job_ids'    => $created,
				'user_id'    => $user_id,
			)
		);

		self::store_result(
			$created
				? sprintf(
					/* translators: 1: id de pedido, 2: lista de trabajos. */
					__( 'Reimpresión del pedido #%1$d encolada: trabajos %2$s.', 'orp-print' ),
					$order_id,
					implode( ', ', $created )
				)
				: sprintf(
					/* translators: %d: id de pedido. */
					__( 'La reimpresión del pedido #%d no creó trabajos nuevos (ya existían).', 'orp-print' ),
					$order_id
				),
			$created ? 'success' : 'info'
		);
	}

	/** Resultado de la reimpresión pendiente de mostrar en el panel. */
	public static function take_result(): ?array {
		$result = get_transient( self::RESULT_TRANSIENT );

		if ( ! is_array( $result ) ) {
			return null;
		}

		delete_transient( self::RESULT_TRANSIENT );

		return $result;
	}

	/** Guarda el resultado para el aviso (se consume una sola vez). */
	private static function store_result( string $message, string $type ): void {
		set_transient(
			self::RESULT_TRANSIENT,
			array(
				'message' => $message,
				'type'    => $type,
			),
			MINUTE_IN_SECONDS * 5
		);
	}
}
