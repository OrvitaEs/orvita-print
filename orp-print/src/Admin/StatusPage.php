<?php
/**
 * Página de estado (solo lectura, diagnóstico). La página completa de
 * diagnóstico (HPOS, errores) llega en F1-10; esta muestra lo esencial:
 * versiones, trabajos por estado y agentes vistos.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Admin;

use Orvita\PrintBridge\Support\Settings;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class StatusPage {

	/** Pinta la página. Solo lectura, sin formularios. */
	public static function render(): void {
		if ( ! current_user_can( ORP_CAPABILITY ) ) {
			wp_die( esc_html__( 'No tienes permisos para ver esta página.', 'orp-print' ) );
		}

		global $wpdb;

		$jobs_table   = $wpdb->prefix . 'orp_jobs';
		$agents_table = $wpdb->prefix . 'orp_agents';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$statuses = (array) $wpdb->get_results(
			"SELECT status, COUNT(*) AS total FROM {$jobs_table} GROUP BY status",
			ARRAY_A
		);

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$agents = (array) $wpdb->get_results(
			"SELECT name, status, agent_version, last_seen_at FROM {$agents_table} ORDER BY id ASC",
			ARRAY_A
		);

		$settings = Settings::all();
		?>
		<div class="wrap">
			<h1><?php echo esc_html( __( 'Estado de Orvita Print', 'orp-print' ) ); ?></h1>
			<p><?php esc_html_e( 'Diagnóstico básico de la cola y los agentes. La página completa de diagnóstico llega en F1-10.', 'orp-print' ); ?></p>

			<h2><?php esc_html_e( 'Versiones', 'orp-print' ); ?></h2>
			<p>
				<?php
				printf(
					/* translators: 1: versión del plugin, 2: versión del esquema, 3: versión de configuración, 4: versión de plantilla */
					esc_html__( 'Plugin %1$s · Esquema %2$s · Config %3$s · Plantilla %4$s', 'orp-print' ),
					esc_html( ORP_VERSION ),
					esc_html( (string) get_option( 'orp_db_version' ) ),
					esc_html( (string) get_option( 'orp_config_version', '1' ) ),
					esc_html( (string) get_option( 'orp_template_version', '1' ) )
				);
				?>
			</p>
			<p>
				<?php
				printf(
					/* translators: 1: ancho de papel en mm, 2: codificación, 3: minutos de caducidad */
					esc_html__( 'Papel %1$s mm · Codificación %2$s · Caducidad de trabajo %3$s min', 'orp-print' ),
					esc_html( (string) $settings['paper_width_mm'] ),
					esc_html( (string) $settings['encoding'] ),
					esc_html( (string) $settings['job_expiry_minutes'] )
				);
				?>
			</p>

			<h2><?php esc_html_e( 'Trabajos por estado', 'orp-print' ); ?></h2>
			<table class="widefat striped" style="max-width:480px">
				<thead>
					<tr>
						<th><?php esc_html_e( 'Estado', 'orp-print' ); ?></th>
						<th><?php esc_html_e( 'Total', 'orp-print' ); ?></th>
					</tr>
				</thead>
				<tbody>
					<?php if ( ! $statuses ) : ?>
						<tr><td colspan="2"><?php esc_html_e( 'Sin trabajos todavía. Usa «wp orp seed» para crear uno de prueba.', 'orp-print' ); ?></td></tr>
					<?php else : ?>
						<?php foreach ( $statuses as $row ) : ?>
							<tr>
								<td><?php echo esc_html( (string) $row['status'] ); ?></td>
								<td><?php echo esc_html( (string) $row['total'] ); ?></td>
							</tr>
						<?php endforeach; ?>
					<?php endif; ?>
				</tbody>
			</table>

			<h2><?php esc_html_e( 'Agentes', 'orp-print' ); ?></h2>
			<table class="widefat striped" style="max-width:640px">
				<thead>
					<tr>
						<th><?php esc_html_e( 'Nombre', 'orp-print' ); ?></th>
						<th><?php esc_html_e( 'Estado', 'orp-print' ); ?></th>
						<th><?php esc_html_e( 'Versión', 'orp-print' ); ?></th>
						<th><?php esc_html_e( 'Último aviso', 'orp-print' ); ?></th>
					</tr>
				</thead>
				<tbody>
					<?php if ( ! $agents ) : ?>
						<tr><td colspan="4"><?php esc_html_e( 'Sin agentes emparejados.', 'orp-print' ); ?></td></tr>
					<?php else : ?>
						<?php foreach ( $agents as $agent ) : ?>
							<tr>
								<td><?php echo esc_html( (string) $agent['name'] ); ?></td>
								<td><?php echo esc_html( (string) $agent['status'] ); ?></td>
								<td><?php echo esc_html( (string) ( $agent['agent_version'] ?? '' ) ); ?></td>
								<td>
									<?php
									$seen = '' !== (string) $agent['last_seen_at']
										? wp_date( 'd/m/Y H:i', (int) strtotime( (string) $agent['last_seen_at'] . ' UTC' ) )
										: __( 'nunca', 'orp-print' );
									echo esc_html( $seen );
									?>
								</td>
							</tr>
						<?php endforeach; ?>
					<?php endif; ?>
				</tbody>
			</table>
		</div>
		<?php
	}
}
