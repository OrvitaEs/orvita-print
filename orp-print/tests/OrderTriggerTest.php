<?php
/**
 * Pruebas del disparo y el fan-out (F1-05 y F1-06):
 * - Pedido nuevo → EXACTAMENTE un trabajo (checkout clásico y doble disparo
 *   del checkout de bloques; la dedupe lo protege).
 * - dedupe_scope = trigger: cambiar al estado configurado → trabajo nuevo;
 *   scope = order → ninguno.
 * - Cancelación del pedido → pending → cancelled (Doc/05 §4.1).
 * - Fan-out: un trabajo por salida activa, copias congeladas; desactivar la
 *   salida cancela sus pendientes y no genera trabajos nuevos.
 * - Sin local por defecto o sin salidas activas: nada en silencio, no se crean
 *   trabajos y queda registro en orp_logs.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Tests;

use Orvita\PrintBridge\Domain\DestinationRepository;
use Orvita\PrintBridge\Domain\LocationRepository;
use Orvita\PrintBridge\Domain\OrderTrigger;
use Orvita\PrintBridge\Support\Clock;
use Orvita\PrintBridge\Support\Settings;

final class OrderTriggerTest extends \WP_UnitTestCase {

	/** @var int Local de pruebas. */
	private int $location_id;

	/** Clave de meta canónica para enrutar el pedido al local de pruebas. */
	private const META_KEY = 'orp_route_trigger_test';

	/** Valor de la meta de enrutado. */
	private const META_VALUE = 'trigger-suite';

	public function set_up(): void {
		parent::set_up();

		global $wpdb;

		// Local por regla order_meta canónica: inmune a filas de otros tests.
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->insert(
			$wpdb->prefix . 'orp_locations',
			array(
				'code'       => 'test-trigger',
				'name'       => 'Local de pruebas del disparo',
				'enabled'    => 1,
				'priority'   => 5,
				'rules'      => wp_json_encode(
					array(
						array(
							'type'     => 'order_meta',
							'operator' => 'in',
							'values'   => array( self::META_KEY, self::META_VALUE ),
						),
					)
				),
				'is_default' => 0,
				'created_at' => Clock::now_sql(),
				'updated_at' => Clock::now_sql(),
			),
			array( '%s', '%s', '%d', '%d', '%s', '%d', '%s', '%s' )
		);
		$this->location_id = (int) $wpdb->insert_id;

		// Estado de ajustes determinista (el rollback de la suite lo restaura).
		Settings::save(
			array(
				'trigger_states'      => array( 'created', 'processing' ),
				'dedupe_scope'        => 'order',
				'default_location_id' => 0,
			)
		);

		// Ajustes son cacheados en memoria por Settings::all() vía get_option;
		// el option cache del test ya refleja la escritura anterior.

		// Sin default ni otros locales marcados: los tests del disparo no
		// deben caer en un local por defecto de otro escenario.
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->query( "UPDATE {$wpdb->prefix}orp_locations SET is_default = 0" );
	}

	/*
	 ---------------------------------------------------------------------
	 * Ayudantes.
	 * ------------------------------------------------------------------- */

	/** ¿Está WooCommerce? Crea sus tablas si faltan. */
	private function woocommerce_or_skip(): void {
		if ( ! class_exists( \WC_Order::class ) ) {
			$this->markTestSkipped( 'WooCommerce no está activo en la suite de pruebas.' );
		}

		if ( ! get_option( 'woocommerce_db_version' ) ) {
			\WC_Install::install();
		}
	}

	/** Salida del local de pruebas. */
	private function seed_destination( string $role, string $printer, int $copies, bool $enabled = true ): void {
		DestinationRepository::upsert(
			$this->location_id,
			array(
				'role'         => $role,
				'label'        => ucfirst( $role ),
				'printer_name' => $printer,
				'copies'       => $copies,
				'enabled'      => $enabled,
			)
		);
	}

	/**
	 * Pedido con los datos ya puestos al crearlo (como en el checkout real):
	 * el hook woocommerce_new_order dispara con los datos presentes.
	 */
	private function make_order( string $status = 'pending' ): \WC_Order {
		$order = new \WC_Order();
		$order->update_meta_data( self::META_KEY, self::META_VALUE );
		$order->set_shipping_city( 'Torrelodones' );
		$order->set_billing_first_name( 'María' );
		if ( 'pending' !== $status ) {
			$order->set_status( $status );
		}
		$order->save();

		$this->assertGreaterThan( 0, $order->get_id() );
		return $order;
	}

	/** Trabajos de un pedido (fila completa). */
	private function jobs_for_order( int $order_id ): array {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		return (array) $wpdb->get_results(
			$wpdb->prepare(
				'SELECT id, location_id, order_id, printer_role, copies, status, idempotency_key
				   FROM ' . $wpdb->prefix . 'orp_jobs
				  WHERE order_id = %d AND location_id = %d
				  ORDER BY id ASC',
				$order_id,
				$this->location_id
			)
		);
	}

	/*
	 ---------------------------------------------------------------------
	 * F1-05: pedido nuevo → exactamente un trabajo.
	 * ------------------------------------------------------------------- */

	/** Creación del pedido dispara una vez: un solo trabajo. */
	public function test_new_order_creates_exactly_one_job(): void {
		$this->woocommerce_or_skip();
		$this->seed_destination( 'default', 'EPSON T20', 1 );

		$order = $this->make_order();

		$jobs = $this->jobs_for_order( (int) $order->get_id() );
		$this->assertCount( 1, $jobs, 'Un pedido nuevo genera exactamente un trabajo.' );
		$this->assertSame( 'pending', $jobs[0]->status );
		$this->assertSame( 'default', $jobs[0]->printer_role );
		$this->assertSame( 1, (int) $jobs[0]->copies );
	}

	/** Doble disparo del checkout de bloques (new_order + ambos hooks): sigue un solo trabajo. */
	public function test_blocks_checkout_double_fire_still_one_job(): void {
		$this->woocommerce_or_skip();
		$this->seed_destination( 'default', 'EPSON T20', 1 );

		$order = $this->make_order(); // ya disparó woocommerce_new_order.

		// La petición del checkout de bloques dispara los dos caminos:
		// Store API y el clásico. La dedupe debe fundirlos.
		do_action( 'woocommerce_store_api_checkout_order_processed', $order );
		do_action( 'woocommerce_checkout_order_processed', $order->get_id(), array(), $order );

		$jobs = $this->jobs_for_order( (int) $order->get_id() );
		$this->assertCount( 1, $jobs, 'El doble disparo del checkout no duplica el trabajo.' );
	}

	/*
	 ---------------------------------------------------------------------
	 * F1-05: dedupe_scope trigger vs order al cambiar de estado.
	 * ------------------------------------------------------------------- */

	/** scope = trigger: pasar a un estado configurado crea trabajo nuevo. */
	public function test_scope_trigger_creates_new_job_on_status_change(): void {
		$this->woocommerce_or_skip();
		$this->seed_destination( 'default', 'EPSON T20', 1 );

		Settings::save( array( 'dedupe_scope' => 'trigger' ) );

		$order = $this->make_order();

		$this->assertCount( 1, $this->jobs_for_order( (int) $order->get_id() ) );

		// Disparo al pasar a processing (está en trigger_states del set_up).
		$order->update_status( 'processing' );

		$jobs = $this->jobs_for_order( (int) $order->get_id() );
		$this->assertCount( 2, $jobs, 'Con scope=trigger el cambio de estado dispara otro trabajo.' );

		// Las claves difieren: el trigger entra en la clave (Doc/04 §3).
		$this->assertNotSame( $jobs[0]->idempotency_key, $jobs[1]->idempotency_key );

		$expected_second = hash(
			'sha256',
			$order->get_id() . '|processing|' . $this->location_id . '|default|' . (int) get_option( 'orp_template_version', 1 )
		);
		$this->assertSame( $expected_second, $jobs[1]->idempotency_key, 'La clave del segundo trabajo lleva el trigger del estado.' );
	}

	/** scope = order: aunque el estado esté configurado, no hay trabajo nuevo. */
	public function test_scope_order_creates_no_job_on_status_change(): void {
		$this->woocommerce_or_skip();
		$this->seed_destination( 'default', 'EPSON T20', 1 );

		// scope = order es el valor del set_up.
		$order = $this->make_order();

		$this->assertCount( 1, $this->jobs_for_order( (int) $order->get_id() ) );

		// 'processing' está en trigger_states pero el scope es order: misma clave.
		$order->update_status( 'processing' );

		$this->assertCount( 1, $this->jobs_for_order( (int) $order->get_id() ), 'Con scope=order un pedido nunca genera dos papeles.' );
	}

	/** Cambio de estado NO configurado con scope=trigger: tampoco dispara. */
	public function test_non_configured_status_does_not_fire(): void {
		$this->woocommerce_or_skip();
		$this->seed_destination( 'default', 'EPSON T20', 1 );

		Settings::save( array( 'dedupe_scope' => 'trigger' ) );

		$order = $this->make_order();

		$order->update_status( 'on-hold' ); // no está en trigger_states.

		$this->assertCount( 1, $this->jobs_for_order( (int) $order->get_id() ) );
	}

	/*
	 ---------------------------------------------------------------------
	 * F1-05: cancelación del pedido → pending → cancelled.
	 * ------------------------------------------------------------------- */

	public function test_order_cancellation_cancels_pending_jobs(): void {
		$this->woocommerce_or_skip();
		$this->seed_destination( 'default', 'EPSON T20', 1 );

		$order = $this->make_order();

		$this->assertCount( 1, $this->jobs_for_order( (int) $order->get_id() ) );

		$order->update_status( 'cancelled' );

		$jobs = $this->jobs_for_order( (int) $order->get_id() );
		$this->assertCount( 1, $jobs );
		$this->assertSame( 'cancelled', $jobs[0]->status, 'Pedido anulado antes de imprimir: trabajo cancelled, sin papel.' );
	}

	/*
	 ---------------------------------------------------------------------
	 * F1-06: fan-out por salidas.
	 * ------------------------------------------------------------------- */

	/** Local con 2 salidas → 2 trabajos, cada uno con su rol y sus copias congeladas. */
	public function test_fanout_creates_one_job_per_active_destination(): void {
		$this->woocommerce_or_skip();
		$this->seed_destination( 'default', 'EPSON T20', 1 );
		$this->seed_destination( 'cocina', 'XPRINTER 80C', 3 );

		$order = $this->make_order();

		$jobs = $this->jobs_for_order( (int) $order->get_id() );
		$this->assertCount( 2, $jobs, 'Un trabajo por salida activa.' );

		$roles = array( $jobs[0]->printer_role, $jobs[1]->printer_role );
		$this->assertContains( 'default', $roles );
		$this->assertContains( 'cocina', $roles );

		$copies = array();
		foreach ( $jobs as $job ) {
			$copies[ $job->printer_role ] = (int) $job->copies;
		}
		$this->assertSame( 1, $copies['default'] );
		$this->assertSame( 3, $copies['cocina'], 'Las copias de la salida quedan congeladas en el trabajo.' );
	}

	/** Una salida desactivada no entra en el fan-out (solo enabled = 1). */
	public function test_disabled_destination_never_gets_new_jobs(): void {
		$this->woocommerce_or_skip();
		$this->seed_destination( 'default', 'EPSON T20', 1 );
		$this->seed_destination( 'cocina', 'XPRINTER 80C', 2, false );

		$order = $this->make_order();

		$jobs = $this->jobs_for_order( (int) $order->get_id() );
		$this->assertCount( 1, $jobs, 'La salida desactivada no genera trabajo.' );
		$this->assertSame( 'default', $jobs[0]->printer_role );
	}

	/** Desactivar una salida con trabajos pendientes: pendientes → cancelled. */
	public function test_disabling_destination_cancels_its_pending_jobs(): void {
		$this->woocommerce_or_skip();
		$this->seed_destination( 'default', 'EPSON T20', 1 );
		$this->seed_destination( 'cocina', 'XPRINTER 80C', 2 );

		$order = $this->make_order();
		$this->assertCount( 2, $this->jobs_for_order( (int) $order->get_id() ) );

		// El agente reporta en su heartbeat la salida desactivada (upsert).
		DestinationRepository::upsert(
			$this->location_id,
			array(
				'role'         => 'cocina',
				'label'        => 'Cocina',
				'printer_name' => 'XPRINTER 80C',
				'copies'       => 2,
				'enabled'      => false,
			)
		);

		$jobs    = $this->jobs_for_order( (int) $order->get_id() );
		$by_role = array();
		foreach ( $jobs as $job ) {
			$by_role[ $job->printer_role ] = $job->status;
		}

		$this->assertSame( 'pending', $by_role['default'], 'La otra salida no se toca.' );
		$this->assertSame( 'cancelled', $by_role['cocina'], 'Los pendientes de la salida desactivada se cancelan.' );
	}

	/** Cambiar copias de la salida: los pendientes conservan la copia congelada. */
	public function test_changing_destination_copies_does_not_touch_pending_jobs(): void {
		$this->woocommerce_or_skip();
		$this->seed_destination( 'default', 'EPSON T20', 1 );

		$order = $this->make_order();

		$jobs = $this->jobs_for_order( (int) $order->get_id() );
		$this->assertSame( 1, (int) $jobs[0]->copies );

		// El agente reporta copies = 5 en el heartbeat.
		$this->seed_destination( 'default', 'EPSON T20', 5 );

		// El trabajo pendiente conserva sus copias originales (Doc/04 §2.8).
		$jobs = $this->jobs_for_order( (int) $order->get_id() );
		$this->assertCount( 1, $jobs );
		$this->assertSame( 1, (int) $jobs[0]->copies, 'Los pendientes conservan las copias con las que se crearon.' );

		// Un pedido NUEVO usa el valor nuevo.
		$order_b = $this->make_order();
		$jobs_b  = $this->jobs_for_order( (int) $order_b->get_id() );
		$this->assertSame( 5, (int) $jobs_b[0]->copies, 'Los trabajos nuevos usan las copias vigentes de la salida.' );
	}

	/*
	 ---------------------------------------------------------------------
	 * Nunca en silencio + cambio de reglas.
	 * ------------------------------------------------------------------- */

	/** Pedido sin local asignable: log en orp_logs y ningún trabajo. */
	public function test_unassignable_order_logs_and_creates_nothing(): void {
		$this->woocommerce_or_skip();
		$this->seed_destination( 'default', 'EPSON T20', 1 );

		// Pedido SIN la meta de enrutado (y sin local por defecto en set_up).
		$order = new \WC_Order();
		$order->set_shipping_city( 'Villanueva del Trabajo' );
		$order->save();

		$this->assertCount( 0, $this->jobs_for_order( (int) $order->get_id() ) );

		// El log quedó registrado (nunca en silencio).
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$found = (int) $wpdb->get_var(
			$wpdb->prepare(
				"SELECT COUNT(*) FROM {$wpdb->prefix}orp_logs
				  WHERE context = 'locations' AND level = 'warning'
				    AND message LIKE %s",
				'%' . $wpdb->esc_like( 'sin local por defecto' ) . '%'
			)
		);
		$this->assertGreaterThanOrEqual( 1, $found, 'La caída sin local por defecto queda registrada en orp_logs.' );
	}

	/** Local sin salidas activas: no se crean trabajos (la cola no crece sin agente). */
	public function test_location_without_destinations_creates_nothing(): void {
		$this->woocommerce_or_skip();

		// Sin salidas en el local de pruebas.
		$order = $this->make_order();

		$this->assertCount( 0, $this->jobs_for_order( (int) $order->get_id() ) );
	}

	/** Cambiar la regla del local después no altera los trabajos ya encolados. */
	public function test_changing_rule_does_not_touch_enqueued_jobs(): void {
		$this->woocommerce_or_skip();
		$this->seed_destination( 'default', 'EPSON T20', 1 );

		$order = $this->make_order();
		$jobs  = $this->jobs_for_order( (int) $order->get_id() );
		$this->assertCount( 1, $jobs );

		// Cambia la regla y el nombre del local en BD.
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->update(
			$wpdb->prefix . 'orp_locations',
			array(
				'rules'      => wp_json_encode(
					array(
						array(
							'type'     => 'city',
							'operator' => 'in',
							'values'   => array( 'Otra Ciudad' ),
						),
					)
				),
				'name'       => 'Local renombrado',
				'updated_at' => Clock::now_sql(),
			),
			array( 'id' => $this->location_id ),
			array( '%s', '%s', '%s' )
		);

		$jobs_after = $this->jobs_for_order( (int) $order->get_id() );
		$this->assertCount( 1, $jobs_after );
		$this->assertSame( $jobs[0]->id, $jobs_after[0]->id, 'El trabajo encolado no cambia al editar la regla.' );
		$this->assertSame( $this->location_id, (int) $jobs_after[0]->location_id, 'El local queda congelado en la fila del trabajo.' );
	}

	/*
	 ---------------------------------------------------------------------
	 * Registro del disparo.
	 * ------------------------------------------------------------------- */

	/** OrderTrigger::register() engancha los cuatro caminos. */
	public function test_hooks_are_registered(): void {
		OrderTrigger::register();

		$this->assertNotFalse( has_action( 'woocommerce_new_order', array( OrderTrigger::class, 'on_new_order' ) ) );
		$this->assertNotFalse( has_action( 'woocommerce_checkout_order_processed', array( OrderTrigger::class, 'on_classic_checkout' ) ) );
		$this->assertNotFalse( has_action( 'woocommerce_store_api_checkout_order_processed', array( OrderTrigger::class, 'on_store_api_checkout' ) ) );
		$this->assertNotFalse( has_action( 'woocommerce_order_status_changed', array( OrderTrigger::class, 'on_status_changed' ) ) );
	}
}
