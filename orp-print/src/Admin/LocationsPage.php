<?php
/**
 * Locales y reglas (F1-02): CRUD de orp_locations desde el panel.
 *
 * Cada local tiene: código canónico, nombre, activado, prioridad (menor
 * número = más prioridad), reglas JSON (Doc/04 §2.1) y marca de local por
 * defecto (solo uno). El JSON se valida: si es inválido, no se guarda y se
 * muestra el error.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Admin;

use Orvita\PrintBridge\Domain\LocationRepository;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class LocationsPage {

	/** Procesa las acciones antes de renderizar. */
	public static function register(): void {
		add_action( 'admin_init', array( self::class, 'maybe_save' ) );
	}

	/** Guardar (crear/editar), borrar, activar/desactivar y marcar por defecto. */
	public static function maybe_save(): void {
		if ( ! is_admin() || ( isset( $_GET['page'] ) && Menu::SLUG_LOCATIONS !== $_GET['page'] ) ) {
			return;
		}

		if ( ! current_user_can( ORP_CAPABILITY ) ) {
			return;
		}

		$screen_action = isset( $_REQUEST['orp_action'] ) ? sanitize_key( (string) wp_unslash( $_REQUEST['orp_action'] ) ) : '';

		switch ( $screen_action ) {
			case 'save':
				check_admin_referer( 'orp_save_location', 'orp_location_nonce' );
				self::save( (array) wp_unslash( $_POST['orp'] ) );
				break;

			case 'delete':
				$id = isset( $_GET['id'] ) ? absint( $_GET['id'] ) : 0;
				check_admin_referer( 'orp_delete_location_' . $id );
				self::delete( $id );
				break;

			case 'toggle':
				$id = isset( $_GET['id'] ) ? absint( $_GET['id'] ) : 0;
				check_admin_referer( 'orp_toggle_location_' . $id );
				self::toggle( $id );
				break;

			case 'set_default':
				$id = isset( $_GET['id'] ) ? absint( $_GET['id'] ) : 0;
				check_admin_referer( 'orp_default_location_' . $id );
				self::set_default( $id );
				break;
		}
	}

	/** Crea o actualiza el local enviado por POST. */
	private static function save( array $input ): void {
		$result = LocationRepository::save(
			array(
				'id'         => isset( $input['id'] ) ? absint( $input['id'] ) : 0,
				'code'       => sanitize_key( (string) ( $input['code'] ?? '' ) ),
				'name'       => sanitize_text_field( (string) ( $input['name'] ?? '' ) ),
				'enabled'    => ! empty( $input['enabled'] ),
				'priority'   => absint( $input['priority'] ?? 100 ),
				'rules_json' => (string) wp_unslash( (string) ( $input['rules_json'] ?? '' ) ),
				'is_default' => ! empty( $input['is_default'] ),
			)
		);

		if ( isset( $result['error'] ) ) {
			add_settings_error( 'orp-locations', 'orp_location_' . $result['error'], self::error_message( $result['error'] ), 'error' );
			return;
		}

		$id = (int) $result['id'];

		// Sincroniza el local por defecto con orp_settings.default_location_id.
		$row   = LocationRepository::get( $id );
		$name  = is_array( $row ) ? (string) $row['name'] : '';
		if ( is_array( $row ) && 1 === (int) $row['is_default'] ) {
			LocationRepository::set_default( $id );
		}

		add_settings_error(
			'orp-locations',
			'orp_location_saved',
			sprintf(
				/* translators: %s: nombre del local guardado. */
				__( 'Local «%s» guardado. Los trabajos ya encolados conservan su local: cambiar reglas no los altera.', 'orp-print' ),
				$name
			),
			'updated'
		);
	}

	/** Borra un local (y sus salidas/agentes huérfanos). */
	private static function delete( int $id ): void {
		$location = LocationRepository::get( $id );

		if ( null === $location || ! LocationRepository::delete( $id ) ) {
			add_settings_error( 'orp-locations', 'orp_location_not_found', __( 'El local no existe o ya fue borrado.', 'orp-print' ), 'error' );
			return;
		}

		add_settings_error(
			'orp-locations',
			'orp_location_deleted',
			sprintf(
				/* translators: %s: nombre del local borrado. */
				__( 'Local «%s» borrado junto con sus salidas y agentes.', 'orp-print' ),
				(string) $location['name']
			),
			'updated'
		);
	}

	/** Activa o desactiva un local. */
	private static function toggle( int $id ): void {
		global $wpdb;

		$location = LocationRepository::get( $id );
		if ( null === $location ) {
			return;
		}

		$new = 1 === (int) $location['enabled'] ? 0 : 1;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché.
		$wpdb->update(
			$wpdb->prefix . 'orp_locations',
			array(
				'enabled' => $new,
				'updated_at' => \Orvita\PrintBridge\Support\Clock::now_sql(),
			),
			array( 'id' => $id ),
			array( '%d', '%s' ),
			array( '%d' )
		);

		add_settings_error(
			'orp-locations',
			'orp_location_toggled',
			$new ? __( 'Local activado.', 'orp-print' ) : __( 'Local desactivado: no recibe pedidos hasta que lo reactives.', 'orp-print' ),
			'updated'
		);
	}

	/** Marca un local como por defecto (solo hay uno). */
	private static function set_default( int $id ): void {
		if ( ! LocationRepository::set_default( $id ) ) {
			return;
		}

		add_settings_error(
			'orp-locations',
			'orp_location_default',
			__( 'Local por defecto actualizado. Es el destino de los pedidos que no coinciden con ninguna regla, con aviso en el panel.', 'orp-print' ),
			'updated'
		);
	}

	/** Texto legible de cada error de validación. */
	private static function error_message( string $code ): string {
		switch ( $code ) {
			case 'missing_fields':
				return __( 'Faltan el código o el nombre del local.', 'orp-print' );
			case 'invalid_code':
				return __( 'El código solo puede llevar minúsculas, números, guiones y guiones bajos.', 'orp-print' );
			case 'duplicate_code':
				return __( 'Ya existe un local con ese código: debe ser único.', 'orp-print' );
			case 'invalid_json':
				return __( 'El JSON de reglas no es válido: revísalo y vuelve a guardar.', 'orp-print' );
			case 'too_many_rules':
				return __( 'Demasiadas reglas en el local (máximo 20).', 'orp-print' );
			case 'invalid_rule':
				return __( 'Hay una regla con tipo, operador o valores no admitidos.', 'orp-print' );
			default:
				return __( 'No se pudo guardar el local.', 'orp-print' );
		}
	}

	/** Resumen de una regla para la tabla: «city en [Alcalá, alcala]». */
	private static function rules_summary( ?string $json ): string {
		$rules = json_decode( (string) $json, true );
		if ( ! is_array( $rules ) || array() === $rules ) {
			return __( 'Sin reglas (solo local por defecto)', 'orp-print' );
		}

		$labels = array(
			'city'             => __( 'ciudad', 'orp-print' ),
			'postcode'         => __( 'código postal', 'orp-print' ),
			'shipping_method'  => __( 'método de envío', 'orp-print' ),
			'product_category' => __( 'categoría', 'orp-print' ),
			'order_meta'       => __( 'meta del pedido', 'orp-print' ),
		);

		$out = array();
		foreach ( $rules as $rule ) {
			$type     = (string) ( $rule['type'] ?? '' );
			$operator = (string) ( $rule['operator'] ?? '' );
			$values   = isset( $rule['values'] ) && is_array( $rule['values'] ) ? implode( ', ', array_map( 'strval', $rule['values'] ) ) : '';
			$out[]    = sprintf( '%s %s [%s]', $labels[ $type ] ?? $type, $operator, $values );
		}

		return implode( ' · ', $out );
	}

	/** Pinta la página: tabla de locales + formulario de alta/edición. */
	public static function render(): void {
		if ( ! current_user_can( ORP_CAPABILITY ) ) {
			wp_die( esc_html__( 'No tienes permisos para ver esta página.', 'orp-print' ) );
		}

		$editing_id = isset( $_GET['edit'] ) ? absint( $_GET['edit'] ) : 0;
		$editing    = $editing_id > 0 ? LocationRepository::get( $editing_id ) : null;
		$locations  = LocationRepository::all();

		$form = array(
			'id'         => is_array( $editing ) ? (int) $editing['id'] : 0,
			'code'       => is_array( $editing ) ? (string) $editing['code'] : '',
			'name'       => is_array( $editing ) ? (string) $editing['name'] : '',
			'enabled'    => is_array( $editing ) ? (bool) $editing['enabled'] : true,
			'priority'   => is_array( $editing ) ? (int) $editing['priority'] : 100,
			'rules_json' => '',
			'is_default' => is_array( $editing ) ? (bool) $editing['is_default'] : false,
		);

		if ( is_array( $editing ) ) {
			$rules = json_decode( (string) $editing['rules'], true );
			$form['rules_json'] = is_array( $rules ) && array() !== $rules
				? (string) wp_json_encode( $rules, JSON_PRETTY_PRINT | JSON_UNESCAPED_UNICODE )
				: '';
		}
		?>
		<div class="wrap">
			<h1><?php echo esc_html( __( 'Locales y reglas', 'orp-print' ) ); ?></h1>
			<?php settings_errors( 'orp-locations' ); ?>

			<p class="description">
				<?php esc_html_e( 'Los locales activos se evalúan por prioridad ascendente y gana el primero cuyas reglas se cumplen; si ninguno coincide, el pedido va al local por defecto con un aviso. Las reglas se leen al crear el trabajo: cambiarlas no altera los trabajos ya encolados.', 'orp-print' ); ?>
			</p>

			<h2><?php echo esc_html( $form['id'] > 0 ? __( 'Editar local', 'orp-print' ) : __( 'Añadir local', 'orp-print' ) ); ?></h2>
			<form method="post" action="">
				<?php wp_nonce_field( 'orp_save_location', 'orp_location_nonce' ); ?>
				<input type="hidden" name="orp_action" value="save" />
				<input type="hidden" name="orp[id]" value="<?php echo esc_attr( (string) $form['id'] ); ?>" />
				<table class="form-table" role="presentation">
					<tr>
						<th scope="row"><label for="orp-code"><?php esc_html_e( 'Código (canónico)', 'orp-print' ); ?></label></th>
						<td>
							<input type="text" id="orp-code" name="orp[code]" required maxlength="32" value="<?php echo esc_attr( $form['code'] ); ?>" class="regular-text" />
							<p class="description"><?php esc_html_e( 'Minúsculas, números, guiones y guiones bajos. Único. Es el código que puede usar una regla order_meta del checkout.', 'orp-print' ); ?></p>
						</td>
					</tr>
					<tr>
						<th scope="row"><label for="orp-name"><?php esc_html_e( 'Nombre', 'orp-print' ); ?></label></th>
						<td><input type="text" id="orp-name" name="orp[name]" required maxlength="120" value="<?php echo esc_attr( $form['name'] ); ?>" class="regular-text" /></td>
					</tr>
					<tr>
						<th scope="row"><label for="orp-priority"><?php esc_html_e( 'Prioridad', 'orp-print' ); ?></label></th>
						<td>
							<input type="number" id="orp-priority" name="orp[priority]" min="0" max="65535" value="<?php echo esc_attr( (string) $form['priority'] ); ?>" class="small-text" />
							<p class="description"><?php esc_html_e( 'Menor número = se evalúa antes.', 'orp-print' ); ?></p>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Estado', 'orp-print' ); ?></th>
						<td>
							<label>
								<input type="checkbox" name="orp[enabled]" value="1" <?php checked( $form['enabled'] ); ?> />
								<?php esc_html_e( 'Local activo', 'orp-print' ); ?>
							</label>
						</td>
					</tr>
					<tr>
						<th scope="row"><?php esc_html_e( 'Local por defecto', 'orp-print' ); ?></th>
						<td>
							<label>
								<input type="checkbox" name="orp[is_default]" value="1" <?php checked( $form['is_default'] ); ?> />
								<?php esc_html_e( 'Marcar como local por defecto (solo puede haber uno)', 'orp-print' ); ?>
							</label>
						</td>
					</tr>
					<tr>
						<th scope="row"><label for="orp-rules-json"><?php esc_html_e( 'Reglas (JSON)', 'orp-print' ); ?></label></th>
						<td>
							<textarea id="orp-rules-json" name="orp[rules_json]" rows="9" class="large-text code" spellcheck="false"><?php echo esc_textarea( $form['rules_json'] ); ?></textarea>
							<p class="description">
								<?php esc_html_e( 'Array JSON. Tipos: city, postcode, shipping_method, product_category, order_meta. Operadores: in, equals, contains, prefix, exists. Todas las reglas del local deben cumplirse. En order_meta, values[0] es la clave del metadato. Ejemplo:', 'orp-print' ); ?>
							</p>
							<code style="display:block;white-space:pre-wrap;margin-top:8px">[
  { "type": "city", "operator": "in", "values": ["Alcalá", "alcala"] },
  { "type": "postcode", "operator": "prefix", "values": ["28"] }
]</code>
						</td>
					</tr>
				</table>
				<?php submit_button( $form['id'] > 0 ? __( 'Guardar local', 'orp-print' ) : __( 'Añadir local', 'orp-print' ) ); ?>
				<?php if ( $form['id'] > 0 ) : ?>
					<p><a href="<?php echo esc_url( admin_url( 'admin.php?page=' . Menu::SLUG_LOCATIONS ) ); ?>"><?php esc_html_e( 'Cancelar edición y crear uno nuevo', 'orp-print' ); ?></a></p>
				<?php endif; ?>
			</form>

			<hr />

			<h2><?php esc_html_e( 'Locales existentes', 'orp-print' ); ?></h2>
			<table class="widefat striped" style="max-width:1100px">
				<thead>
					<tr>
						<th><?php esc_html_e( 'Nombre', 'orp-print' ); ?></th>
						<th><?php esc_html_e( 'Código', 'orp-print' ); ?></th>
						<th><?php esc_html_e( 'Prioridad', 'orp-print' ); ?></th>
						<th><?php esc_html_e( 'Reglas', 'orp-print' ); ?></th>
						<th><?php esc_html_e( 'Estado', 'orp-print' ); ?></th>
						<th><?php esc_html_e( 'Acciones', 'orp-print' ); ?></th>
					</tr>
				</thead>
				<tbody>
					<?php if ( array() === $locations ) : ?>
						<tr><td colspan="6"><?php esc_html_e( 'Sin locales todavía. Crea el primero arriba.', 'orp-print' ); ?></td></tr>
					<?php else : ?>
						<?php foreach ( $locations as $location ) : ?>
							<?php
							$id      = (int) $location['id'];
							$toggle  = 1 === (int) $location['enabled'] ? __( 'Desactivar', 'orp-print' ) : __( 'Activar', 'orp-print' );
							?>
							<tr>
								<td>
									<?php echo esc_html( (string) $location['name'] ); ?>
									<?php if ( 1 === (int) $location['is_default'] ) : ?>
										<span class="badge" style="background:#f0f0f1;padding:2px 6px;border-radius:3px"><?php esc_html_e( 'Por defecto', 'orp-print' ); ?></span>
									<?php endif; ?>
								</td>
								<td><code><?php echo esc_html( (string) $location['code'] ); ?></code></td>
								<td><?php echo esc_html( (string) $location['priority'] ); ?></td>
								<td><?php echo esc_html( self::rules_summary( (string) $location['rules'] ) ); ?></td>
								<td><?php echo esc_html( 1 === (int) $location['enabled'] ? __( 'Activo', 'orp-print' ) : __( 'Inactivo', 'orp-print' ) ); ?></td>
								<td>
									<a href="<?php echo esc_url( admin_url( 'admin.php?page=' . Menu::SLUG_LOCATIONS . '&edit=' . $id ) ); ?>"><?php esc_html_e( 'Editar', 'orp-print' ); ?></a> |
									<a href="<?php echo esc_url( wp_nonce_url( admin_url( 'admin.php?page=' . Menu::SLUG_LOCATIONS . '&orp_action=toggle&id=' . $id ), 'orp_toggle_location_' . $id ) ); ?>"><?php echo esc_html( $toggle ); ?></a> |
									<a href="<?php echo esc_url( wp_nonce_url( admin_url( 'admin.php?page=' . Menu::SLUG_LOCATIONS . '&orp_action=set_default&id=' . $id ), 'orp_default_location_' . $id ) ); ?>"><?php esc_html_e( 'Por defecto', 'orp-print' ); ?></a> |
									<a href="<?php echo esc_url( wp_nonce_url( admin_url( 'admin.php?page=' . Menu::SLUG_LOCATIONS . '&orp_action=delete&id=' . $id ), 'orp_delete_location_' . $id ) ); ?>"
										onclick="return confirm('<?php echo esc_js( __( '¿Borrar este local junto con sus salidas y agentes?', 'orp-print' ) ); ?>');"><?php esc_html_e( 'Borrar', 'orp-print' ); ?></a>
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
