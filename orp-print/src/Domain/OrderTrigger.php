<?php
/**
 * OrderTrigger — disparo automático de trabajos desde el pedido (F1-05, Doc/03 §5.1):
 *
 * Hooks enganchados (la dedupe por idempotency_key protege de los dobles
 * disparos — Doc/06 §11):
 * - woocommerce_new_order: creación del pedido. Es el disparador correcto del
 *   piloto: con la pasarela COD el pedido nace en on-hold y no hay cambio de
 *   estado posterior (Doc/05 §5).
 * - woocommerce_checkout_order_processed: checkout clásico.
 * - woocommerce_store_api_checkout_order_processed: checkout de bloques (una
 *   sola petición dispara los dos caminos en algunas configuraciones).
 * - woocommerce_order_status_changed: estados configurados en trigger_states.
 *
 * Semántica de creación: el checkout de bloques crea antes un pedido
 * BORRADOR (checkout-draft) al abrir la caja; disparar ahí imprimiría
 * carritos abandonados. Un borrador no es «pedido creado»: los hooks de
 * new_order lo ignoran y la transición borrador → estado real entra por el
 * cambio de estado CON TRIGGER «created» (es la creación efectiva), de modo
 * que la dedupe la funde con el disparo del hook de checkout.
 *
 * Cancelación (Doc/05 §4.1: «Pedido anulado antes de imprimir → trabajo
 * cancelled, sin papel»): al pasar el pedido a cancelled, los trabajos
 * pendientes del pedido se cancelan. Un trabajo claimed ya está en el agente
 * (no se puede desimprimir): solo se tocan los pending.
 *
 * Cambiar una regla después no altera los trabajos ya encolados: el local
 * queda congelado en la fila de orp_jobs (location_id), Doc/05 §3.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Domain;

use Orvita\PrintBridge\Support\Logger;
use Orvita\PrintBridge\Support\Settings;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class OrderTrigger {

	/** Registra los hooks de disparo y cancelación. */
	public static function register(): void {
		add_action( 'woocommerce_new_order', array( self::class, 'on_new_order' ), 10, 2 );
		add_action( 'woocommerce_checkout_order_processed', array( self::class, 'on_classic_checkout' ), 10, 3 );
		add_action( 'woocommerce_store_api_checkout_order_processed', array( self::class, 'on_store_api_checkout' ), 10, 1 );
		add_action( 'woocommerce_order_status_changed', array( self::class, 'on_status_changed' ), 10, 4 );
	}

	/*
	 ---------------------------------------------------------------------
	 * Hooks.
	 * ------------------------------------------------------------------- */

	/**
	 * Creación del pedido (piloto COD: nace en on-hold y el disparador es la
	 * creación). Los borradores (checkout de bloques, alta manual) no son
	 * pedidos creados: los captura el cambio de estado como «created».
	 *
	 * @param int           $order_id Id del pedido.
	 * @param \WC_Order|int $order    Pedido (Woo 6.x pasa el objeto con HPOS).
	 */
	public static function on_new_order( int $order_id, $order = null ): void {
		$wc_order = self::resolve_order( $order_id, $order );

		if ( null === $wc_order ) {
			return;
		}

		if ( self::is_draft_status( (string) $wc_order->get_status() ) ) {
			return;
		}

		self::dispatch( $wc_order, 'created' );
	}

	/** Checkout clásico. */
	public static function on_classic_checkout( $order_id, $posted_data = array(), $order = null ): void {
		unset( $posted_data );

		$wc_order = self::resolve_order( (int) $order_id, $order );

		if ( null === $wc_order ) {
			return;
		}

		self::dispatch( $wc_order, 'created' );
	}

	/** Checkout de bloques (Store API). La dedupe cubre el doble disparo. */
	public static function on_store_api_checkout( $order, ...$extra ): void {
		unset( $extra );

		if ( ! $order instanceof \WC_Order ) {
			return;
		}

		self::dispatch( $order, 'created' );
	}

	/**
	 * Cambio de estado: si el estado nuevo está configurado en trigger_states
	 * se dispara; si el pedido pasa a cancelled, los trabajos pendientes se
	 * cancelan (Doc/05 §4.1). Una transición que NACE de un borrador es la
	 * creación efectiva del pedido: dispara con «created».
	 *
	 * @param int            $order_id  Id del pedido.
	 * @param string         $old_status Estado anterior.
	 * @param string         $new_status Estado nuevo.
	 * @param \WC_Order|null $order     Pedido (Woo pasa el objeto con HPOS).
	 */
	public static function on_status_changed( $order_id, $old_status, $new_status, $order = null ): void {
		$wc_order = self::resolve_order( (int) $order_id, $order );

		if ( null === $wc_order ) {
			return;
		}

		// Pedido anulado antes de imprimir: sin papel.
		if ( 'cancelled' === $new_status ) {
			self::cancel_pending_jobs( $wc_order );
			return;
		}

		// La salida del borrador ES la creación del pedido: trigger «created»,
		// la misma clave que el hook de checkout. Así el checkout de bloques
		// nunca imprime dos veces (borrador → real + hook de checkout).
		$old_is_draft = self::is_draft_status( (string) $old_status );

		$states = (array) Settings::get( 'trigger_states', array( 'created' ) );

		if ( in_array( (string) $new_status, array_map( 'strval', $states ), true ) ) {
			self::dispatch( $wc_order, $old_is_draft ? 'created' : (string) $new_status );
			return;
		}

		if ( $old_is_draft && ! in_array( (string) $new_status, array_map( 'strval', $states ), true ) ) {
			// Del borrador a un estado NO configurado (p. ej. pending): es la
			// creación del pedido con el checkout de bloques. Dispara igual.
			self::dispatch( $wc_order, 'created' );
		}
	}

	/*
	 ---------------------------------------------------------------------
	 * Flujo de disparo.
	 * ------------------------------------------------------------------- */

	/**
	 * Resuelve local → salidas activas → documento → un trabajo por salida.
	 * Nunca en silencio: sin local asignable queda log + aviso en el panel y
	 * no se crean trabajos.
	 */
	private static function dispatch( \WC_Order $order, string $trigger ): void {
		$location = LocationResolver::resolve_for_order( $order );

		if ( null === $location ) {
			// Sin local coincidente y sin local por defecto: log (ya lo dejó
			// el resolver) + aviso en el panel; el pedido no se encola.
			return;
		}

		$destinations = DestinationRepository::for_location( (int) $location->id, true );

		if ( array() === $destinations ) {
			// Sin salidas activas la cola no crece sin agente (Doc/03 §5.5).
			Logger::log(
				'info',
				'jobs',
				'Pedido sin salidas activas en su local: no se crean trabajos (la cola no crece sin agente).',
				null,
				null,
				array(
					'order_id'    => (int) $order->get_id(),
					'location_id' => (int) $location->id,
					'trigger'     => $trigger,
				)
			);
			return;
		}

		$created = FanOut::create_jobs( $order, $location, $destinations, $trigger );

		// Nota en el pedido: queda en la ficha, visible siempre (HPOS: API CRUD).
		if ( array() !== $created ) {
			$order->add_order_note(
				sprintf(
					/* translators: 1: lista de ids de trabajo, 2: nombre del local, 3: disparo. */
					__( 'Orvita Print: trabajos %1$s encolados (local %2$s, disparo: %3$s).', 'orp-print' ),
					implode( ', ', $created ),
					(string) $location->name,
					$trigger
				)
			);
		}

		Logger::log(
			'info',
			'jobs',
			'Disparo procesado: un trabajo por salida activa del local.',
			null,
			null,
			array(
				'order_id' => (int) $order->get_id(),
				'location' => (string) $location->code,
				'trigger'  => $trigger,
				'created'  => $created,
				'dedupe'   => (string) Settings::get( 'dedupe_scope', 'order' ),
			)
		);
	}

	/** Cancela los trabajos pendientes de un pedido anulado (Doc/05 §4.1). */
	private static function cancel_pending_jobs( \WC_Order $order ): void {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_jobs';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$affected = $wpdb->query(
			$wpdb->prepare(
				"UPDATE {$table} SET status = 'cancelled' WHERE order_id = %d AND status = 'pending'",
				(int) $order->get_id()
			)
		);

		if ( (int) $affected > 0 ) {
			Logger::log(
				'warning',
				'jobs',
				'Pedido anulado: trabajos pendientes cancelados, sin papel.',
				null,
				null,
				array(
					'order_id'   => (int) $order->get_id(),
					'cancelados' => (int) $affected,
				)
			);

			$order->add_order_note(
				sprintf(
					/* translators: %d: número de trabajos cancelados. */
					__( 'Orvita Print: pedido anulado; %d trabajo(s) pendiente(s) cancelado(s), sin papel.', 'orp-print' ),
					(int) $affected
				)
			);
		}
	}

	/*
	 ---------------------------------------------------------------------
	 * Utilidades.
	 * ------------------------------------------------------------------- */

	/**
	 * Resuelve el pedido a partir del id y del objeto del hook, si vino.
	 * Todo por la API CRUD (HPOS): nunca get_post_meta.
	 */
	private static function resolve_order( int $order_id, $order ): ?\WC_Order {
		if ( $order instanceof \WC_Order && $order->get_id() === $order_id ) {
			return $order;
		}

		$wc_order = wc_get_order( $order_id );

		// wc_get_order devuelve false o null para ids inexistentes y objetos
		// de otros tipos (reembolsos…) que no son WC_Order.
		return ( $wc_order instanceof \WC_Order ) ? $wc_order : null;
	}

	/** ¿Es un estado de borrador (aún no es un pedido real)? */
	private static function is_draft_status( string $status ): bool {
		return in_array( $status, array( 'checkout-draft', 'auto-draft', 'draft', 'new' ), true );
	}
}
