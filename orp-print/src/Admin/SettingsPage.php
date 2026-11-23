<?php
/**
 * Página de ajustes — tres capas (Doc/03 §15, F1-01):
 *
 * 1. **Producto** (fijo, no editable): qué puede hacer la versión actual.
 * 2. **Preset**: combinación típica de ajustes; son configuración, no
 *    condicionales de código. Al elegir uno se escriben sus valores en la
 *    capa de instancia. El preset de una instalación nueva es «Aviso».
 * 3. **Instancia**: los valores concretos, con las claves exactas de
 *    orp_settings (Doc/04 §2.7).
 *
 * Los cambios que afectan al agente suben orp_config_version. La plantilla
 * (que subiría orp_template_version) es F1-03 y no está aquí todavía.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Admin;

use Orvita\PrintBridge\Domain\LocationRepository;
use Orvita\PrintBridge\Support\Presets;
use Orvita\PrintBridge\Support\Settings;
use Orvita\PrintBridge\Template\TemplateSettings;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class SettingsPage {

	/** Registra el procesado del formulario antes de renderizar nada. */
	public static function register(): void {
		add_action( 'admin_init', array( self::class, 'maybe_save' ) );
	}

	/** Estados de pedido disponibles como disparador (además de «created»). */
	private static function trigger_status_options(): array {
		$options = array();
		foreach ( \wc_get_order_statuses() as $slug_with_prefix => $label ) {
			$slug             = substr( (string) $slug_with_prefix, strlen( 'wc-' ) );
			$options[ $slug ] = $label;
		}

		return $options;
	}

	/** Procesa los dos formularios (preset e instancia) con nonce y capacidad. */
	public static function maybe_save(): void {
		if ( ! is_admin() || ( isset( $_GET['page'] ) && Menu::SLUG_SETTINGS !== $_GET['page'] ) ) {
			return;
		}

		if ( ! current_user_can( ORP_CAPABILITY ) ) {
			return;
		}

		// Formulario del preset.
		if ( isset( $_POST['orp_preset_action'] ) ) {
			check_admin_referer( 'orp_save_preset', 'orp_preset_nonce' );
			self::save_preset( sanitize_key( wp_unslash( $_POST['orp_preset'] ?? '' ) ) );
			return;
		}

		// Formulario de instancia.
		if ( isset( $_POST['orp_settings_action'] ) ) {
			check_admin_referer( 'orp_save_settings', 'orp_settings_nonce' );
			self::save_instance( (array) wp_unslash( $_POST['orp'] ) );
		}
	}

	/** Aplica un preset: escribe su combinación típica en la instancia. */
	private static function save_preset( string $key ): void {
		$preset = Presets::get( $key );

		if ( null === $preset ) {
			add_settings_error( 'orp-settings', 'orp_invalid_preset', __( 'Preset desconocido.', 'orp-print' ), 'error' );
			return;
		}

		$values           = array( 'preset' => $key );
		$values          += $preset['settings']; // El preset sobrescribe sus claves.
		$values['preset'] = $key;               // …pero nunca la clave preset.

		Settings::save( $values );

		// La parte de plantilla del preset (configuración, no código): marca
		// los campos por defecto y sube orp_template_version si cambia (F1-03).
		if ( isset( $preset['template_fields'] ) && is_array( $preset['template_fields'] ) ) {
			TemplateSettings::apply_field_set( $preset['template_fields'], $key );
		}

		add_settings_error(
			'orp-settings',
			'orp_preset_applied',
			sprintf(
				/* translators: %s: nombre del preset aplicado. */
				__( 'Preset «%s» aplicado. Ajusta ahora los valores de instancia si lo necesitas.', 'orp-print' ),
				$preset['label']
			),
			'updated'
		);
	}

	/** Sanea y guarda la capa de instancia. */
	private static function save_instance( array $input ): void {
		$previous = Settings::all();

		$clean = self::sanitize_instance( $input );

		Settings::save( $clean );

		$location_id = (int) $clean['default_location_id'];

		if ( $location_id > 0 && (int) $previous['default_location_id'] !== $location_id ) {
			LocationRepository::make_sole_default( $location_id );
		}

		// «Ninguno» (0): sincronía bidireccional completa (hallazgo del tester
		// 2026-11-23) — limpia también el is_default huérfano que quedara en la
		// tabla; el resolver lo seguía usando aunque la opción fuera 0.
		if ( 0 === $location_id ) {
			LocationRepository::clear_default();
		}

		add_settings_error(
			'orp-settings',
			'orp_settings_saved',
			__( 'Ajustes guardados.', 'orp-print' ),
			'updated'
		);
	}

	/**
	 * Sanea el formulario de instancia sin guardar nada. Público para las
	 * pruebas (F1-13): valores fuera de rango vuelven al límite o al valor
	 * por defecto (poll_interval 0 → 10, paper_width_mm 999 → 80…).
	 *
	 * @param array<string, mixed> $input Entrada cruda del formulario.
	 * @return array<string, mixed> Valores saneados listos para Settings::save().
	 */
	public static function sanitize_instance( array $input ): array {
		$clean = array();

		// Disparadores: «created» + estados de pedido elegidos.
		$states = array( 'created' );
		foreach ( (array) ( $input['trigger_states'] ?? array() ) as $state ) {
			$state = sanitize_key( (string) $state );
			if ( array_key_exists( $state, self::trigger_status_options() ) ) {
				$states[] = $state;
			}
		}
		$clean['trigger_states'] = array_values( array_unique( $states ) );

		// Ámbito de deduplicación.
		$scope = sanitize_key( (string) ( $input['dedupe_scope'] ?? '' ) );
		$clean['dedupe_scope'] = in_array( $scope, array( 'order', 'trigger' ), true ) ? $scope : 'order';

		// Ancho de papel (58/80 mm), codificación y modo de corte: enumeraciones.
		$width = (int) ( $input['paper_width_mm'] ?? 80 );
		$clean['paper_width_mm'] = in_array( $width, array( 58, 80 ), true ) ? $width : 80;

		$encoding = sanitize_text_field( (string) ( $input['encoding'] ?? '' ) );
		$clean['encoding'] = in_array( $encoding, array( 'CP850', 'CP858', 'CP437' ), true ) ? $encoding : 'CP850';

		$cut = sanitize_key( (string) ( $input['cut_mode'] ?? '' ) );
		$clean['cut_mode'] = in_array( $cut, array( 'partial', 'full' ), true ) ? $cut : 'partial';

		// Booleanos (checkboxes).
		$clean['open_drawer'] = ! empty( $input['open_drawer'] );
		$clean['sound_alert'] = ! empty( $input['sound_alert'] );

		// Enteros acotados.
		$clean['poll_interval']      = self::bounded_int( $input['poll_interval'] ?? 10, 1, 600, 10 );
		$clean['job_expiry_minutes'] = self::bounded_int( $input['job_expiry_minutes'] ?? 30, 1, 1440, 30 );
		$clean['retention_days']     = self::bounded_int( $input['retention_days'] ?? 90, 1, 3650, 90 );

		// Local por defecto: id existente o 0; sincroniza is_default en la tabla.
		$location_id = self::bounded_int( $input['default_location_id'] ?? 0, 0, PHP_INT_MAX, 0 );
		if ( $location_id > 0 && null === LocationRepository::get( $location_id ) ) {
			$location_id = 0;
		}
		$clean['default_location_id'] = $location_id;

		return $clean;
	}

	/** Entero acotado con valor de respaldo. */
	private static function bounded_int( mixed $value, int $min, int $max, int $fallback ): int {
		$value = (int) $value;
		if ( $value < $min ) {
			return $fallback;
		}

		return min( $value, $max );
	}

	/** Pinta la página. */
	public static function render(): void {
		if ( ! current_user_can( ORP_CAPABILITY ) ) {
			wp_die( esc_html__( 'No tienes permisos para ver esta página.', 'orp-print' ) );
		}

		$settings     = Settings::all();
		$preset_key   = in_array( (string) $settings['preset'], Presets::KEYS, true ) ? (string) $settings['preset'] : Settings::DEFAULT_PRESET;
		$presets      = Presets::all();
		$locations    = LocationRepository::all();
		$trigger_opts = self::trigger_status_options();
		$config_changed = (int) get_option( 'orp_config_version', 1 );
		?>
		<div class="wrap">
			<h1><?php echo esc_html( __( 'Ajustes de Orvita Print', 'orp-print' ) ); ?></h1>
			<?php settings_errors( 'orp-settings' ); ?>

			<h2 class="title"><?php esc_html_e( 'Capa 1 · Producto', 'orp-print' ); ?></h2>
			<p class="description">
				<?php esc_html_e( 'Capacidades de esta versión (fijas, no editables): un documento por pedido, de 1 a n salidas por local, disparo al crear el pedido o por estado, deduplicación por pedido o por disparo y plantilla por campos seleccionables (pestaña «Plantilla del ticket»).', 'orp-print' ); ?>
			</p>

			<hr />

			<h2 class="title"><?php esc_html_e( 'Capa 2 · Preset', 'orp-print' ); ?></h2>
			<p class="description">
				<?php esc_html_e( 'Un preset es una combinación típica de ajustes, no un modo del código: al aplicarlo se escriben sus valores en la capa de instancia y luego puedes afinarlos.', 'orp-print' ); ?>
			</p>

			<form method="post" action="">
				<?php wp_nonce_field( 'orp_save_preset', 'orp_preset_nonce' ); ?>
				<table class="form-table" role="presentation">
					<tr>
						<th scope="row"><label for="orp_preset"><?php esc_html_e( 'Preset activo', 'orp-print' ); ?></label></th>
						<td>
							<select id="orp_preset" name="orp_preset">
								<?php foreach ( $presets as $key => $preset ) : ?>
									<option value="<?php echo esc_attr( $key ); ?>" <?php selected( $preset_key, $key ); ?>>
										<?php echo esc_html( $preset['label'] ); ?> — <?php echo esc_html( $preset['description'] ); ?>
									</option>
								<?php endforeach; ?>
							</select>
							<p class="description">
								<?php esc_html_e( 'Instalación nueva: «Aviso». La parte de plantilla del preset marca los campos del ticket; las salidas de cada preset se aplican en la fase F1-06.', 'orp-print' ); ?>
							</p>
						</td>
					</tr>
				</table>
				<p>
					<input type="hidden" name="orp_preset_action" value="1" />
					<?php submit_button( __( 'Aplicar preset', 'orp-print' ), 'secondary', 'submit', false ); ?>
				</p>
			</form>

			<hr />

			<h2 class="title"><?php esc_html_e( 'Capa 3 · Instancia', 'orp-print' ); ?></h2>
			<p class="description">
				<?php
				printf(
					/* translators: %d: versión actual de configuración del agente. */
					esc_html__( 'Valores concretos de esta instalación. Versión de configuración visible por los agentes: %d (sube automáticamente al cambiar ajustes que les afectan).', 'orp-print' ),
					(int) $config_changed
				);
				?>
			</p>

			<form method="post" action="">
				<?php wp_nonce_field( 'orp_save_settings', 'orp_settings_nonce' ); ?>

				<h3><?php esc_html_e( 'Disparo y deduplicación', 'orp-print' ); ?></h3>
				<table class="form-table" role="presentation">
					<tr>
						<th scope="row"><?php esc_html_e( 'Disparadores', 'orp-print' ); ?></th>
						<td>
							<fieldset>
								<label style="margin-bottom:6px">
									<input type="checkbox" checked="checked" disabled />
									<?php echo esc_html( __( 'Creación del pedido (siempre activo: el piloto nace en on-hold con COD y el disparador correcto es la creación)', 'orp-print' ) ); ?>
								</label>
								<br />
								<?php foreach ( $trigger_opts as $slug => $label ) : ?>
									<label style="margin-bottom:6px">
										<input
											type="checkbox"
											name="orp[trigger_states][]"
											value="<?php echo esc_attr( $slug ); ?>"
											<?php checked( in_array( $slug, (array) $settings['trigger_states'], true ) ); ?>
										/>
										<?php
										printf(
											/* translators: %s: nombre del estado de pedido. */
											esc_html__( 'Pasar al estado «%s»', 'orp-print' ),
											esc_html( (string) $label )
										);
										?>
									</label>
									<br />
								<?php endforeach; ?>
							</fieldset>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Deduplicación', 'orp-print' ); ?></th>
						<td>
							<label>
								<input type="radio" name="orp[dedupe_scope]" value="order" <?php checked( 'order', $settings['dedupe_scope'] ); ?> />
								<?php esc_html_e( 'Un solo aviso por pedido (dedupe_scope: order)', 'orp-print' ); ?>
							</label>
							<br />
							<label>
								<input type="radio" name="orp[dedupe_scope]" value="trigger" <?php checked( 'trigger', $settings['dedupe_scope'] ); ?> />
								<?php esc_html_e( 'Un trabajo por cada disparo (dedupe_scope: trigger)', 'orp-print' ); ?>
							</label>
						</td>
					</tr>
					<tr>
						<th scope="row"><label for="orp-default-location"><?php esc_html_e( 'Local por defecto', 'orp-print' ); ?></label></th>
						<td>
							<select id="orp-default-location" name="orp[default_location_id]">
								<option value="0"><?php esc_html_e( '— Ninguno (los pedidos sin regla no se encolan; se registran en el log) —', 'orp-print' ); ?></option>
								<?php foreach ( $locations as $location ) : ?>
									<option value="<?php echo esc_attr( (string) $location['id'] ); ?>" <?php selected( (int) $location['id'], (int) $settings['default_location_id'] ); ?>>
										<?php echo esc_html( (string) $location['name'] . ' (' . (string) $location['code'] . ')' ); ?>
									</option>
								<?php endforeach; ?>
							</select>
							<p class="description">
								<?php esc_html_e( 'Red de seguridad: si ninguna regla coincide, el pedido va aquí y se muestra un aviso en el panel. También se marca como «por defecto» en Locales.', 'orp-print' ); ?>
							</p>
						</td>
					</tr>
				</table>

				<h3><?php esc_html_e( 'Documento y agente', 'orp-print' ); ?></h3>
				<table class="form-table" role="presentation">
					<tr>
						<th scope="row"><label for="orp-paper-width"><?php esc_html_e( 'Ancho de papel', 'orp-print' ); ?></label></th>
						<td>
							<select id="orp-paper-width" name="orp[paper_width_mm]">
								<option value="80" <?php selected( 80, (int) $settings['paper_width_mm'] ); ?>><?php esc_html_e( '80 mm', 'orp-print' ); ?></option>
								<option value="58" <?php selected( 58, (int) $settings['paper_width_mm'] ); ?>><?php esc_html_e( '58 mm', 'orp-print' ); ?></option>
							</select>
						</td>
					</tr>
					<tr>
						<th scope="row"><label for="orp-encoding"><?php esc_html_e( 'Codificación', 'orp-print' ); ?></label></th>
						<td>
							<select id="orp-encoding" name="orp[encoding]">
								<option value="CP850" <?php selected( 'CP850', $settings['encoding'] ); ?>>CP850</option>
								<option value="CP858" <?php selected( 'CP858', $settings['encoding'] ); ?>>CP858</option>
								<option value="CP437" <?php selected( 'CP437', $settings['encoding'] ); ?>>CP437</option>
							</select>
						</td>
					</tr>
					<tr>
						<th scope="row"><label for="orp-cut-mode"><?php esc_html_e( 'Modo de corte', 'orp-print' ); ?></label></th>
						<td>
							<select id="orp-cut-mode" name="orp[cut_mode]">
								<option value="partial" <?php selected( 'partial', $settings['cut_mode'] ); ?>><?php esc_html_e( 'Corte parcial', 'orp-print' ); ?></option>
								<option value="full" <?php selected( 'full', $settings['cut_mode'] ); ?>><?php esc_html_e( 'Corte completo', 'orp-print' ); ?></option>
							</select>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Cajón', 'orp-print' ); ?></th>
						<td>
							<label>
								<input type="checkbox" name="orp[open_drawer]" value="1" <?php checked( ! empty( $settings['open_drawer'] ) ); ?> />
								<?php esc_html_e( 'Abrir el cajón al imprimir', 'orp-print' ); ?>
							</label>
						</td>
					</tr>
					<tr>
						<th scope="row"><label for="orp-poll-interval"><?php esc_html_e( 'Intervalo de poll', 'orp-print' ); ?></label></th>
						<td>
							<input type="number" id="orp-poll-interval" name="orp[poll_interval]" min="1" max="600" value="<?php echo esc_attr( (string) $settings['poll_interval'] ); ?>" class="small-text" />
							<?php esc_html_e( 'segundos (10 s ± 20 % de jitter en el agente)', 'orp-print' ); ?>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Aviso sonoro', 'orp-print' ); ?></th>
						<td>
							<label>
								<input type="checkbox" name="orp[sound_alert]" value="1" <?php checked( ! empty( $settings['sound_alert'] ) ); ?> />
								<?php esc_html_e( 'Sonar al haber trabajo pendiente en el agente', 'orp-print' ); ?>
							</label>
						</td>
					</tr>
				</table>

				<h3><?php esc_html_e( 'Cola y retención', 'orp-print' ); ?></h3>
				<table class="form-table" role="presentation">
					<tr>
						<th scope="row"><label for="orp-job-expiry"><?php esc_html_e( 'Caducidad de trabajos', 'orp-print' ); ?></label></th>
						<td>
							<input type="number" id="orp-job-expiry" name="orp[job_expiry_minutes]" min="1" max="1440" value="<?php echo esc_attr( (string) $settings['job_expiry_minutes'] ); ?>" class="small-text" />
							<?php esc_html_e( 'minutos; el agente no reclama un trabajo caducado (410)', 'orp-print' ); ?>
						</td>
					</tr>
					<tr>
						<th scope="row"><label for="orp-retention-days"><?php esc_html_e( 'Retención', 'orp-print' ); ?></label></th>
						<td>
							<input type="number" id="orp-retention-days" name="orp[retention_days]" min="1" max="3650" value="<?php echo esc_attr( (string) $settings['retention_days'] ); ?>" class="small-text" />
							<?php esc_html_e( 'días conservando los trabajos entregados', 'orp-print' ); ?>
						</td>
					</tr>
				</table>

				<p>
					<input type="hidden" name="orp_settings_action" value="1" />
					<?php submit_button( __( 'Guardar ajustes', 'orp-print' ), 'primary', 'submit', false ); ?>
				</p>
			</form>
		</div>
		<?php
	}
}
