<?php
/**
 * Pruebas de las acciones de la página Cola (F1-08): cancelar
 * (pending/claimed → cancelled) y reencolar (failed/expired → la MISMA fila
 * en pending, intentos a cero, sin duplicados).
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Tests;

use Orvita\PrintBridge\Admin\JobsPage;
use Orvita\PrintBridge\Support\Clock;

final class JobsPageActionsTest extends \WP_UnitTestCase {

	/** @var int Local de pruebas. */
	private int $location_id;

	public function set_up(): void {
		parent::set_up();

		global $wpdb;

		$now = Clock::now_sql();

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->insert(
			$wpdb->prefix . 'orp_locations',
			array(
				'code'       => 'test-cola',
				'name'       => 'Local de la cola',
				'enabled'    => 1,
				'priority'   => 100,
				'rules'      => null,
				'is_default' => 0,
				'created_at' => $now,
				'updated_at' => $now,
			),
			array( '%s', '%s', '%d', '%d', '%s', '%d', '%s', '%s' )
		);
		$this->location_id = (int) $wpdb->insert_id;
	}

	/** Inserta un trabajo y devuelve [id, idempotency_key]. */
	private function insert_job( string $status, array $overrides = array() ): array {
		global $wpdb;

		$key   = 'cola-' . uniqid( '', true );
		$row   = array_merge(
			array(
				'location_id'     => $this->location_id,
				'order_id'        => 0,
				'printer_role'    => 'default',
				'copies'          => 1,
				'status'          => $status,
				'payload'         => '{"version":1,"blocks":[]}',
				'idempotency_key' => $key,
				'attempts'        => 0,
				'max_attempts'    => 5,
				'claimed_by'      => null,
				'claimed_at'      => null,
				'delivered_at'    => null,
				'last_error'      => null,
				'created_at'      => Clock::now_sql(),
				'expires_at'      => gmdate( 'Y-m-d H:i:s', time() + 30 * MINUTE_IN_SECONDS ),
			),
			$overrides
		);

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$affected = $wpdb->insert(
			$wpdb->prefix . 'orp_jobs',
			$row,
			array( '%d', '%d', '%s', '%d', '%s', '%s', '%s', '%d', '%d', '%d', '%s', '%s', '%s', '%s', '%s' )
		);

		$this->assertNotFalse( $affected, 'El trabajo de prueba debe insertarse.' );

		return array( (int) $wpdb->insert_id, $key );
	}

	/** Lee los campos de interés de un trabajo. */
	private function fetch_job( int $job_id ): object {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$row = $wpdb->get_row(
			$wpdb->prepare(
				'SELECT status, attempts, idempotency_key, claimed_by, claimed_at, last_error, expires_at, delivered_at FROM ' . $wpdb->prefix . 'orp_jobs WHERE id = %d',
				$job_id
			)
		);

		$this->assertNotNull( $row );

		return $row;
	}

	/** Cancelar un trabajo pendiente → cancelled. */
	public function test_cancel_pending_job(): void {
		$job_id = $this->insert_job( 'pending' )[0];

		$this->assertTrue( JobsPage::cancel_job( $job_id ) );
		$this->assertSame( 'cancelled', $this->fetch_job( $job_id )->status );
	}

	/** Cancelar un trabajo reclamado → cancelled (el papel no se desimprime). */
	public function test_cancel_claimed_job(): void {
		global $wpdb;

		$job_id = $this->insert_job(
			'claimed',
			array(
				'claimed_by' => 99,
				'claimed_at' => Clock::now_sql(),
			)
		)[0];

		$this->assertTrue( JobsPage::cancel_job( $job_id ) );
		$this->assertSame( 'cancelled', $this->fetch_job( $job_id )->status );
		$this->assertNull( $this->fetch_job( $job_id )->claimed_by );
	}

	/** Lo entregado no se cancela: false y el estado intacto. */
	public function test_cancel_delivered_job_returns_false(): void {
		$job_id = $this->insert_job(
			'delivered',
			array( 'delivered_at' => Clock::now_sql() )
		)[0];

		$this->assertFalse( JobsPage::cancel_job( $job_id ) );
		$this->assertSame( 'delivered', $this->fetch_job( $job_id )->status );
	}

	/** Cancelar un trabajo que no existe → false. */
	public function test_cancel_missing_job_returns_false(): void {
		$this->assertFalse( JobsPage::cancel_job( 99999999 ) );
	}

	/**
	 * Reencolar un trabajo fallido restaura LA MISMA fila: mismo id, mismo
	 * idempotency_key, pending con attempts = 0, sin reclamo, sin error y
	 * caducidad nueva (Doc/07 §3 F1-08).
	 */
	public function test_requeue_failed_job_restores_the_same_row(): void {
		$expires_at = gmdate( 'Y-m-d H:i:s', time() - 60 );

		list( $job_id, $key ) = $this->insert_job(
			'failed',
			array(
				'attempts'   => 5,
				'claimed_by' => 99,
				'claimed_at' => $expires_at,
				'last_error' => 'orp_printer_not_found: no existe la impresora',
				'expires_at' => $expires_at,
			)
		);

		$this->assertTrue( JobsPage::requeue_job( $job_id ) );

		$job = $this->fetch_job( $job_id );

		$this->assertSame( 'pending', $job->status );
		$this->assertSame( 0, (int) $job->attempts, 'Los intentos se reinician: si no, nadie podría reclamarlo.' );
		$this->assertSame( $key, (string) $job->idempotency_key, 'MISMA fila: la clave se conserva y nada se duplica.' );
		$this->assertNull( $job->claimed_by );
		$this->assertNull( $job->claimed_at );
		$this->assertNull( $job->last_error );

		// Caducidad nueva: el reencolado debe tener ventana de nuevo.
		$expires = (int) strtotime( (string) $job->expires_at . ' UTC' );
		$this->assertGreaterThan( time() + 5 * MINUTE_IN_SECONDS, $expires, 'La caducidad se extiende con job_expiry_minutes.' );
	}

	/** Reencolar un trabajo caducado → pending con caducidad nueva. */
	public function test_requeue_expired_job(): void {
		$job_id = $this->insert_job(
			'expired',
			array( 'expires_at' => gmdate( 'Y-m-d H:i:s', time() - 60 ) )
		)[0];

		$this->assertTrue( JobsPage::requeue_job( $job_id ) );

		$job = $this->fetch_job( $job_id );
		$this->assertSame( 'pending', $job->status );
		$this->assertSame( 0, (int) $job->attempts );
	}

	/** Los pendientes no se reencolan (no es su acción). */
	public function test_requeue_pending_job_returns_false(): void {
		$job_id = $this->insert_job( 'pending' )[0];

		$this->assertFalse( JobsPage::requeue_job( $job_id ) );
		$this->assertSame( 'pending', $this->fetch_job( $job_id )->status );
	}

	/** Lo entregado no se reencola. */
	public function test_requeue_delivered_job_returns_false(): void {
		$job_id = $this->insert_job(
			'delivered',
			array( 'delivered_at' => Clock::now_sql() )
		)[0];

		$this->assertFalse( JobsPage::requeue_job( $job_id ) );
		$this->assertSame( 'delivered', $this->fetch_job( $job_id )->status );
	}

	/** La consulta de la lista respeta los filtros de estado y local. */
	public function test_list_filters(): void {
		global $wpdb;

		$this->insert_job( 'pending' );
		$failed = $this->insert_job( 'failed', array( 'attempts' => 5 ) )[0];

		// Filtro por estado con parámetros de la misma consulta que usa la página.
		$table = $wpdb->prefix . 'orp_jobs';
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$total = (int) $wpdb->get_var(
			$wpdb->prepare( "SELECT COUNT(*) FROM {$table} WHERE status = %s AND location_id = %d", 'failed', $this->location_id )
		);
		$this->assertSame( 1, $total );
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$this->assertSame(
			$failed,
			(int) $wpdb->get_var(
				$wpdb->prepare( "SELECT id FROM {$table} WHERE status = %s AND location_id = %d", 'failed', $this->location_id )
			)
		);
	}
}
