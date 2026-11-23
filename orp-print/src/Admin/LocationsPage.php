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

use Orvita\PrintBridge\Domain\DestinationRepository;
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

			// F1-07: genera un código de emparejamiento para el local.
			case 'pair_code':
				$id = isset( $_GET['id'] ) ? absint( $_GET['id'] ) : 0;
				check_admin_referer( 'orp_pair_code_' . $id );
				self::pair_code( $id );
				break;
		}
	}

	/**
	 * Genera un código de emparejamiento (F1-07): 6 dígitos, 10 minutos,
	 * 5 intentos. Se muestra en el aviso y queda como «código vigente» en la
	 * tabla hasta que se use, caduque o se bloquee; el agente lo teclea con
	 * `orvitaprint pair --server <URL> <código>`.
	 */
	private static function pair_code( int $id ): void {
		$location = LocationRepository::get( $id );

		if ( null === $location ) {
			add_settings_error( 'orp-locations', 'orp_pairing_not_found', __( 'El local no existe: no se generó el código.', 'orp-print' ), 'error' );

			return;
		}

		$created = \Orvita\PrintBridge\Domain\PairingCodes::create( $id, (int) get_current_user_id() );

		if ( null === $created ) {
			add_settings_error( 'orp-locations', 'orp_pairing_failed', __( 'No se pudo generar el código de emparejamiento. Vuelve a intentarlo.', 'orp-print' ), 'error' );

			return;
		}

		$expires = strtotime( $created['expires_at'] . ' UTC' );

		add_settings_error(
			'orp-locations',
			'orp_pairing_created',
			sprintf(
				/* translators: 1: nombre del local, 2: código de 6 dígitos, 3: hora de caducidad local. */
				__( 'Código de emparejamiento para «%1$s»: %2$s (caduca a las %3$s). Solo vale para este local y un solo agente; el agente lo teclea con orvitaprint pair --server <URL> %2$s.', 'orp-print' ),
				(string) $location['name'],
				(string) $created['code'],
				false !== $expires ? wp_date( 'H:i', $expires ) : '—'
			),
			'updated'
		);
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

	/** Pinta la página: tabla de locales + formulario de alta/edición + destinos. */
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
						<th><?php esc_html_e( 'Emparejamiento', 'orp-print' ); ?></th>
						<th><?php esc_html_e( 'Acciones', 'orp-print' ); ?></th>
					</tr>
				</thead>
				<tbody>
					<?php if ( array() === $locations ) : ?>
						<tr><td colspan="7"><?php esc_html_e( 'Sin locales todavía. Crea el primero arriba.', 'orp-print' ); ?></td></tr>
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
									<?php
									$pairing = \Orvita\PrintBridge\Domain\PairingCodes::latest_valid( $id );
									if ( null !== $pairing ) {
										$pairing_expires = strtotime( (string) $pairing->expires_at . ' UTC' );
										echo '<code style="font-size:1.2em">' . esc_html( (string) $pairing->code ) . '</code> ';
										printf(
											/* translators: %s: hora de caducidad local. */
											esc_html__( 'vigente hasta las %s', 'orp-print' ),
											esc_html( false !== $pairing_expires ? wp_date( 'H:i', $pairing_expires ) : '—' )
										);
									} else {
										esc_html_e( 'sin código vigente', 'orp-print' );
									}
									?>
									· <a href="<?php echo esc_url( wp_nonce_url( admin_url( 'admin.php?page=' . Menu::SLUG_LOCATIONS . '&orp_action=pair_code&id=' . $id ), 'orp_pair_code_' . $id ) ); ?>"><?php esc_html_e( 'Generar código', 'orp-print' ); ?></a>
								</td>
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

			<?php self::render_destinations_panel( $locations ); ?>
		</div>
		<?php
	}

	/**
	 * Panel de destinos (F1-06): SOLO LECTURA — las salidas las reporta el
	 * agente en su heartbeat (Doc/04 §2.5); aquí no se editan. Avisos:
	 * - dos salidas activas del mismo local con la misma impresora (Doc/04
	 *   §2.8: para eso están las copias, no dos salidas);
	 * - un local sin ninguna salida activa (no generará trabajos).
	 *
	 * @param array<int, array<string, mixed>> $locations Locales existentes.
	 */
	private static function render_destinations_panel( array $locations ): void {
		$all = DestinationRepository::all();

		$by_location = array();
		foreach ( $all as $destination ) {
			$by_location[ (int) $destination->location_id ][] = $destination;
		}

		$duplicate_warnings = array();
		$empty_warnings     = array();

		foreach ( $locations as $location ) {
			$location_id = (int) $location['id'];
			$destinations = $by_location[ $location_id ] ?? array();

			$enabled = array_values(
				array_filter( $destinations, static fn( $destination ) => 1 === (int) $destination->enabled )
			);

			// Aviso: dos salidas activas que apuntan a la misma impresora.
			$printers = array_map(
				static fn( $destination ) => (string) $destination->printer_name,
				$enabled
			);

			$counts   = array_count_values( $printers );
			$repeated = array_keys( array_filter( $counts, static fn( $count ) => $count > 1 ) );

			foreach ( $repeated as $printer ) {
				$duplicate_warnings[] = array(
					'location' => (string) $location['name'],
					'printer'  => $printer,
				);
			}

			// Aviso: sin salidas activas no se crean trabajos.
			if ( array() === $enabled ) {
				$empty_warnings[] = (string) $location['name'];
			}
		}
		?>
		<hr />

		<h2><?php esc_html_e( 'Salidas (destinos) por local', 'orp-print' ); ?></h2>
		<p class="description">
			<?php esc_html_e( 'Vista de solo lectura: cada salida la reporta el agente del TPV en su heartbeat (impresora, copias y estado). Cada pedido genera un trabajo por salida activa del local, con copias congeladas al crearlo. Para cambiarlas actúa desde el agente.', 'orp-print' ); ?>
		</p>

		<?php if ( array() !== $duplicate_warnings ) : ?>
			<?php foreach ( $duplicate_warnings as $warning ) : ?>
				<div class="notice notice-warning inline">
					<p>
						<?php
						printf(
							/* translators: 1: nombre del local, 2: nombre de la impresora. */
							esc_html__( 'Aviso: dos salidas activas del local «%1$s» apuntan a la misma impresora («%2$s»). Para varias copias usa la salida con su número de copias, no salidas repetidas.', 'orp-print' ),
							esc_html( $warning['location'] ),
							esc_html( $warning['printer'] )
						);
						?>
					</p>
				</div>
			<?php endforeach; ?>
		<?php endif; ?>

		<?php if ( array() !== $empty_warnings ) : ?>
			<div class="notice notice-info inline">
				<p>
					<?php
					printf(
						/* translators: %s: lista de nombres de local. */
						esc_html__( 'Los locales %1$s no tienen ninguna salida activa: no se crearán trabajos para sus pedidos hasta que el agente reporte una salida.', 'orp-print' ),
						esc_html( implode( ', ', $empty_warnings ) )
					);
					?>
				</p>
			</div>
		<?php endif; ?>

		<table class="widefat striped" style="max-width:1100px">
			<thead>
				<tr>
					<th><?php esc_html_e( 'Local', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Rol', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Etiqueta', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Impresora', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Copias', 'orp-print' ); ?></th>
					<th><?php esc_html_e( 'Estado', 'orp-print' ); ?></th>
				</tr>
			</thead>
			<tbody>
				<?php if ( array() === $all ) : ?>
					<tr><td colspan="6"><?php esc_html_e( 'Sin salidas todavía: el agente del TPV las registra al emparejarse y en cada heartbeat.', 'orp-print' ); ?></td></tr>
				<?php else : ?>
					<?php foreach ( $all as $destination ) : ?>
						<tr>
							<td>
								<?php
								$name = '';
								foreach ( $locations as $location ) {
									if ( (int) $location['id'] === (int) $destination->location_id ) {
										$name = (string) $location['name'];
										break;
									}
								}
								echo esc_html( '' !== $name ? $name : sprintf( /* translators: %d: id de local. */ __( 'Local #%d (borrado)', 'orp-print' ), (int) $destination->location_id ) );
								?>
							</td>
							<td><code><?php echo esc_html( (string) $destination->role ); ?></code></td>
							<td><?php echo esc_html( (string) $destination->label ); ?></td>
							<td><?php echo esc_html( (string) $destination->printer_name ); ?></td>
							<td><?php echo esc_html( (string) $destination->copies ); ?></td>
							<td><?php echo esc_html( 1 === (int) $destination->enabled ? __( 'Activa', 'orp-print' ) : __( 'Desactivada', 'orp-print' ) ); ?></td>
						</tr>
					<?php endforeach; ?>
				<?php endif; ?>
			</tbody>
		</table>
		<?php
	}
}
