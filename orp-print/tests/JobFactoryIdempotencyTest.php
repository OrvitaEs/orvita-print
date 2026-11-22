<?php
/**
 * Prueba de idempotencia de JobFactory (B3): dos create() con la misma clave
 * de idempotencia producen una sola fila y el segundo create() devuelve null.
 * Mismo patrón que ClaimAtomicTest (esquema real con WP_UnitTestCase).
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Tests;

use Orvita\PrintBridge\Domain\JobFactory;
use Orvita\PrintBridge\Support\Clock;

final class JobFactoryIdempotencyTest extends \WP_UnitTestCase {

	/** @var int Local de pruebas. */
	private int $location_id;

	public function set_up(): void {
		parent::set_up();

		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->insert(
			$wpdb->prefix . 'orp_locations',
			array(
				'code'       => 'test-factory',
				'name'       => 'Local de pruebas del JobFactory',
				'enabled'    => 1,
				'priority'   => 100,
				'rules'      => null,
				'is_default' => 0,
				'created_at' => Clock::now_sql(),
				'updated_at' => Clock::now_sql(),
			),
			array( '%s', '%s', '%d', '%d', '%s', '%d', '%s', '%s' )
		);
		$this->location_id = (int) $wpdb->insert_id;
	}

	/** Cuenta los trabajos de un pedido en el local de pruebas. */
	private function count_jobs_for_order( int $order_id ): int {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		return (int) $wpdb->get_var(
			$wpdb->prepare(
				'SELECT COUNT(*) FROM ' . $wpdb->prefix . 'orp_jobs WHERE location_id = %d AND order_id = %d',
				$this->location_id,
				$order_id
			)
		);
	}

	/** Dos create() con la misma idempotency_key: la segunda devuelve null y hay 1 fila. */
	public function test_duplicate_create_returns_null_and_single_row(): void {
		$document = array(
			'version' => 1,
			'blocks'  => array(),
		);

		$first = JobFactory::create( $this->location_id, 991, 'default', 1, $document );
		$this->assertNotNull( $first, 'El primer create() debe insertar el trabajo y devolver su id.' );

		$second = JobFactory::create( $this->location_id, 991, 'default', 1, $document );
		$this->assertNull( $second, 'El segundo create() con la misma clave debe devolver null (INSERT IGNORE).' );

		$this->assertSame( 1, $this->count_jobs_for_order( 991 ), 'Solo debe existir una fila para la clave.' );
	}

	/** Pedidos distintos generan trabajos distintos: la deduplicación no los colisiona. */
	public function test_different_orders_create_distinct_jobs(): void {
		$document = array( 'version' => 1, 'blocks' => array() );

		$job_a = JobFactory::create( $this->location_id, 992, 'default', 1, $document );
		$job_b = JobFactory::create( $this->location_id, 993, 'default', 1, $document );

		$this->assertNotNull( $job_a );
		$this->assertNotNull( $job_b );
		$this->assertNotSame( $job_a, $job_b );
		$this->assertSame( 2, $this->count_jobs_for_order( 992 ) + $this->count_jobs_for_order( 993 ) );
	}
}
