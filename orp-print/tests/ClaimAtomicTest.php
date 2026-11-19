<?php
/**
 * Prueba del reclamo atómico (F0-05): solo el primero gana, el segundo recibe
 * cero filas (el controlador responde 409/410), un ack repetido no altera el
 * estado y release devuelve a pending sin consumir intento.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Tests;

use Orvita\PrintBridge\Rest\JobsController;
use Orvita\PrintBridge\Support\Clock;

final class ClaimAtomicTest extends \WP_UnitTestCase {

	/** @var int Local de pruebas. */
	private int $location_id;

	/** @var int Agente de pruebas. */
	private int $agent_id;

	public function set_up(): void {
		parent::set_up();

		global $wpdb;

		$now = Clock::now_sql();

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->insert(
			$wpdb->prefix . 'orp_locations',
			array(
				'code'       => 'test-loc',
				'name'       => 'Local de pruebas',
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

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->insert(
			$wpdb->prefix . 'orp_agents',
			array(
				'location_id'  => $this->location_id,
				'name'         => 'Agente de pruebas',
				'token_hash'   => str_repeat( 'a', 64 ),
				'token_prefix' => 'orp_test',
				'status'       => 'active',
				'created_at'   => $now,
			),
			array( '%d', '%s', '%s', '%s', '%s', '%s' )
		);
		$this->agent_id = (int) $wpdb->insert_id;
	}

	/** Inserta un trabajo pendiente y devuelve su id. */
	private function create_job( string $expires_offset = '+30 minutes' ): int {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$affected = $wpdb->insert(
			$wpdb->prefix . 'orp_jobs',
			array(
				'location_id'     => $this->location_id,
				'order_id'        => 0,
				'printer_role'    => 'default',
				'copies'          => 1,
				'status'          => 'pending',
				'payload'         => '{"version":1,"blocks":[]}',
				'idempotency_key' => 'test-key-' . uniqid( '', true ),
				'attempts'        => 0,
				'max_attempts'    => 5,
				'created_at'      => Clock::now_sql(),
				'expires_at'      => gmdate( 'Y-m-d H:i:s', (int) strtotime( $expires_offset ) ),
			),
			array( '%d', '%d', '%s', '%d', '%s', '%s', '%s', '%d', '%d', '%s', '%s' )
		);

		$this->assertNotFalse( $affected, 'El trabajo de prueba debe insertarse.' );

		return (int) $wpdb->insert_id;
	}

	/** Lee el estado actual de un trabajo. */
	private function fetch_job( int $job_id ): object {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$row = $wpdb->get_row(
			$wpdb->prepare(
				'SELECT status, attempts, claimed_by, claimed_at, delivered_at, last_error FROM ' . $wpdb->prefix . 'orp_jobs WHERE id = %d',
				$job_id
			)
		);

		$this->assertNotNull( $row );

		return $row;
	}

	/** El primer claim gana: 1 fila, claimed + intento consumido. */
	public function test_first_claim_succeeds(): void {
		$job_id   = $this->create_job();
		$affected = JobsController::claim_atomically( $job_id, $this->agent_id );

		$this->assertSame( 1, $affected );

		$job = $this->fetch_job( $job_id );
		$this->assertSame( 'claimed', $job->status );
		$this->assertSame( 1, (int) $job->attempts );
		$this->assertSame( $this->agent_id, (int) $job->claimed_by );
		$this->assertNotNull( $job->claimed_at );
	}

	/** El segundo claim obtiene 0 filas: el controlador responderá 409. */
	public function test_second_claim_gets_zero_rows(): void {
		$job_id = $this->create_job();
		$this->assertSame( 1, JobsController::claim_atomically( $job_id, $this->agent_id ) );

		// Un segundo agente (u otro intento) no puede reclamarlo de nuevo.
		$this->assertSame( 0, JobsController::claim_atomically( $job_id, $this->agent_id ) );

		$job = $this->fetch_job( $job_id );
		$this->assertSame( 'claimed', $job->status );
		$this->assertSame( 1, (int) $job->attempts, 'El intento no debe consumirse dos veces.' );
	}

	/** Un trabajo caducado no se reclama: el controlador mapeará a 410. */
	public function test_expired_job_cannot_be_claimed(): void {
		$job_id = $this->create_job( '-1 minute' );

		$this->assertSame( 0, JobsController::claim_atomically( $job_id, $this->agent_id ) );

		$job = $this->fetch_job( $job_id );
		$this->assertSame( 'pending', $job->status );
		$this->assertSame( 0, (int) $job->attempts );
	}

	/** Un trabajo agotado (attempts >= max_attempts) no se reclama. */
	public function test_exhausted_job_cannot_be_claimed(): void {
		global $wpdb;

		$job_id = $this->create_job();

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->query(
			$wpdb->prepare(
				'UPDATE ' . $wpdb->prefix . 'orp_jobs SET attempts = max_attempts WHERE id = %d',
				$job_id
			)
		);

		$this->assertSame( 0, JobsController::claim_atomically( $job_id, $this->agent_id ) );
	}

	/** Un ack repetido no altera el estado final (idempotencia). */
	public function test_ack_is_idempotent(): void {
		$job_id = $this->create_job();
		$this->assertSame( 1, JobsController::claim_atomically( $job_id, $this->agent_id ) );

		$this->assertTrue( JobsController::ack( $job_id, 'delivered', null ) );
		$this->assertSame( 'delivered', $this->fetch_job( $job_id )->status );

		// Segundo ack (aunque sea failed): la cláusula status='claimed' lo descarta.
		$this->assertFalse( JobsController::ack( $job_id, 'failed', 'duplicado' ) );
		$this->assertSame( 'delivered', $this->fetch_job( $job_id )->status );
	}

	/** Release devuelve a pending sin consumir intento. */
	public function test_release_keeps_attempts(): void {
		$job_id = $this->create_job();
		$this->assertSame( 1, JobsController::claim_atomically( $job_id, $this->agent_id ) );

		$this->assertTrue( JobsController::release_job( $job_id, $this->agent_id, 'fallo transitorio de prueba' ) );

		$job = $this->fetch_job( $job_id );
		$this->assertSame( 'pending', $job->status );
		$this->assertSame( 1, (int) $job->attempts, 'Release no debe consumir intento.' );
		$this->assertNull( $job->claimed_by );
		$this->assertNull( $job->claimed_at );
		$this->assertStringContainsString( 'fallo transitorio', (string) $job->last_error );
	}

	/** El poll solo devuelve trabajos pendientes del local, en orden de id. */
	public function test_poll_returns_only_pending_of_location(): void {
		$first  = $this->create_job();
		$second = $this->create_job();
		$this->assertSame( 1, JobsController::claim_atomically( $first, $this->agent_id ) );

		$rows = JobsController::pending_for_location( $this->location_id, 0, 10 );
		$this->assertCount( 1, $rows, 'Solo queda un trabajo pendiente (el otro está claimed).' );
		$this->assertSame( $second, (int) $rows[0]->id );

		// Cursor: con after = segundo id no queda nada (respuesta vacía, sin error).
		$this->assertCount( 0, JobsController::pending_for_location( $this->location_id, $second, 10 ) );
	}
}
