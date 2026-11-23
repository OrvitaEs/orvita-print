<?php
/**
 * Pruebas del Reaper (F1-09): arrendamiento de 120 s (sin tocar attempts),
 * intentos agotados → failed, caducidad → expired con aviso acumulable y
 * retención 90/30 días.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Tests;

use Orvita\PrintBridge\Domain\Reaper;
use Orvita\PrintBridge\Support\Clock;

final class ReaperTest extends \WP_UnitTestCase {

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
				'code'       => 'test-reaper',
				'name'       => 'Local del reaper',
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
				'name'         => 'Agente del reaper',
				'token_hash'   => str_repeat( 'b', 64 ),
				'token_prefix' => 'orp_reap',
				'status'       => 'active',
				'created_at'   => $now,
			),
			array( '%d', '%s', '%s', '%s', '%s', '%s' )
		);
		$this->agent_id = (int) $wpdb->insert_id;

		// Aviso de caducados a cero antes de cada prueba.
		update_option( Reaper::EXPIRED_NOTICE_OPTION, 0, false );
	}

	public function tear_down(): void {
		update_option( Reaper::EXPIRED_NOTICE_OPTION, 0, false );

		parent::tear_down();
	}

	/**
	 * Inserta un trabajo con control fino de los campos de tiempo.
	 *
	 * @param string $status     Estado inicial.
	 * @param array  $overrides  Campos: attempts, max_attempts, claimed_at, expires_at, created_at.
	 */
	private function insert_job( string $status, array $overrides = array() ): int {
		global $wpdb;

		$row = array_merge(
			array(
				'location_id'     => $this->location_id,
				'order_id'        => 0,
				'printer_role'    => 'default',
				'copies'          => 1,
				'status'          => $status,
				'payload'         => '{"version":1,"blocks":[]}',
				'idempotency_key' => 'reaper-' . uniqid( '', true ),
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

		return (int) $wpdb->insert_id;
	}

	/** Lee los campos de interés de un trabajo. */
	private function fetch_job( int $job_id ): object {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$row = $wpdb->get_row(
			$wpdb->prepare(
				'SELECT status, attempts, max_attempts, claimed_by, claimed_at, last_error, created_at FROM ' . $wpdb->prefix . 'orp_jobs WHERE id = %d',
				$job_id
			)
		);

		$this->assertNotNull( $row );

		return $row;
	}

	/** Lease vencido (>120 s) → pending SIN tocar attempts (el claim ya los consumió). */
	public function test_expired_lease_returns_to_pending_without_touching_attempts(): void {
		$claimed_at = gmdate( 'Y-m-d H:i:s', time() - 180 );
		$job_id     = $this->insert_job(
			'claimed',
			array(
				'attempts'   => 2,
				'claimed_by' => $this->agent_id,
				'claimed_at' => $claimed_at,
			)
		);

		$summary = Reaper::run();

		$this->assertGreaterThanOrEqual( 1, $summary['requeued'], 'El lease vencido vuelve a la cola.' );

		$job = $this->fetch_job( $job_id );
		$this->assertSame( 'pending', $job->status );
		$this->assertSame( 2, (int) $job->attempts, 'Los intentos NO se tocan al devolver el lease.' );
		$this->assertNull( $job->claimed_by );
		$this->assertNull( $job->claimed_at );
	}

	/** Un trabajo reclamado hace segundos por un agente activo NO se roba. */
	public function test_recent_lease_is_not_stolen(): void {
		$claimed_at = gmdate( 'Y-m-d H:i:s', time() - 10 );
		$job_id     = $this->insert_job(
			'claimed',
			array(
				'attempts'   => 1,
				'claimed_by' => $this->agent_id,
				'claimed_at' => $claimed_at,
			)
		);

		Reaper::run();

		$job = $this->fetch_job( $job_id );
		$this->assertSame( 'claimed', $job->status, 'El lease de 120 s sigue vivo: no se toca.' );
		$this->assertSame( $this->agent_id, (int) $job->claimed_by );
	}

	/** Lease vencido con intentos agotados → failed con el último error. */
	public function test_expired_lease_with_exhausted_attempts_becomes_failed(): void {
		$claimed_at = gmdate( 'Y-m-d H:i:s', time() - 300 );
		$job_id     = $this->insert_job(
			'claimed',
			array(
				'attempts'    => 5,
				'claimed_by'  => $this->agent_id,
				'claimed_at'  => $claimed_at,
				'last_error'  => 'error anterior',
			)
		);

		Reaper::run();

		$job = $this->fetch_job( $job_id );
		$this->assertSame( 'failed', $job->status );
		$this->assertSame( 5, (int) $job->attempts );
		$this->assertStringContainsString( 'agotados', (string) $job->last_error );
	}

	/** Pending con la caducidad pasada → expired y aviso acumulable en el panel. */
	public function test_pending_expired_becomes_expired_with_notice(): void {
		$expires_at = gmdate( 'Y-m-d H:i:s', time() - 60 );
		$job_id     = $this->insert_job( 'pending', array( 'expires_at' => $expires_at ) );

		$before = (int) get_option( Reaper::EXPIRED_NOTICE_OPTION, 0 );
		Reaper::run();
		$after = (int) get_option( Reaper::EXPIRED_NOTICE_OPTION, 0 );

		$job = $this->fetch_job( $job_id );
		$this->assertSame( 'expired', $job->status );
		$this->assertSame( 1, $after - $before, 'El aviso acumula los caducados.' );
	}

	/** Claimed con la caducidad pasada → expired (nunca se imprime, Doc/04 §4). */
	public function test_claimed_expired_becomes_expired(): void {
		$expires_at = gmdate( 'Y-m-d H:i:s', time() - 10 );
		$claimed_at = gmdate( 'Y-m-d H:i:s', time() - 10 );
		$job_id     = $this->insert_job(
			'claimed',
			array(
				'expires_at' => $expires_at,
				'claimed_at' => $claimed_at,
				'claimed_by' => $this->agent_id,
			)
		);

		Reaper::run();

		$job = $this->fetch_job( $job_id );
		$this->assertSame( 'expired', $job->status );
		$this->assertNull( $job->claimed_by );
	}

	/** Pending agotado (caso del release tras agotar) → failed. */
	public function test_pending_exhausted_becomes_failed(): void {
		$job_id = $this->insert_job( 'pending', array( 'attempts' => 5 ) );

		Reaper::run();

		$job = $this->fetch_job( $job_id );
		$this->assertSame( 'failed', $job->status );
		$this->assertStringContainsString( 'agotados', (string) $job->last_error );
	}

	/** Un trabajo delivered no lo toca el reaper. */
	public function test_delivered_jobs_are_untouched(): void {
		$job_id = $this->insert_job(
			'delivered',
			array( 'delivered_at' => Clock::now_sql() )
		);

		Reaper::run();

		$this->assertSame( 'delivered', $this->fetch_job( $job_id )->status );
	}

	/**
	 * retention_days = 0 significa «conservar siempre»: el Reaper no borra
	 * NINGÚN trabajo, aunque tenga 100 días (F1-13). El panel acota a ≥1;
	 * un 0 solo llega editando la opción directamente, y se respeta.
	 */
	public function test_retention_days_zero_deletes_nothing(): void {
		global $wpdb;

		$original = get_option( \Orvita\PrintBridge\Support\Settings::OPTION, array() );

		$old_created = gmdate( 'Y-m-d H:i:s', time() - 100 * DAY_IN_SECONDS );
		$old_job     = $this->insert_job( 'delivered', array( 'created_at' => $old_created ) );

		\Orvita\PrintBridge\Support\Settings::save( array( 'retention_days' => 0 ) );

		$summary = Reaper::run();

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$this->assertNotSame(
			0,
			(int) $wpdb->get_var( $wpdb->prepare( 'SELECT COUNT(*) FROM ' . $wpdb->prefix . 'orp_jobs WHERE id = %d', $old_job ) ),
			'Con retention_days = 0 el trabajo viejo NO se borra.'
		);
		$this->assertSame( 0, $summary['jobs_deleted'] );

		// Restaurar ajustes.
		update_option( \Orvita\PrintBridge\Support\Settings::OPTION, $original );
	}

	/** Retención: jobs de más de 90 días se borran; los recientes se quedan. */
	public function test_job_retention_deletes_old_rows(): void {
		$old_created  = gmdate( 'Y-m-d H:i:s', time() - 100 * DAY_IN_SECONDS );
		$recent_job   = $this->insert_job( 'delivered', array() );
		$old_job      = $this->insert_job( 'delivered', array( 'created_at' => $old_created ) );
		$old_pending  = $this->insert_job( 'expired', array( 'created_at' => $old_created ) );

		Reaper::run();

		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$this->assertSame( 0, (int) $wpdb->get_var( $wpdb->prepare( 'SELECT COUNT(*) FROM ' . $wpdb->prefix . 'orp_jobs WHERE id = %d', $old_job ) ) );
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$this->assertSame( 0, (int) $wpdb->get_var( $wpdb->prepare( 'SELECT COUNT(*) FROM ' . $wpdb->prefix . 'orp_jobs WHERE id = %d', $old_pending ) ) );
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$this->assertNotSame( 0, (int) $wpdb->get_var( $wpdb->prepare( 'SELECT COUNT(*) FROM ' . $wpdb->prefix . 'orp_jobs WHERE id = %d', $recent_job ) ), 'El trabajo reciente sobrevive.' );
	}

	/** Retención de logs: 30 días (Doc/04 §2.6); lo reciente se queda. */
	public function test_log_retention_deletes_rows_older_than_30_days(): void {
		global $wpdb;

		$old_created = gmdate( 'Y-m-d H:i:s', time() - 40 * DAY_IN_SECONDS );

		\Orvita\PrintBridge\Support\Logger::log( 'info', 'tests', 'Log reciente de la prueba.' );

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->query(
			$wpdb->prepare(
				"INSERT INTO {$wpdb->prefix}orp_logs (created_at, level, context, message) VALUES (%s, 'info', 'tests', 'Log viejo de la prueba')",
				$old_created
			)
		);

		$old_id = (int) $wpdb->insert_id;

		Reaper::run();

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$this->assertSame( 0, (int) $wpdb->get_var( $wpdb->prepare( 'SELECT COUNT(*) FROM ' . $wpdb->prefix . 'orp_logs WHERE id = %d', $old_id ) ), 'El log de más de 30 días se borra.' );
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$this->assertNotSame(
			0,
			(int) $wpdb->get_var(
				$wpdb->prepare( "SELECT COUNT(*) FROM {$wpdb->prefix}orp_logs WHERE message = %s", 'Log reciente de la prueba.' )
			),
			'El log reciente sobrevive.'
		);
	}

	/** El lazy run se limita con el transient (no corre dos veces seguidas). */
	public function test_lazy_run_is_throttled(): void {
		delete_transient( 'orp_reaper_lastrun' );

		Reaper::lazy_run();

		$this->assertNotFalse( get_transient( 'orp_reaper_lastrun' ), 'El lazy run apunta su hora.' );

		// Segundo inmediato: el transient lo frena (sin forma de observar el
		// run aquí, basta con que no renueve la marca antes de tiempo).
		$first = get_transient( 'orp_reaper_lastrun' );
		Reaper::lazy_run();
		$this->assertSame( (int) $first, (int) get_transient( 'orp_reaper_lastrun' ) );
	}

	/** El cron horario queda programado al registrar (entornos con WP-Cron). */
	public function test_cron_is_scheduled(): void {
		delete_option( 'cron' );
		Reaper::ensure_schedule();

		$this->assertNotFalse( wp_next_scheduled( Reaper::CRON_HOOK ), 'El cron horario del reaper debe estar programado.' );
	}

	/** Agentes sin latido > 3 min (o nunca) para los avisos del panel. */
	public function test_disconnected_agents_detection(): void {
		// Recién visto: conectado.
		global $wpdb;
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->update(
			$wpdb->prefix . 'orp_agents',
			array( 'last_seen_at' => Clock::now_sql() ),
			array( 'id' => $this->agent_id ),
			array( '%s' ),
			array( '%d' )
		);
		$this->assertCount( 0, array_filter( Reaper::disconnected_agents(), fn( $agent ) => (int) $agent->id === $this->agent_id ) );

		// Hace 5 minutos: desconectado.
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->update(
			$wpdb->prefix . 'orp_agents',
			array( 'last_seen_at' => gmdate( 'Y-m-d H:i:s', time() - 300 ) ),
			array( 'id' => $this->agent_id ),
			array( '%s' ),
			array( '%d' )
		);
		$found = false;
		foreach ( Reaper::disconnected_agents() as $agent ) {
			if ( (int) $agent->id === $this->agent_id ) {
				$found = true;
			}
		}
		$this->assertTrue( $found, 'El agente con latido de hace 5 minutos aparece como desconectado.' );
	}
}
