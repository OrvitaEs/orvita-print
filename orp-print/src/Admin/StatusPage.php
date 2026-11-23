<?php
/**
 * Página «Estado» — diagnóstico completo (F1-10, Doc/03 §11).
 *
 * Solo lectura, sin formularios y sin datos personales: versiones (plugin,
 * API, WordPress, WooCommerce, PHP, HPOS, config_version, template_version),
 * comprobaciones automáticas (✅/⚠️), locales con sus reglas resumidas,
 * agentes con último latido/versión/salidas, resumen de la cola por estado
 * y los últimos 20 eventos de orp_logs con nivel y contexto.
 *
 * Soporte facilitado: esta página es lo primero que se pide al administrador
 * cuando algo no imprime (Doc/07 §3 F1-10).
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Admin;

use Orvita\PrintBridge\Domain\DestinationRepository;
use Orvita\PrintBridge\Domain\LocationRepository;
use Orvita\PrintBridge\Domain\Reaper;
use Orvita\PrintBridge\Rest\Router;
use Orvita\PrintBridge\Support\Settings;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class StatusPage {

	/** Número de eventos de orp_logs que muestra la página. */
	public const RECENT_EVENTS = 20;

	/** Estados de la cola en el orden del contrato (Doc/04 §2.4). */
	private const STATUSES = array( 'pending', 'claimed', 'delivered', 'failed', 'expired', 'cancelled' );

	/** Pinta la página. Solo lectura, sin formularios. */
	public static function render(): void {
		if ( ! current_user_can( ORP_CAPABILITY ) ) {
			wp_die( esc_html__( 'No tienes permisos para ver esta página.', 'orp-print' ) );
		}

		$checks = self::checks();
		?>
		<div class="wrap">
			<h1><?php echo esc_html( __( 'Estado de Orvita Print', 'orp-print' ) ); ?></h1>
			<p class="description">
				<?php esc_html_e( 'Diagnóstico del plugin para soporte: comprobaciones automáticas, versiones, locales, agentes, cola y últimos eventos. No se muestra ningún dato personal.', 'orp-print' ); ?>
			</p>

			<?php self::render_checks( $checks ); ?>
			<?php self::render_versions(); ?>
			<?php self::render_locations(); ?>
			<?php self::render_agents(); ?>
			<?php self::render_queue(); ?>
			<?php self::render_recent_events(); ?>
		</div>
		<?php
	}

	/*
	 * ---------------------------------------------------------------------
	 * Comprobaciones automáticas (✅/⚠️).
	 * ---------------------------------------------------------------------
	 */

	/**
	 * Comprobaciones automáticas del entorno. Cada fila: etiqueta,
	 * ¿correcta? y detalle. El orden va de lo estructural a lo operativo.
	 *
	 * @return array<int, array{label: string, ok: bool, detail: string}>
	 */
	public static function checks(): array {
		global $wp_version;

		$checks = array();

		// WooCommerce activo y versión (HPOS y estados de pedido lo exigen).
		$woo_ok = defined( 'WC_VERSION' );
		$checks[] = array(
			'label'  => __( 'WooCommerce activo', 'orp-print' ),
			'ok'     => $woo_ok,
			'detail' => $woo_ok
				? sprintf( /* translators: %s: versión de WooCommerce. */ __( 'versión %s', 'orp-print' ), WC_VERSION )
				: __( 'no detectado: sin WooCommerce no hay pedidos que imprimir', 'orp-print' ),
		);

		// HPOS (tablas de pedido propias): activado y declarado compatible.
		$hpos = false;
		if ( class_exists( \Automattic\WooCommerce\Utilities\OrderUtil::class ) ) {
			$hpos = \Automattic\WooCommerce\Utilities\OrderUtil::custom_orders_table_usage_is_enabled();
		}
		$checks[] = array(
			'label'  => __( 'HPOS (tablas de pedido propias)', 'orp-print' ),
			'ok'     => $hpos,
			'detail' => $hpos
				? __( 'activo: los pedidos se leen por la API CRUD (compatible)', 'orp-print' )
				: __( 'desactivado: el plugin es compatible con las dos configuraciones', 'orp-print' ),
		);

		// Cron del reaper programado (limpieza sin trabajos zombis).
		$cron = wp_next_scheduled( Reaper::CRON_HOOK );
		$checks[] = array(
			'label'  => __( 'Cron de limpieza (Reaper)', 'orp-print' ),
			'ok'     => false !== $cron,
			'detail' => false !== $cron
				? sprintf(
					/* translators: %s: fecha y hora local de la próxima ejecución. */
					__( 'programado; próxima ejecución: %s', 'orp-print' ),
					wp_date( 'd/m/Y H:i', (int) $cron )
				)
				: __( 'sin programar: se reprograma al reactivar el plugin o al llamar a Reaper::ensure_schedule()', 'orp-print' ),
		);

		// GD disponible: el logo del ticket se genera a PNG con GD.
		$gd = extension_loaded( 'gd' ) && function_exists( 'imagecreatetruecolor' );
		$checks[] = array(
			'label'  => __( 'Extensión GD de PHP', 'orp-print' ),
			'ok'     => $gd,
			'detail' => $gd
				? __( 'disponible: el logo se convierte a PNG para la impresora térmica', 'orp-print' )
				: __( 'no disponible: el logo no se emitirá en el ticket (necesaria para el bloque «Logo»)', 'orp-print' ),
		);

		// Permalinks: sin ellos la REST API funciona por ?rest_route= pero
		// conviene tenerlos activos (y las URL del panel quedan limpias).
		$permalinks = (string) get_option( 'permalink_structure' );
		$checks[] = array(
			'label'  => __( 'Enlaces permanentes', 'orp-print' ),
			'ok'     => '' !== $permalinks,
			'detail' => '' !== $permalinks
				? __( 'activos: la API REST responde en /wp-json/', 'orp-print' )
				: __( 'en «simple»: la API responde solo por ?rest_route=; activa enlaces permanentes', 'orp-print' ),
		);

		// Capacidad del plugin: el rol administrator debe poder gestionarlo.
		$has_role = false;
		$admins   = get_role( 'administrator' );
		if ( $admins instanceof \WP_Role ) {
			$has_role = (bool) $admins->has_cap( ORP_CAPABILITY );
		}
		$checks[] = array(
			'label'  => __( 'Capacidad manage_orp_print', 'orp-print' ),
			'ok'     => $has_role && current_user_can( ORP_CAPABILITY ),
			'detail' => $has_role
				? __( 'asignada al rol administrator; tu usuario puede gestionar el plugin', 'orp-print' )
				: __( 'el rol administrator no la tiene: reactiva el plugin para restaurarla', 'orp-print' ),
		);

		// WordPress (informativa: la exigencia del plugin es 6.8+).
		$checks[] = array(
			'label'  => __( 'Versión de WordPress (mínimo 6.8)', 'orp-print' ),
			'ok'     => version_compare( (string) $wp_version, '6.8', '>=' ),
			'detail' => sprintf( /* translators: %s: versión de WordPress instalada. */ __( 'instalada: %s', 'orp-print' ), (string) $wp_version ),
		);

		// PHP (informativa: el soporte es 8.1–8.3).
		$checks[] = array(
			'label'  => __( 'Versión de PHP (soportada 8.1–8.3)', 'orp-print' ),
			'ok'     => version_compare( PHP_VERSION, '8.1', '>=' ) && version_compare( PHP_VERSION, '8.4', '<' ),
			'detail' => sprintf( /* translators: %s: versión de PHP instalada. */ __( 'versión instalada: %s', 'orp-print' ), PHP_VERSION ),
		);

		return $checks;
	}

	/** Tabla de comprobaciones con ✅/⚠️. */
	private static function render_checks( array $checks ): void {
		?>
		<h2><?php esc_html_e( 'Comprobaciones automáticas', 'orp-print' ); ?></h2>
		<table class="widefat striped" style="max-width:760px">
			<tbody>
				<?php foreach ( $checks as $check ) : ?>
					<tr>
						<td style="width:40px">
							<?php if ( $check['ok'] ) : ?>
								<span style="color:#00a32a;font-size:1.1em" aria-hidden="true">✅</span>
								<span class="screen-reader-text"><?php esc_html_e( 'Correcto', 'orp-print' ); ?></span>
							<?php else : ?>
								<span style="color:#dba617;font-size:1.1em" aria-hidden="true">⚠️</span>
								<span class="screen-reader-text"><?php esc_html_e( 'Revisar', 'orp-print' ); ?></span>
							<?php endif; ?>
						</td>
						<td><strong><?php echo esc_html( $check['label'] ); ?></strong></td>
						<td><?php echo esc_html( $check['detail'] ); ?></td>
					</tr>
				<?php endforeach; ?>
			</tbody>
		</table>
		<?php
	}

	/** Versiones del plugin, la API y el entorno (Doc/03 §11). */
	private static function render_versions(): void {
		$settings = Settings::all();
		?>
		<h2><?php esc_html_e( 'Versiones', 'orp-print' ); ?></h2>
		<table class="widefat striped" style="max-width:760px">
			<tbody>
				<tr><th style="width:220px"><?php esc_html_e( 'Plugin', 'orp-print' ); ?></th><td><?php echo esc_html( ORP_VERSION ); ?></td></tr>
				<tr><th><?php esc_html_e( 'API REST', 'orp-print' ); ?></th><td><?php echo esc_html( Router::API_NAMESPACE ); ?></td></tr>
				<tr><th><?php esc_html_e( 'Esquema de base de datos', 'orp-print' ); ?></th><td><?php echo esc_html( (string) get_option( 'orp_db_version' ) ); ?></td></tr>
				<tr><th><?php esc_html_e( 'Versión de configuración (config_version)', 'orp-print' ); ?></th><td><?php echo esc_html( (string) get_option( 'orp_config_version', '1' ) ); ?></td></tr>
				<tr><th><?php esc_html_e( 'Versión de plantilla (orp_template_version)', 'orp-print' ); ?></th><td><?php echo esc_html( (string) get_option( 'orp_template_version', '1' ) ); ?></td></tr>
				<tr><th><?php esc_html_e( 'Preset activo', 'orp-print' ); ?></th><td><?php echo esc_html( (string) $settings['preset'] ); ?></td></tr>
				<tr><th><?php esc_html_e( 'Papel / codificación', 'orp-print' ); ?></th><td>
					<?php
					printf(
						/* translators: 1: ancho de papel en mm, 2: codificación. */
						esc_html__( '%1$s mm · %2$s', 'orp-print' ),
						esc_html( (string) $settings['paper_width_mm'] ),
						esc_html( (string) $settings['encoding'] )
					);
					?>
				</td></tr>
				<tr><th><?php esc_html_e( 'Disparo', 'orp-print' ); ?></th><td><?php echo esc_html( implode( ', ', array_map( 'strval', (array) $settings['trigger_states'] ) ) ); ?> · dedupe_scope: <?php echo esc_html( (string) $settings['dedupe_scope'] ); ?></td></tr>
				<tr><th><?php esc_html_e( 'Caducidad / retención de trabajos', 'orp-print' ); ?></th><td>
					<?php
					printf(
						/* translators: 1: minutos de caducidad, 2: días de retención. */
						esc_html__( '%1$d min · %2$d días', 'orp-print' ),
						esc_html( (string) $settings['job_expiry_minutes'] ),
						esc_html( (string) $settings['retention_days'] )
					);
					?>
				</td></tr>
			</tbody>
		</table>
		<?php
	}

	/** Locales con su estado y sus reglas resumidas. */
	private static function render_locations(): void {
		$locations = LocationRepository::all();
		?>
		<h2><?php esc_html_e( 'Locales', 'orp-print' ); ?></h2>
		<table class="widefat striped" style="max-width:1100px">
			<thead>
				<tr>
					<th><?php esc_html_e( 'Local', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Código', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Prioridad', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Estado', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Reglas', 'orp-print' ); ?></th>
				</tr>
			</thead>
			<tbody>
				<?php if ( array() === $locations ) : ?>
					<tr><td colspan="5"><?php esc_html_e( 'Sin locales todavía.', 'orp-print' ); ?></td></tr>
				<?php else : ?>
					<?php foreach ( $locations as $location ) : ?>
						<tr>
							<td>
								<?php echo esc_html( (string) $location['name'] ); ?>
								<?php if ( 1 === (int) $location['is_default'] ) : ?>
									<span class="badge" style="background:#f0f0f1;padding:2px 6px;border-radius:3px"><?php esc_html_e( 'Por defecto', 'orp-print' ); ?></span>
								<?php endif; ?>
							</td>
							<td><code><?php echo esc_html( (string) $location['code'] ); ?></code></td>
							<td><?php echo esc_html( (string) $location['priority'] ); ?></td>
							<td><?php echo esc_html( 1 === (int) $location['enabled'] ? __( 'Activo', 'orp-print' ) : __( 'Inactivo', 'orp-print' ) ); ?></td>
							<td><?php echo esc_html( LocationsPage::rules_summary( (string) $location['rules'] ) ); ?></td>
						</tr>
					<?php endforeach; ?>
				<?php endif; ?>
			</tbody>
		</table>
		<?php
	}

	/** Agentes con último latido, versión y salidas (Doc/03 §11). */
	private static function render_agents(): void {
		global $wpdb;

		$agents_table = $wpdb->prefix . 'orp_agents';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$agents = (array) $wpdb->get_results(
			"SELECT a.id, a.name, a.status, a.agent_version, a.platform, a.last_seen_at, a.location_id,
			        l.name AS location_name, l.code AS location_code
			   FROM {$agents_table} a
			   LEFT JOIN {$wpdb->prefix}orp_locations l ON l.id = a.location_id
			  ORDER BY a.id ASC"
		);

		$destinations = DestinationRepository::all();
		?>
		<h2><?php esc_html_e( 'Agentes', 'orp-print' ); ?></h2>
		<table class="widefat striped" style="max-width:1100px">
			<thead>
				<tr>
					<th><?php esc_html_e( 'Nombre', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Local', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Conexión', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Versión', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Plataforma', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Salidas', 'orp-print' ); ?></th>
				</tr>
			</thead>
			<tbody>
				<?php if ( array() === $agents ) : ?>
					<tr><td colspan="6"><?php esc_html_e( 'Sin agentes emparejados. Genera un código en «Locales y reglas» y ejecuta «orvitaprint pair» en el TPV.', 'orp-print' ); ?></td></tr>
				<?php else : ?>
					<?php foreach ( $agents as $agent ) : ?>
						<?php
						$loc       = (int) $agent->location_id;
						$seen      = null !== $agent->last_seen_at ? (int) strtotime( (string) $agent->last_seen_at . ' UTC' ) : null;
						$connected = null !== $seen && ( time() - $seen ) < Reaper::AGENT_OFFLINE_SECONDS;
						?>
						<tr>
							<td><?php echo esc_html( (string) $agent->name ); ?></td>
							<td>
								<?php
								echo esc_html(
									'' !== (string) ( $agent->location_name ?? '' )
										? (string) $agent->location_name
										: sprintf( /* translators: %d: id de local. */ __( 'Local #%d (borrado)', 'orp-print' ), $loc )
								);
								?>
							</td>
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
							<td><?php echo esc_html( (string) ( $agent->platform ?? '' ) ); ?></td>
							<td>
								<?php
								$agent_dest = array_values(
									array_filter(
										$destinations,
										static fn( $destination ) => (int) $destination->location_id === $loc
									)
								);
								?>
								<?php if ( array() === $agent_dest ) : ?>
									<span class="description"><?php esc_html_e( 'ninguna todavía', 'orp-print' ); ?></span>
								<?php else : ?>
									<?php foreach ( $agent_dest as $destination ) : ?>
										<div>
											<code><?php echo esc_html( (string) $destination->role ); ?></code>
											<?php
											echo esc_html(
												sprintf(
													/* translators: 1: impresora, 2: copias. */
													__( '%1$s ×%2$d', 'orp-print' ),
													(string) $destination->printer_name,
													(int) $destination->copies
												)
											);
											?>
											<?php if ( 1 !== (int) $destination->enabled ) : ?>
												<span class="description">— <?php esc_html_e( 'desactivada', 'orp-print' ); ?></span>
											<?php endif; ?>
										</div>
									<?php endforeach; ?>
								<?php endif; ?>
							</td>
						</tr>
					<?php endforeach; ?>
				<?php endif; ?>
			</tbody>
		</table>
		<?php
	}

	/** Resumen de la cola por estado (los seis estados, incluidos los vacíos). */
	private static function render_queue(): void {
		global $wpdb;

		$jobs_table = $wpdb->prefix . 'orp_jobs';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$rows = (array) $wpdb->get_results(
			"SELECT status, COUNT(*) AS total FROM {$jobs_table} GROUP BY status",
			ARRAY_A
		);

		$counts = array();
		foreach ( $rows as $row ) {
			$counts[ (string) $row['status'] ] = (int) $row['total'];
		}

		$total = array_sum( $counts );
		?>
		<h2><?php esc_html_e( 'Cola de impresión', 'orp-print' ); ?></h2>
		<?php if ( 0 === $total ) : ?>
			<p class="description"><?php esc_html_e( 'Sin trabajos todavía. Los pedidos que no tengan local asignable se registran en el log sin crear trabajo.', 'orp-print' ); ?></p>
		<?php endif; ?>
		<table class="striped" style="max-width:760px">
			<thead>
				<tr>
					<?php foreach ( self::STATUSES as $status ) : ?>
						<th><?php echo esc_html( $status ); ?></th>
					<?php endforeach; ?>
					<th><?php esc_html_e( 'Total', 'orp-print' ); ?></th>
				</tr>
			</thead>
			<tbody>
				<tr>
					<?php foreach ( self::STATUSES as $status ) : ?>
						<td><?php echo esc_html( (string) ( $counts[ $status ] ?? 0 ) ); ?></td>
					<?php endforeach; ?>
					<td><strong><?php echo esc_html( (string) $total ); ?></strong></td>
				</tr>
			</tbody>
		</table>
		<p>
			<a href="<?php echo esc_url( admin_url( 'admin.php?page=' . Menu::SLUG_JOBS ) ); ?>"><?php esc_html_e( 'Abrir la cola para actuar sobre los trabajos', 'orp-print' ); ?></a>
		</p>
		<?php
	}

	/** Últimos eventos de orp_logs: nivel, contexto y mensaje (sin datos personales). */
	private static function render_recent_events(): void {
		global $wpdb;

		$logs_table = $wpdb->prefix . 'orp_logs';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$events = (array) $wpdb->get_results(
			$wpdb->prepare(
				"SELECT created_at, level, context, message, job_id, agent_id
				   FROM {$logs_table}
				  ORDER BY id DESC
				  LIMIT %d",
				self::RECENT_EVENTS
			)
		);

		$levels = array(
			'info'    => '#00a32a',
			'warning' => '#dba617',
			'error'   => '#d63638',
			'debug'   => '#8c8f94',
		);
		?>
		<h2>
			<?php
			printf(
				/* translators: %d: número de eventos mostrados. */
				esc_html__( 'Últimos %d eventos', 'orp-print' ),
				self::RECENT_EVENTS
			);
			?>
		</h2>
		<table class="widefat striped" style="max-width:1100px">
			<thead>
				<tr>
					<th><?php esc_html_e( 'Fecha (local)', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Nivel', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Contexto', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Evento', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Trabajo / agente', 'orp-print' ); ?></th>
				</tr>
			</thead>
			<tbody>
				<?php if ( array() === $events ) : ?>
					<tr><td colspan="5"><?php esc_html_e( 'Sin eventos registrados todavía (los registros se conservan 30 días).', 'orp-print' ); ?></td></tr>
				<?php else : ?>
					<?php foreach ( $events as $event ) : ?>
						<?php
						$level = (string) $event->level;
						$color = $levels[ $level ] ?? '#8c8f94';
						$refs  = array();
						if ( (int) $event->job_id > 0 ) {
							$refs[] = sprintf( /* translators: %d: id de trabajo. */ __( 'trabajo #%d', 'orp-print' ), (int) $event->job_id );
						}
						if ( (int) $event->agent_id > 0 ) {
							$refs[] = sprintf( /* translators: %d: id de agente. */ __( 'agente #%d', 'orp-print' ), (int) $event->agent_id );
						}
						?>
						<tr>
							<td><?php echo esc_html( wp_date( 'd/m/Y H:i:s', (int) strtotime( (string) $event->created_at . ' UTC' ) ) ); ?></td>
							<td><span style="color:<?php echo esc_attr( $color ); ?>;font-weight:600"><?php echo esc_html( $level ); ?></span></td>
							<td><code><?php echo esc_html( (string) $event->context ); ?></code></td>
							<td><?php echo esc_html( (string) $event->message ); ?></td>
							<td><?php echo esc_html( array() !== $refs ? implode( ' · ', $refs ) : '—' ); ?></td>
						</tr>
					<?php endforeach; ?>
				<?php endif; ?>
			</tbody>
		</table>
		<?php
	}
}
