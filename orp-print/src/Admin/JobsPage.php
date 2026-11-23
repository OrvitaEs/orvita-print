<?php
/**
 * Página «Cola» (F1-08): lista paginada de trabajos con filtros por estado,
 * local y fecha; acciones por fila con nonce (cancelar y reencolar) y vista
 * del documento guardado; cabecera con el estado de los agentes por local.
 *
 * Decisiones de diseño (Doc/07 §3 F1-08):
 * - Reencolar restaura LA MISMA fila a pending (attempts = 0, sin reclamo,
 *   caducidad nueva): no crea trabajo nuevo. La reimpresión con documento
 *   regenerado ya existe como flujo en la ficha del pedido (F1-04); aquí el
 *   objetivo es operativo: reintentar lo que ya está en la cola. El
 *   idempotency_key de la fila se conserva, así que no puede duplicarse nada.
 * - Las acciones van por ENLACE con nonce (patrón de Locales y reglas) y se
 *   procesan en admin_init sobre la misma petición, sin redirección: los
 *   filtros se conservan y el resultado se muestra con settings_errors().
 * - Una salida que el agente DEJÓ de reportar se muestra con la marca «sin
 *   confirmar» y SE CONSERVA (ni se borra ni se desactiva): es lo menos
 *   destructivo con la cola — los trabajos para su rol siguen su curso y la
 *   decisión (baja real) la toma el agente reportándola enabled = 0.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Admin;

use Orvita\PrintBridge\Domain\DestinationRepository;
use Orvita\PrintBridge\Domain\LocationRepository;
use Orvita\PrintBridge\Domain\Reaper;
use Orvita\PrintBridge\Support\Clock;
use Orvita\PrintBridge\Support\Logger;
use Orvita\PrintBridge\Support\Settings;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class JobsPage {

	/** Trabajos por página. */
	public const PER_PAGE = 20;

	/** Estados válidos de la cola (Doc/04 §2.4). */
	public const STATUSES = array( 'pending', 'claimed', 'delivered', 'failed', 'expired', 'cancelled' );

	/** Tolerancia para considerar «reportada» una salida en el último latido, en segundos. */
	private const REPORTED_TOLERANCE_SECONDS = 60;

	/** Procesa las acciones de fila antes de renderizar. */
	public static function register(): void {
		add_action( 'admin_init', array( self::class, 'maybe_handle' ) );
	}

	/** Cancelar y reencolar (enlaces con nonce, sin redirección). */
	public static function maybe_handle(): void {
		if ( ! is_admin() || ( isset( $_GET['page'] ) && Menu::SLUG_JOBS !== $_GET['page'] ) ) {
			return;
		}

		if ( ! current_user_can( ORP_CAPABILITY ) ) {
			return;
		}

		$action = isset( $_REQUEST['orp_action'] ) ? sanitize_key( (string) wp_unslash( $_REQUEST['orp_action'] ) ) : '';
		if ( '' === $action ) {
			return;
		}

		$id = isset( $_GET['id'] ) ? absint( $_GET['id'] ) : 0;
		if ( 0 === $id ) {
			return;
		}

		switch ( $action ) {
			case 'cancel':
				check_admin_referer( 'orp_cancel_job_' . $id );

				if ( self::cancel_job( $id ) ) {
					add_settings_error(
						'orp-jobs',
						'orp_job_cancelled',
						sprintf(
							/* translators: %d: id del trabajo. */
							__( 'Trabajo #%d cancelado: no se imprimirá.', 'orp-print' ),
							$id
						),
						'updated'
					);
				} else {
					add_settings_error(
						'orp-jobs',
						'orp_job_cancel_error',
						sprintf(
							/* translators: %d: id del trabajo. */
							__( 'El trabajo #%d no se pudo cancelar (solo pending o claimed; lo ya impreso no se desimprime).', 'orp-print' ),
							$id
						),
						'error'
					);
				}
				break;

			case 'requeue':
				check_admin_referer( 'orp_requeue_job_' . $id );

				if ( self::requeue_job( $id ) ) {
					add_settings_error(
						'orp-jobs',
						'orp_job_requeued',
						sprintf(
							/* translators: %d: id del trabajo. */
							__( 'Trabajo #%d reencolado: vuelve a pending con los intentos a cero y caducidad nueva. Es la MISMA fila: no se duplica nada.', 'orp-print' ),
							$id
						),
						'updated'
					);
				} else {
					add_settings_error(
						'orp-jobs',
						'orp_job_requeue_error',
						sprintf(
							/* translators: %d: id del trabajo. */
							__( 'El trabajo #%d no se puede reencolar (solo failed o expired).', 'orp-print' ),
							$id
						),
						'error'
					);
				}
				break;
		}
	}

	/* ---------------------------------------------------------------------
	 * Transiciones de estado (públicas para las pruebas PHPUnit).
	 * ------------------------------------------------------------------- */

	/**
	 * Cancela un trabajo: pending/claimed → cancelled (Doc/04 §9: «anulado
	 * desde el panel»). Lo entregado o fallado no se toca: el papel no se
	 * desimprime.
	 */
	public static function cancel_job( int $job_id ): bool {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- UPDATE atómico, sin caché.
		$affected = $wpdb->query(
			$wpdb->prepare(
				"UPDATE {$wpdb->prefix}orp_jobs
				    SET status = 'cancelled', claimed_by = NULL, claimed_at = NULL
				  WHERE id = %d AND status IN ( 'pending', 'claimed' )",
				$job_id
			)
		);

		if ( (int) $affected > 0 ) {
			Logger::log( 'info', 'jobs', 'Trabajo cancelado desde el panel.', $job_id, null, array() );

			return true;
		}

		return false;
	}

	/**
	 * Reencola un trabajo fallido o caducado: la MISMA fila vuelve a pending
	 * con attempts = 0, sin reclamo y con caducidad nueva (job_expiry_minutes
	 * de orp_settings). No crea fila nueva: el idempotency_key se conserva y
	 * nada se duplica. El documento es el que se generó al crear el trabajo.
	 */
	public static function requeue_job( int $job_id ): bool {
		global $wpdb;

		$expires = Clock::sql_offset_minutes( (int) Settings::get( 'job_expiry_minutes', 30 ) );

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- UPDATE atómico, sin caché.
		$affected = $wpdb->query(
			$wpdb->prepare(
				"UPDATE {$wpdb->prefix}orp_jobs
				    SET status = 'pending', attempts = 0, claimed_by = NULL, claimed_at = NULL,
				        last_error = NULL, expires_at = %s
				  WHERE id = %d AND status IN ( 'failed', 'expired' )",
				$expires,
				$job_id
			)
		);

		if ( (int) $affected > 0 ) {
			Logger::log( 'info', 'jobs', 'Trabajo reencolado desde el panel (misma fila, intentos a cero).', $job_id, null, array() );

			return true;
		}

		return false;
	}

	/* ---------------------------------------------------------------------
	 * Render.
	 * ------------------------------------------------------------------- */

	/** Pinta la página. */
	public static function render(): void {
		if ( ! current_user_can( ORP_CAPABILITY ) ) {
			wp_die( esc_html__( 'No tienes permisos para ver esta página.', 'orp-print' ) );
		}

		// El aviso de caducados se limpia aquí: es la página donde el
		// administrador puede actuar (reencolar). admin_notices ya lo pintó.
		Reaper::clear_expired_notice();

		$view_id = isset( $_GET['orp_view'] ) ? absint( $_GET['orp_view'] ) : 0;
		?>
		<div class="wrap">
			<h1><?php echo esc_html( __( 'Cola de impresión', 'orp-print' ) ); ?></h1>
			<?php settings_errors( 'orp-jobs' ); ?>

			<p class="description">
				<?php esc_html_e( 'Un trabajo por pedido y salida: cada fila es un documento para una impresora. Los pending con la caducidad pasada ya no se sirven al agente; el Reaper los marca expired.', 'orp-print' ); ?>
			</p>

			<?php self::render_agents_header(); ?>

			<?php if ( $view_id > 0 ) : ?>
				<?php self::render_document( $view_id ); ?>
			<?php endif; ?>

			<?php self::render_filters(); ?>
			<?php self::render_table(); ?>
		</div>
		<?php
	}

	/**
	 * Cabecera con el estado de los agentes: por local, agente
	 * conectado/desconectado (last_seen > 3 min → desconectado, Doc/03 §11),
	 * versión y salidas reportadas (con la marca «sin confirmar» para las
	 * que el último latido ya no incluye).
	 */
	private static function render_agents_header(): void {
		global $wpdb;

		$agents_table = $wpdb->prefix . 'orp_agents';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$agents = (array) $wpdb->get_results(
			"SELECT a.id, a.name, a.status, a.agent_version, a.platform, a.last_seen_at, a.location_id,
			        l.name AS location_name, l.code AS location_code
			   FROM {$agents_table} a
			   LEFT JOIN {$wpdb->prefix}orp_locations l ON l.id = a.location_id
			  ORDER BY a.location_id ASC, a.id ASC"
		);

		$destinations = DestinationRepository::all();

		// Último latido ACTIVO por local (V1: un agente por local; si hay
		// varios, el más reciente define qué salidas se esperan).
		$last_seen_by_location = array();
		foreach ( $agents as $agent ) {
			$loc = (int) $agent->location_id;
			if ( 'active' !== (string) $agent->status || null === $agent->last_seen_at ) {
				continue;
			}
			$seen = (int) strtotime( (string) $agent->last_seen_at . ' UTC' );
			if ( ! isset( $last_seen_by_location[ $loc ] ) || $seen > $last_seen_by_location[ $loc ] ) {
				$last_seen_by_location[ $loc ] = $seen;
			}
		}

		if ( array() === $agents && array() === $destinations ) {
			return;
		}
		?>
		<h2><?php echo esc_html( __( 'Agentes por local', 'orp-print' ) ); ?></h2>
		<table class="widefat striped" style="max-width:1100px">
			<thead>
				<tr>
					<th><?php esc_html_e( 'Local', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Agente', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Conexión', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Versión', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Salidas reportadas', 'orp-print' ); ?></th>
				</tr>
			</thead>
			<tbody>
				<?php foreach ( $agents as $agent ) : ?>
					<?php
					$loc        = (int) $agent->location_id;
					$seen       = null !== $agent->last_seen_at ? (int) strtotime( (string) $agent->last_seen_at . ' UTC' ) : null;
					$connected  = null !== $seen && ( time() - $seen ) < Reaper::AGENT_OFFLINE_SECONDS;
					$agent_dest = array_values(
						array_filter(
							$destinations,
							static fn( $destination ) => (int) $destination->location_id === $loc
						)
					);
					$last_seen  = $last_seen_by_location[ $loc ] ?? null;
					?>
					<tr>
						<td><?php echo esc_html( (string) ( $agent->location_name ?? sprintf( /* translators: %d: id de local. */ __( 'Local #%d (borrado)', 'orp-print' ), $loc ) ) ); ?></td>
						<td><?php echo esc_html( (string) $agent->name ); ?></td>
						<td>
							<?php if ( 'revoked' === (string) $agent->status ) : ?>
								<span style="color:#8c8f94"><?php esc_html_e( 'revocado', 'orp-print' ); ?></span>
							<?php elseif ( $connected ) : ?>
								<span style="color:#00a32a">●</span> <?php esc_html_e( 'conectado', 'orp-print' ); ?>
							<?php else : ?>
								<span style="color:#dba617">●</span> <?php esc_html_e( 'desconectado', 'orp-print' ); ?>
							<?php endif; ?>
							<span class="description">
								(<?php echo esc_html( null !== $seen ? wp_date( 'd/m/Y H:i', $seen ) : __( 'sin latido nunca', 'orp-print' ) ); ?>)
							</span>
						</td>
						<td><?php echo esc_html( (string) ( $agent->agent_version ?? '' ) ); ?></td>
						<td>
							<?php if ( array() === $agent_dest ) : ?>
								<span class="description"><?php esc_html_e( 'ninguna todavía', 'orp-print' ); ?></span>
							<?php else : ?>
								<?php foreach ( $agent_dest as $destination ) : ?>
									<div>
										<code><?php echo esc_html( (string) $destination->role ); ?></code>
										<?php
										echo esc_html(
											sprintf(
												/* translators: 1: etiqueta, 2: impresora, 3: copias. */
												__( '%1$s · %2$s ×%3$d', 'orp-print' ),
												(string) $destination->label,
												(string) $destination->printer_name,
												(int) $destination->copies
											)
										);
										?>
										<?php if ( ! self::destination_reported( $destination, $last_seen ) ) : ?>
											<span style="color:#dba617">— <?php esc_html_e( 'sin confirmar', 'orp-print' ); ?></span>
										<?php endif; ?>
										<?php if ( 1 !== (int) $destination->enabled ) : ?>
											<span class="description">— <?php esc_html_e( 'desactivada', 'orp-print' ); ?></span>
										<?php endif; ?>
									</div>
								<?php endforeach; ?>
							<?php endif; ?>
						</td>
					</tr>
				<?php endforeach; ?>
			</tbody>
		</table>
		<?php
	}

	/**
	 * ¿Reportó el agente esta salida en su último latido? El heartbeat
	 * actualiza last_seen_at y refresca updated_at de CADA salida reportada;
	 * si updated_at se queda más atrás que el latido (más que la tolerancia),
	 * la última llamada ya no la traía → «sin confirmar».
	 */
	private static function destination_reported( object $destination, ?int $last_seen_utc ): bool {
		if ( null === $last_seen_utc ) {
			return true;
		}

		$updated = (int) strtotime( (string) $destination->updated_at . ' UTC' );

		return $updated >= $last_seen_utc - self::REPORTED_TOLERANCE_SECONDS;
	}

	/** Vista del documento guardado en el payload (JSON bonito, solo lectura). */
	private static function render_document( int $job_id ): void {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$job = $wpdb->get_row(
			$wpdb->prepare( "SELECT * FROM {$wpdb->prefix}orp_jobs WHERE id = %d", $job_id )
		);

		if ( null === $job ) {
			echo '<div class="notice notice-error inline"><p>' . esc_html__( 'El trabajo solicitado no existe.', 'orp-print' ) . '</p></div>';

			return;
		}

		$document = json_decode( (string) $job->payload, true );
		?>
		<hr />
		<h2>
			<?php
			printf(
				/* translators: %d: id del trabajo. */
				esc_html__( 'Documento del trabajo #%d', 'orp-print' ),
				(int) $job->id
			);
			?>
		</h2>
		<p>
			<a href="<?php echo esc_url( admin_url( 'admin.php?page=' . Menu::SLUG_JOBS ) ); ?>"><?php esc_html_e( '← Volver a la cola', 'orp-print' ); ?></a>
		</p>
		<table class="widefat striped" style="max-width:1100px">
			<tbody>
				<tr><th style="width:160px"><?php esc_html_e( 'Estado', 'orp-print' ); ?></th><td><?php echo esc_html( (string) $job->status ); ?></td></tr>
				<tr><th><?php esc_html_e( 'Pedido', 'orp-print' ); ?></th><td><?php echo esc_html( '#' . (int) $job->order_id ); ?></td></tr>
				<tr><th><?php esc_html_e( 'Salida', 'orp-print' ); ?></th><td><code><?php echo esc_html( (string) $job->printer_role ); ?></code> · <?php echo esc_html( sprintf( /* translators: %d: copias. */ __( '%d copias (congeladas al crear)', 'orp-print' ), (int) $job->copies ) ); ?></td></tr>
				<tr><th><?php esc_html_e( 'Intentos', 'orp-print' ); ?></th><td><?php echo esc_html( (int) $job->attempts . ' / ' . (int) $job->max_attempts ); ?></td></tr>
				<tr><th><?php esc_html_e( 'Último error', 'orp-print' ); ?></th><td><?php echo esc_html( null !== $job->last_error && '' !== (string) $job->last_error ? (string) $job->last_error : __( '—', 'orp-print' ) ); ?></td></tr>
				<tr><th><?php esc_html_e( 'Creado', 'orp-print' ); ?></th><td><?php echo esc_html( Clock::to_iso( $job->created_at ) ?? '—' ); ?></td></tr>
				<tr><th><?php esc_html_e( 'Caduca', 'orp-print' ); ?></th><td><?php echo esc_html( Clock::to_iso( $job->expires_at ) ?? '—' ); ?></td></tr>
				<tr><th><?php esc_html_e( 'Entregado', 'orp-print' ); ?></th><td><?php echo esc_html( Clock::to_iso( $job->delivered_at ) ?? __( 'no', 'orp-print' ) ); ?></td></tr>
			</tbody>
		</table>
		<h3><?php esc_html_e( 'Documento neutro (payload, versión 1)', 'orp-print' ); ?></h3>
		<pre style="background:#fff;border:1px solid #c3c4c7;padding:12px;max-width:1100px;overflow:auto;max-height:600px"><?php
			// phpcs:ignore WordPress.PHP.DevelopmentFunctions.error_log_var_export -- Vista de diagnóstico.
			echo esc_html(
				is_array( $document )
					? (string) wp_json_encode( $document, JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE )
					: (string) $job->payload
			);
		?></pre>
		<?php
	}

	/** Filtros por estado, local y fecha (GET; conservan la paginación a cero). */
	private static function render_filters(): void {
		$locations = LocationRepository::all();
		$status    = self::filter_status();
		$location  = self::filter_location();
		$date      = self::filter_date();
		?>
		<form method="get" action="">
			<input type="hidden" name="page" value="<?php echo esc_attr( Menu::SLUG_JOBS ); ?>" />
			<div style="display:flex;gap:12px;align-items:end;flex-wrap:wrap;margin:12px 0">
				<label>
					<?php esc_html_e( 'Estado', 'orp-print' ); ?><br />
					<select name="status">
						<option value=""><?php esc_html_e( 'Todos', 'orp-print' ); ?></option>
						<?php foreach ( self::STATUSES as $option ) : ?>
							<option value="<?php echo esc_attr( $option ); ?>" <?php selected( $status, $option ); ?>><?php echo esc_html( $option ); ?></option>
						<?php endforeach; ?>
					</select>
				</label>
				<label>
					<?php esc_html_e( 'Local', 'orp-print' ); ?><br />
					<select name="location">
						<option value=""><?php esc_html_e( 'Todos', 'orp-print' ); ?></option>
						<?php foreach ( $locations as $single ) : ?>
							<option value="<?php echo esc_attr( (string) $single['id'] ); ?>" <?php selected( $location, (int) $single['id'] ); ?>><?php echo esc_html( (string) $single['name'] ); ?></option>
						<?php endforeach; ?>
					</select>
				</label>
				<label>
					<?php esc_html_e( 'Fecha (creado)', 'orp-print' ); ?><br />
					<input type="date" name="date" value="<?php echo esc_attr( $date ); ?>" />
				</label>
				<label>
					<?php esc_html_e( 'Por página', 'orp-print' ); ?><br />
					<input type="number" name="per_page" min="5" max="100" value="<?php echo esc_attr( (string) self::filter_per_page() ); ?>" class="small-text" />
				</label>
				<?php submit_button( __( 'Filtrar', 'orp-print' ), 'secondary', '', false, array( 'style' => 'margin-bottom:0' ) ); ?>
				<a href="<?php echo esc_url( admin_url( 'admin.php?page=' . Menu::SLUG_JOBS ) ); ?>"><?php esc_html_e( 'Limpiar', 'orp-print' ); ?></a>
			</div>
		</form>
		<?php
	}

	/** Tabla paginada de trabajos con acciones de fila. */
	private static function render_table(): void {
		global $wpdb;

		$where  = array( '1=1' );
		$params = array();

		$status = self::filter_status();
		if ( '' !== $status ) {
			$where[]  = 'status = %s';
			$params[] = $status;
		}

		$location = self::filter_location();
		if ( $location > 0 ) {
			$where[]  = 'location_id = %d';
			$params[] = $location;
		}

		$date = self::filter_date();
		if ( '' !== $date ) {
			// El administrador piensa en su zona horaria; la fila guarda UTC.
			$tz        = wp_timezone();
			$day_start = new \DateTimeImmutable( $date . ' 00:00:00', $tz );
			$utc_start = $day_start->setTimezone( new \DateTimeZone( 'UTC' ) )->format( 'Y-m-d H:i:s' );
			$utc_end   = $day_start->modify( '+1 day' )->setTimezone( new \DateTimeZone( 'UTC' ) )->format( 'Y-m-d H:i:s' );

			$where[]  = 'created_at >= %s AND created_at < %s';
			$params[] = $utc_start;
			$params[] = $utc_end;
		}

		$where_sql = implode( ' AND ', $where );
		$table     = $wpdb->prefix . 'orp_jobs';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$total = (int) $wpdb->get_var(
			$wpdb->prepare( "SELECT COUNT(*) FROM {$table} WHERE {$where_sql}", $params )
		);

		$per_page = self::filter_per_page();
		$paged    = max( 1, isset( $_GET['paged'] ) ? absint( $_GET['paged'] ) : 1 );
		$pages    = max( 1, (int) ceil( $total / $per_page ) );
		$paged    = min( $paged, $pages );
		$offset   = ( $paged - 1 ) * $per_page;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$jobs = (array) $wpdb->get_results(
			$wpdb->prepare(
				"SELECT id, location_id, order_id, printer_role, copies, status, attempts, max_attempts,
				        created_at, expires_at, delivered_at, last_error
				   FROM {$table}
				  WHERE {$where_sql}
				  ORDER BY id DESC
				  LIMIT %d OFFSET %d",
				array_merge( $params, array( $per_page, $offset ) )
			)
		);

		$locations = array();
		foreach ( LocationRepository::all() as $single ) {
			$locations[ (int) $single['id'] ] = (string) $single['name'];
		}
		?>
		<table class="widefat striped" style="max-width:1100px">
			<thead>
				<tr>
					<th><?php esc_html_e( 'Id', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Pedido', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Local', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Salida', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Copias', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Estado', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Intentos', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Creado', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Caduca', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Entregado', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Último error', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Acciones', 'orp-print' ); ?></th>
				</tr>
			</thead>
			<tbody>
				<?php if ( array() === $jobs ) : ?>
					<tr><td colspan="12"><?php esc_html_e( 'Sin trabajos con estos filtros.', 'orp-print' ); ?></td></tr>
				<?php else : ?>
					<?php foreach ( $jobs as $job ) : ?>
						<?php $job_id = (int) $job->id; ?>
						<tr>
							<td><?php echo esc_html( (string) $job_id ); ?></td>
							<td>
								<?php
								$link = self::order_link( (int) $job->order_id );
								echo null !== $link
									? '<a href="' . esc_url( $link ) . '">#' . esc_html( (string) $job->order_id ) . '</a>'
									: '—';
								?>
							</td>
							<td><?php echo esc_html( $locations[ (int) $job->location_id ] ?? sprintf( /* translators: %d: id de local. */ __( 'Local #%d (borrado)', 'orp-print' ), (int) $job->location_id ) ); ?></td>
							<td><code><?php echo esc_html( (string) $job->printer_role ); ?></code></td>
							<td><?php echo esc_html( (string) $job->copies ); ?></td>
							<td><?php echo esc_html( (string) $job->status ); ?></td>
							<td><?php echo esc_html( (int) $job->attempts . ' / ' . (int) $job->max_attempts ); ?></td>
							<td><?php echo esc_html( self::date_short( (string) $job->created_at ) ); ?></td>
							<td><?php echo esc_html( self::date_short( (string) $job->expires_at ) ); ?></td>
							<td><?php echo esc_html( null !== $job->delivered_at ? self::date_short( (string) $job->delivered_at ) : '—' ); ?></td>
							<td style="max-width:220px;overflow:hidden;text-overflow:ellipsis"><?php echo esc_html( null !== $job->last_error && '' !== (string) $job->last_error ? (string) $job->last_error : '—' ); ?></td>
							<td>
								<a href="<?php echo esc_url( admin_url( 'admin.php?page=' . Menu::SLUG_JOBS . '&orp_view=' . $job_id ) ); ?>"><?php esc_html_e( 'Ver documento', 'orp-print' ); ?></a>
								<?php if ( in_array( (string) $job->status, array( 'pending', 'claimed' ), true ) ) : ?>
									| <a href="<?php echo esc_url( wp_nonce_url( admin_url( 'admin.php?page=' . Menu::SLUG_JOBS . '&orp_action=cancel&id=' . $job_id ), 'orp_cancel_job_' . $job_id ) ); ?>"><?php esc_html_e( 'Cancelar', 'orp-print' ); ?></a>
								<?php endif; ?>
								<?php if ( in_array( (string) $job->status, array( 'failed', 'expired' ), true ) ) : ?>
									| <a href="<?php echo esc_url( wp_nonce_url( admin_url( 'admin.php?page=' . Menu::SLUG_JOBS . '&orp_action=requeue&id=' . $job_id ), 'orp_requeue_job_' . $job_id ) ); ?>"><?php esc_html_e( 'Reencolar', 'orp-print' ); ?></a>
								<?php endif; ?>
							</td>
						</tr>
					<?php endforeach; ?>
				<?php endif; ?>
			</tbody>
		</table>

		<?php if ( $pages > 1 ) : ?>
			<div class="tablenav" style="max-width:1100px">
				<div class="tablenav-pages">
					<?php
					echo wp_kses_post(
						paginate_links(
							array(
								'base'      => add_query_arg( 'paged', '%#%' ),
								'format'    => '',
								'current'   => $paged,
								'total'     => $pages,
								'prev_text' => '«',
								'next_text' => '»',
							)
						) ?? ''
					);
					?>
				</div>
			</div>
		<?php endif; ?>
		<?php
	}

	/* ---------------------------------------------------------------------
	 * Filtros y utilidades.
	 * ------------------------------------------------------------------- */

	/** Estado del filtro (whitelist de los 6 estados). */
	private static function filter_status(): string {
		$status = isset( $_GET['status'] ) ? sanitize_key( (string) wp_unslash( $_GET['status'] ) ) : '';

		return in_array( $status, self::STATUSES, true ) ? $status : '';
	}

	/** Local del filtro. */
	private static function filter_location(): int {
		return isset( $_GET['location'] ) ? absint( $_GET['location'] ) : 0;
	}

	/** Fecha del filtro (Y-m-d o vacío). */
	private static function filter_date(): string {
		$date = isset( $_GET['date'] ) ? sanitize_text_field( (string) wp_unslash( $_GET['date'] ) ) : '';

		return preg_match( '/^\d{4}-\d{2}-\d{2}$/', $date ) ? $date : '';
	}

	/** Trabajos por página (entre 5 y 100; 20 por defecto). */
	private static function filter_per_page(): int {
		$per_page = isset( $_GET['per_page'] ) ? absint( $_GET['per_page'] ) : self::PER_PAGE;

		return max( 5, min( 100, $per_page ) );
	}

	/** Fecha corta local a partir de un datetime UTC de BD. */
	private static function date_short( string $utc ): string {
		$timestamp = strtotime( $utc . ' UTC' );

		return false !== $timestamp ? wp_date( 'd/m H:i', $timestamp ) : '—';
	}

	/** Enlace de edición del pedido, compatible con HPOS y con posts. */
	private static function order_link( int $order_id ): ?string {
		if ( $order_id <= 0 ) {
			return null;
		}

		if ( class_exists( \Automattic\WooCommerce\Utilities\OrderUtil::class )
			&& \Automattic\WooCommerce\Utilities\OrderUtil::custom_orders_table_usage_is_enabled() ) {
			return admin_url( 'admin.php?page=wc-orders&action=edit&id=' . $order_id );
		}

		return admin_url( 'post.php?post=' . $order_id . '&action=edit' );
	}
}
