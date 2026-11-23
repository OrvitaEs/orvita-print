<?php
/**
 * Reaper (F1-09) — limpieza de la cola sin trabajos zombis (Doc/04 §4):
 *
 * - Arrendamiento: un trabajo `claimed` sin ack durante LEASE_SECONDS vuelve
 *   a `pending` SIN tocar attempts (el claim ya consumió el intento); si con
 *   el lease vencido ya tiene attempts >= max_attempts, pasa a `failed`.
 *   Un trabajo reclamado hace segundos por un agente ACTIVO no se roba: el
   * límite de 120 s lo cubre (el agente realista hace claim→imprimir→ack en
 *   segundos; F2-07) — solo se toca lo que lleva más de 120 s sin responder.
 * - Caducidad: `pending`/`claimed` con expires_at pasado → `expired` y aviso
 *   acumulable en el panel (un trabajo caducado nunca se imprime).
 * - Retención: orp_jobs > retention_days (90 por defecto) → delete;
 *   orp_logs > 30 días (Doc/04 §2.6) → delete. Nada de datos personales.
 *
 * Ejecución: cron horario de WordPress + ejecución perezosa (lazy) en
 * `rest_api_init` como respaldo para entornos sin WP-Cron real (Doc/04 §4:
 * «en la siguiente petición o en el cron de limpieza»); el lazy corre como
 * mucho cada LAZY_INTERVAL_SECONDS. Los UPDATE/DELETE son atómicos e
 * idempotentes: dos ejecuciones solapadas no corrompen nada.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Domain;

use Orvita\PrintBridge\Support\Clock;
use Orvita\PrintBridge\Support\Logger;
use Orvita\PrintBridge\Support\Settings;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class Reaper {

	/** Hook del cron horario. */
	public const CRON_HOOK = 'orp_reaper_hourly';

	/** Arrendamiento de un claim sin ack, en segundos (Doc/04 §4). */
	public const LEASE_SECONDS = 120;

	/** Retención de orp_logs en días (Doc/04 §2.6, congelado). */
	public const LOG_RETENTION_DAYS = 30;

	/** Separación mínima entre ejecuciones perezosas, en segundos. */
	public const LAZY_INTERVAL_SECONDS = 5 * MINUTE_IN_SECONDS;

	/** Transient que limita la frecuencia del lazy run. */
	private const LAZY_TRANSIENT = 'orp_reaper_lastrun';

	/** Opción con el contador de caducados sin leer (aviso acumulable). */
	public const EXPIRED_NOTICE_OPTION = 'orp_expired_unread';

	/** Umbral de «agente sin heartbeat» en segundos (Doc/03 §11). */
	public const AGENT_OFFLINE_SECONDS = 3 * MINUTE_IN_SECONDS;

	/** Registra el cron y el lazy run. */
	public static function register(): void {
		add_action( self::CRON_HOOK, array( self::class, 'run' ) );
		add_action( 'rest_api_init', array( self::class, 'lazy_run' ), 5 );
		self::ensure_schedule();
	}

	/** Programa el cron horario si no está ya programado. */
	public static function ensure_schedule(): void {
		if ( false === wp_next_scheduled( self::CRON_HOOK ) ) {
			wp_schedule_event( time() + HOUR_IN_SECONDS, 'hourly', self::CRON_HOOK );
		}
	}

	/** Retira el cron (desactivación del plugin). */
	public static function deactivate(): void {
		$timestamp = wp_next_scheduled( self::CRON_HOOK );

		while ( false !== $timestamp && null !== $timestamp ) {
			wp_unschedule_event( (int) $timestamp, self::CRON_HOOK );
			$timestamp = wp_next_scheduled( self::CRON_HOOK );
		}
	}

	/**
	 * Ejecución perezosa: en cada petición REST se mira (lectura barata) si
	 * toca; si toca, se ejecuta y se apunta la hora. Respaldo para hostings
	 * sin cron real: el agente hace poll cada 10 s, así que el reaper corre
	 * aunque WP-Cron esté dormido.
	 */
	public static function lazy_run(): void {
		if ( get_transient( self::LAZY_TRANSIENT ) ) {
			return;
		}

		// Se marca ANTES de correr: si la ejecución muere a medias, no se
		// repite en cada petición hasta que pase el intervalo.
		set_transient( self::LAZY_TRANSIENT, time(), self::LAZY_INTERVAL_SECONDS );

		self::run();
	}

	/**
	 * Ejecuta las cuatro tareas de limpieza. Devuelve los conteos (para
	 * pruebas y CLI).
	 *
	 * @return array{requeued: int, exhausted: int, expired: int, jobs_deleted: int, logs_deleted: int}
	 */
	public static function run(): array {
		global $wpdb;

		$jobs_table = $wpdb->prefix . 'orp_jobs';
		$now        = Clock::now_sql();
		$lease_edge = gmdate( 'Y-m-d H:i:s', time() - self::LEASE_SECONDS );
		$summary    = array(
			'requeued'     => 0,
			'exhausted'    => 0,
			'expired'      => 0,
			'jobs_deleted' => 0,
			'logs_deleted' => 0,
		);

		// 1. Caducidad: pending/claimed con expires_at pasado → expired.
		//    Un trabajo caducado nunca se imprime (Doc/04 §4). Los pending ya
		//    no se servían (JobsController filtra por expires_at) y los
		//    claimed con el lease vivo también caen aquí: no hay ack posible
		//    tras la caducidad. En ambos casos se limpia el reclamo.
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- UPDATE masivo atómico.
		$summary['expired'] = (int) $wpdb->query(
			$wpdb->prepare(
				"UPDATE {$jobs_table}
				    SET status = 'expired', claimed_by = NULL, claimed_at = NULL
				  WHERE status IN ( 'pending', 'claimed' )
				    AND expires_at <= %s",
				$now
			)
		);

		if ( $summary['expired'] > 0 ) {
			// Aviso acumulable para el panel: la marca se consume al abrir la Cola.
			$pending_notice = (int) get_option( self::EXPIRED_NOTICE_OPTION, 0 );
			update_option( self::EXPIRED_NOTICE_OPTION, $pending_notice + $summary['expired'], false );

			Logger::log(
				'warning',
				'jobs',
				'Trabajos caducados sin imprimir: se marcan expired (nunca se imprimen, Doc/04 §4).',
				null,
				null,
				array( 'caducados' => $summary['expired'] )
			);
		}

		// 2. Lease vencido (>120 s sin ack) y aún con intentos: vuelve a
		//    pending SIN tocar attempts (el claim ya los consumió).
		//    El WHERE lleva expires_at > now: un caducado ya salió en el paso 1
		//    y no debe reanimarse.
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- UPDATE masivo atómico.
		$summary['requeued'] = (int) $wpdb->query(
			$wpdb->prepare(
				"UPDATE {$jobs_table}
				    SET status = 'pending', claimed_by = NULL, claimed_at = NULL
				  WHERE status = 'claimed'
				    AND claimed_at IS NOT NULL
				    AND claimed_at < %s
				    AND attempts < max_attempts
				    AND expires_at > %s",
				$lease_edge,
				$now
			)
		);

		// 3. Lease vencido con los intentos agotados: failed con el último error.
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- UPDATE masivo atómico.
		$summary['exhausted'] = (int) $wpdb->query(
			$wpdb->prepare(
				"UPDATE {$jobs_table}
				    SET status = 'failed', claimed_by = NULL, claimed_at = NULL, last_error = %s
				  WHERE status = 'claimed'
				    AND claimed_at IS NOT NULL
				    AND claimed_at < %s
				    AND attempts >= max_attempts
				    AND expires_at > %s",
				__( 'Intentos agotados: el agente no confirmó el trabajo dentro del arrendamiento.', 'orp-print' ),
				$lease_edge,
				$now
			)
		);

		// 4. Caso borde «pending agotado» (p. ej. un release tras agotar
		//    intentos): ningún agente puede reclamarlo (claim exige
		//    attempts < max_attempts); se marca failed para que no quede
		//    pendiente eterno y el panel pueda reencolarlo a mano.
		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- UPDATE masivo atómico.
		$pending_exhausted = (int) $wpdb->query(
			$wpdb->prepare(
				"UPDATE {$jobs_table}
				    SET status = 'failed', last_error = %s
				  WHERE status = 'pending'
				    AND attempts >= max_attempts
				    AND expires_at > %s",
				__( 'Intentos agotados sin agente al cargo.', 'orp-print' ),
				$now
			)
		);
		$summary['exhausted'] += $pending_exhausted;

		if ( $summary['requeued'] + $summary['exhausted'] > 0 ) {
			Logger::log(
				'warning',
				'jobs',
				'Reaper: arrendamientos vencidos devueltos a la cola o marcados failed.',
				null,
				null,
				array(
					'devueltos' => $summary['requeued'],
					'failed'    => $summary['exhausted'],
				)
			);
		}

		// 5. Retención de orp_jobs (Doc/04 §2.7: retention_days, 90 por
		//    defecto). Se limpian TODOS los estados: un trabajo con más de
		//    90 días no aporta (ni pending, que ya estaría caducado).
		//    retention_days = 0 significa «conservar siempre»: no se borra
		//    nada (el panel acota a ≥1; solo editando la opción directamente
		//    se puede poner 0, y la limpieza respeta esa decisión).
		$retention_days = (int) Settings::get( 'retention_days', 90 );

		if ( $retention_days > 0 ) {
			$jobs_edge = gmdate( 'Y-m-d H:i:s', time() - ( $retention_days * DAY_IN_SECONDS ) );

			// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Retención.
			$summary['jobs_deleted'] = (int) $wpdb->query(
				$wpdb->prepare(
					"DELETE FROM {$jobs_table} WHERE created_at < %s",
					$jobs_edge
				)
			);
		}

		// 6. Retención de orp_logs: 30 días (Doc/04 §2.6). Sin datos personales.
		$logs_edge = gmdate( 'Y-m-d H:i:s', time() - ( self::LOG_RETENTION_DAYS * DAY_IN_SECONDS ) );

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Retención.
		$summary['logs_deleted'] = (int) $wpdb->query(
			$wpdb->prepare(
				"DELETE FROM {$wpdb->prefix}orp_logs WHERE created_at < %s",
				$logs_edge
			)
		);

		return $summary;
	}

	/**
	 * Agentes ACTIVOS sin heartbeat reciente (Doc/03 §11): last_seen con más
	 * de 3 minutos o nulo (emparejado y sin llegar a latir). Para los avisos
	 * del panel y la cabecera de la Cola.
	 *
	 * @return array<object> Filas de orp_agents + name del local.
	 */
	public static function disconnected_agents(): array {
		global $wpdb;

		$agents_table    = $wpdb->prefix . 'orp_agents';
		$locations_table = $wpdb->prefix . 'orp_locations';
		$edge            = gmdate( 'Y-m-d H:i:s', time() - self::AGENT_OFFLINE_SECONDS );

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		return (array) $wpdb->get_results(
			$wpdb->prepare(
				"SELECT a.id, a.name, a.agent_version, a.last_seen_at, l.name AS location_name
				   FROM {$agents_table} a
				   LEFT JOIN {$locations_table} l ON l.id = a.location_id
				  WHERE a.status = 'active'
				    AND ( a.last_seen_at IS NULL OR a.last_seen_at < %s )
				  ORDER BY a.id ASC",
				$edge
			)
		);
	}

	/** Contador de trabajos caducados sin leer (no consume). */
	public static function expired_notice_count(): int {
		return (int) get_option( self::EXPIRED_NOTICE_OPTION, 0 );
	}

	/**
	 * Consume el aviso de caducados: se llama al abrir la página Cola (el
	 * aviso se ve en cualquier página admin hasta que el administrador abre
	 * la cola, donde puede actuar — reencolar — y entonces se limpia).
	 */
	public static function clear_expired_notice(): void {
		if ( get_option( self::EXPIRED_NOTICE_OPTION, 0 ) ) {
			update_option( self::EXPIRED_NOTICE_OPTION, 0, false );
		}
	}
}
