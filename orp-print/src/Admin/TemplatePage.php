<?php
/**
 * Página «Plantilla del ticket» (F1-03, Doc/03 §5.4).
 *
 * Panel de selección de campos: cada campo activable/desactivable; el preset
 * elegido en Ajustes marca los valores por defecto (Completo = todos). Aquí
 * se editan además el pie personalizado y el logo (adjunto de la biblioteca
 * de medios elegido desde el propio panel).
 *
 * Al guardar se sube orp_template_version si algo cambia (Doc/04 §2.7) y el
 * cambio queda registrado (en orp_logs y visible en esta página): los
 * trabajos ya encolados no se alteran y los pedidos ya impresos no se
 * duplican.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Admin;

use Orvita\PrintBridge\Template\TemplateSettings;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class TemplatePage {

	/** Registra el procesado del formulario antes de renderizar nada. */
	public static function register(): void {
		add_action( 'admin_init', array( self::class, 'maybe_save' ) );
	}

	/** Procesa el formulario con nonce y capacidad. */
	public static function maybe_save(): void {
		if ( ! is_admin() || ( isset( $_GET['page'] ) && Menu::SLUG_TEMPLATE !== $_GET['page'] ) ) {
			return;
		}

		if ( ! current_user_can( ORP_CAPABILITY ) ) {
			return;
		}

		if ( ! isset( $_POST['orp_template_action'] ) ) {
			return;
		}

		check_admin_referer( 'orp_save_template', 'orp_template_nonce' );

		$input = (array) wp_unslash( $_POST['orp'] );

		TemplateSettings::save(
			array(
				'fields'        => (array) ( $input['fields'] ?? array() ),
				'footer_text'   => (string) ( $input['footer_text'] ?? '' ),
				'logo_media_id' => (int) ( $input['logo_media_id'] ?? 0 ),
			)
		);

		add_settings_error(
			'orp-template',
			'orp_template_saved',
			__( 'Plantilla guardada. Los pedidos ya impresos no se duplican: la versión de plantilla entra en la clave de idempotencia.', 'orp-print' ),
			'updated'
		);
	}

	/** Pinta la página. */
	public static function render(): void {
		if ( ! current_user_can( ORP_CAPABILITY ) ) {
			wp_die( esc_html__( 'No tienes permisos para ver esta página.', 'orp-print' ) );
		}

		$data    = TemplateSettings::get();
		$fields  = (array) $data['fields'];
		$preview = admin_url( 'admin.php?page=' . Menu::SLUG_PREVIEW );
		?>
		<div class="wrap">
			<h1><?php echo esc_html( __( 'Plantilla del ticket', 'orp-print' ) ); ?></h1>
			<?php settings_errors( 'orp-template' ); ?>

			<p class="description">
				<?php esc_html_e( 'La misma plantilla alimenta las dos salidas: el documento que imprime el agente y la vista previa imprimible del navegador. El preset elegido en Ajustes marca los valores por defecto; aquí se ajustan a mano. El texto del ticket debe validarlo el cliente antes de imprimir en producción.', 'orp-print' ); ?>
			</p>
			<p>
				<a class="button button-secondary" href="<?php echo esc_url( $preview ); ?>" target="_blank">
					<?php esc_html_e( 'Ver vista previa con datos de ejemplo', 'orp-print' ); ?> ↗
				</a>
			</p>

			<form method="post" action="">
				<?php wp_nonce_field( 'orp_save_template', 'orp_template_nonce' ); ?>

				<h2 class="title"><?php esc_html_e( 'Campos del ticket', 'orp-print' ); ?></h2>
				<table class="form-table" role="presentation">
					<?php foreach ( TemplateSettings::FIELDS as $field_id => $label ) : ?>
						<tr>
							<th scope="row"><?php echo esc_html( (string) $label ); ?></th>
							<td>
								<label>
									<input
										type="checkbox"
										name="orp[fields][<?php echo esc_attr( (string) $field_id ); ?>]"
										value="1"
										<?php checked( ! empty( $fields[ $field_id ] ) ); ?>
									/>
									<?php esc_html_e( 'Imprimir este campo', 'orp-print' ); ?>
								</label>
							</td>
						</tr>
					<?php endforeach; ?>
					<tr>
						<th scope="row"><label for="orp-footer-text"><?php esc_html_e( 'Texto del pie', 'orp-print' ); ?></label></th>
						<td>
							<textarea id="orp-footer-text" name="orp[footer_text]" rows="3" class="large-text"><?php echo esc_textarea( (string) $data['footer_text'] ); ?></textarea>
							<p class="description"><?php esc_html_e( 'Se imprime solo si el campo «Pie personalizado» está activado.', 'orp-print' ); ?></p>
						</td>
					</tr>
					<tr>
						<th scope="row"><label for="orp-logo-media"><?php esc_html_e( 'Logo (PNG)', 'orp-print' ); ?></label></th>
						<td>
							<input type="number" min="0" step="1" id="orp-logo-media" name="orp[logo_media_id]" value="<?php echo esc_attr( (string) $data['logo_media_id'] ); ?>" class="small-text" />
							<button type="button" class="button" id="orp-logo-pick"><?php esc_html_e( 'Elegir logo…', 'orp-print' ); ?></button>
							<span id="orp-logo-name" class="description" style="margin-left:8px">
								<?php
								$logo_id = (int) $data['logo_media_id'];
								if ( $logo_id > 0 ) {
									echo esc_html( (string) get_the_title( $logo_id ) );
								}
								?>
							</span>
							<p class="description">
								<?php
								printf(
									/* translators: %d: ancho máximo del logo en píxeles. */
									esc_html__( 'Adjunto de la biblioteca de medios. El plugin genera el PNG (máximo %d px de ancho, fondo blanco) que el agente decodifica; si no hay logo configurado, el bloque no se emite.', 'orp-print' ),
									384
								);
								?>
							</p>
						</td>
					</tr>
				</table>

				<p>
					<input type="hidden" name="orp_template_action" value="1" />
					<?php submit_button( __( 'Guardar plantilla', 'orp-print' ), 'primary', 'submit', false ); ?>
				</p>
			</form>

			<hr />
			<h2 class="title"><?php esc_html_e( 'Registro de cambios', 'orp-print' ); ?></h2>
			<p class="description">
				<?php
				printf(
					/* translators: 1: versión de plantilla vigente, 2: fecha del último cambio. */
					esc_html__( 'Versión de plantilla: %1$d · Último cambio: %2$s. La versión entra en la clave de idempotencia: los pedidos ya impresos no se duplican al cambiar la plantilla.', 'orp-print' ),
					(int) TemplateSettings::version(),
					esc_html( self::last_change_label() )
				);
				?>
			</p>

			<table class="widefat striped" style="max-width:720px">
				<thead>
					<tr>
						<th><?php esc_html_e( 'Fecha', 'orp-print' ); ?></th>
						<th><?php esc_html_e( 'Cambio', 'orp-print' ); ?></th>
					</tr>
				</thead>
				<tbody>
					<?php $changes = self::recent_changes(); ?>
					<?php if ( array() === $changes ) : ?>
						<tr><td colspan="2"><?php esc_html_e( 'Sin cambios registrados todavía.', 'orp-print' ); ?></td></tr>
					<?php else : ?>
						<?php foreach ( $changes as $change ) : ?>
							<tr>
								<td><?php echo esc_html( wp_date( 'd/m/Y H:i', (int) strtotime( (string) $change->created_at . ' UTC' ) ) ); ?></td>
								<td><?php echo esc_html( (string) $change->message ); ?></td>
							</tr>
						<?php endforeach; ?>
					<?php endif; ?>
				</tbody>
			</table>
		</div>
		<?php

		// Selector de logo con la biblioteca de medios (JS vanilla, sin build).
		self::print_media_script();
	}

	/** Último cambio de plantilla con la hora del sitio. */
	private static function last_change_label(): string {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Registro propio del plugin.
		$last = $wpdb->get_var(
			$wpdb->prepare(
				"SELECT created_at FROM {$wpdb->prefix}orp_logs WHERE context = %s ORDER BY id DESC LIMIT 1",
				'template'
			)
		);

		if ( ! $last ) {
			return __( 'nunca', 'orp-print' );
		}

		return wp_date( 'd/m/Y H:i', (int) strtotime( (string) $last . ' UTC' ) );
	}

	/** Últimos 5 cambios de plantilla (Doc/04 §2.6: retención 30 días). */
	private static function recent_changes(): array {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Registro propio del plugin.
		return (array) $wpdb->get_results(
			$wpdb->prepare(
				"SELECT created_at, message FROM {$wpdb->prefix}orp_logs WHERE context = %s ORDER BY id DESC LIMIT 5",
				'template'
			)
		);
	}

	/** Script del selector de logo (wp.media, vanilla). */
	private static function print_media_script(): void {
		wp_enqueue_media();
		?>
		<script>
		(function () {
			var button = document.getElementById('orp-logo-pick');
			var input = document.getElementById('orp-logo-media');
			var label = document.getElementById('orp-logo-name');
			if (!button || !input) {
				return;
			}
			button.addEventListener('click', function (event) {
				event.preventDefault();
				if (!window.wp || !window.wp.media) {
					return;
				}
				var frame = window.wp.media({ title: <?php echo wp_json_encode( __( 'Elegir logo del ticket', 'orp-print' ) ); ?>, multiple: false });
				frame.on('select', function () {
					var attachment = frame.state().get('selection').first().toJSON();
					input.value = attachment.id;
					if (label) {
						label.textContent = attachment.title || attachment.filename || '';
					}
				});
				frame.open();
			});
		})();
		</script>
		<?php
	}
}
