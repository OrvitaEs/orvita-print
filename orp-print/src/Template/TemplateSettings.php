<?php
/**
 * TemplateSettings — selección de campos del ticket (F1-03, Doc/03 §5.4).
 *
 * Cada campo de la plantilla (título del negocio, nº de pedido, cliente,
 * teléfono, dirección, líneas de producto, totales, pie, logo, QR y la nota
 * «No válido como factura») es activable/desactivable; el preset elegido
 * marca los valores por defecto (Completo = todos).
 *
 * La plantilla vive en la opción `orp_template`, separada de `orp_settings`
 * porque Doc/04 §2.7 cierra las claves de esa tabla (frozen) y define la
 * plantilla como «sección propia del panel». Al guardarla sube
 * `orp_template_version`, que entra en la clave de idempotencia: los pedidos
 * ya impresos no se duplican (Doc/04 §3, Doc/03 §5.4).
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Template;

use Orvita\PrintBridge\Support\Logger;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class TemplateSettings {

	/** Nombre de la opción en wp_options. */
	public const OPTION = 'orp_template';

	/**
	 * Campos de la plantilla con su etiqueta visible. El orden es también el
	 * orden de los bloques en el ticket.
	 */
	public const FIELDS = array(
		'logo'         => 'Logo',
		'business_name' => 'Título del negocio',
		'datetime'     => 'Fecha y hora',
		'order_number' => 'Nº de pedido',
		'customer'     => 'Cliente',
		'phone'        => 'Teléfono',
		'address'      => 'Dirección de entrega',
		'items'        => 'Líneas de producto',
		'totals'       => 'Totales',
		'qr'           => 'Código QR',
		'footer'       => 'Pie personalizado',
		'not_invoice'  => 'Nota «No válido como factura»',
	);

	/** Conjunto mínimo del preset «Aviso» (Doc/03 §15: plantilla mínima). */
	public const PRESET_AVISO = array(
		'business_name' => true,
		'datetime'      => true,
		'order_number'  => true,
		'items'         => true,
		'totals'        => true,
	);

	/** Datos de la plantilla: mapa de campos + textos. */
	private static function defaults(): array {
		$all_on = array();
		foreach ( array_keys( self::FIELDS ) as $field ) {
			$all_on[ $field ] = true;
		}

		return array(
			'fields'        => $all_on,
			'footer_text'   => __( 'Gracias por su pedido', 'orp-print' ),
			'logo_media_id' => 0,
		);
	}

	/** Datos completos (guardados + por defecto). */
	public static function get(): array {
		$stored = get_option( self::OPTION, array() );

		if ( ! is_array( $stored ) ) {
			$stored = array();
		}

		return self::sanitize_shape( array_merge( self::defaults(), $stored ) );
	}

	/** Mapa de campos activados (booleano por id de campo). */
	public static function fields(): array {
		return (array) self::get()['fields'];
	}

	/** ¿Está activado un campo? */
	public static function is_enabled( string $field ): bool {
		$fields = self::fields();
		return ! empty( $fields[ $field ] );
	}

	/** Texto del pie personalizado. */
	public static function footer_text(): string {
		return (string) self::get()['footer_text'];
	}

	/** Id de adjunto del logo (0 = sin logo; el bloque image no se emite). */
	public static function logo_media_id(): int {
		return max( 0, (int) self::get()['logo_media_id'] );
	}

	/** Versión vigente de la plantilla (entra en la clave de idempotencia). */
	public static function version(): int {
		return (int) get_option( 'orp_template_version', 1 );
	}

	/**
	 * Guarda la plantilla saneada. Si algo cambia de verdad, incrementa
	 * `orp_template_version` y lo registra en orp_logs: el cambio queda
	 * visible en la página de plantilla (Doc/04 §2.7).
	 *
	 * @param array $input ['fields' => array, 'footer_text' => string, 'logo_media_id' => int].
	 */
	public static function save( array $input ): bool {
		$previous = self::get();

		$clean = array(
			'fields'        => self::sanitize_fields( (array) ( $input['fields'] ?? array() ) ),
			'footer_text'   => self::sanitize_footer_text( (string) ( $input['footer_text'] ?? '' ) ),
			'logo_media_id' => self::sanitize_logo_media_id( (int) ( $input['logo_media_id'] ?? 0 ) ),
		);

		update_option( self::OPTION, $clean, false );

		$changed = $clean !== $previous;

		if ( $changed ) {
			self::bump_version();
		}

		return $changed;
	}

	/**
	 * Aplica el conjunto de campos de un preset (configuración, no código).
	 * Solo toca `fields`; el pie y el logo se conservan.
	 *
	 * @param array<string, bool> $fields Mapa completo de campos del preset.
	 * @param string              $preset Clave del preset que lo aplica (para el registro).
	 */
	public static function apply_field_set( array $fields, string $preset ): void {
		$data          = self::get();
		$clean_fields  = self::sanitize_fields( $fields );

		if ( $clean_fields === $data['fields'] ) {
			return; // Nada cambia: no se sube versión.
		}

		update_option(
			self::OPTION,
			array(
				'fields'        => $clean_fields,
				'footer_text'   => (string) $data['footer_text'],
				'logo_media_id' => (int) $data['logo_media_id'],
			),
			false
		);

		self::bump_version( sprintf( 'preset %s', $preset ) );
	}

	/* ---------------------------------------------------------------------
	 * Internos.
	 * ------------------------------------------------------------------- */

	/** Normaliza la forma de los datos (claves y tipos). */
	private static function sanitize_shape( array $data ): array {
		$defaults = self::defaults();

		return array(
			'fields'        => self::sanitize_fields( (array) ( $data['fields'] ?? $defaults['fields'] ) ),
			'footer_text'   => (string) ( $data['footer_text'] ?? $defaults['footer_text'] ),
			'logo_media_id' => max( 0, (int) ( $data['logo_media_id'] ?? 0 ) ),
		);
	}

	/** Sanea el mapa de campos: solo ids conocidos y booleanos. */
	private static function sanitize_fields( array $fields ): array {
		$clean = array();

		foreach ( array_keys( self::FIELDS ) as $field ) {
			$clean[ $field ] = array_key_exists( $field, $fields ) && (bool) $fields[ $field ];
		}

		return $clean;
	}

	/**
	 * Texto del pie: texto plano multilínea, sin etiquetas. El handler del
	 * panel ya aplicó wp_unslash a la entrada completa.
	 */
	private static function sanitize_footer_text( string $text ): string {
		return trim( (string) sanitize_textarea_field( $text ) );
	}

	/** Logo: adjunto existente e imagen; si no, 0 (el bloque no se emite). */
	private static function sanitize_logo_media_id( int $media_id ): int {
		if ( $media_id <= 0 ) {
			return 0;
		}

		$attachment = get_post( $media_id );

		if ( null === $attachment || 'attachment' !== $attachment->post_type || ! wp_attachment_is_image( $media_id ) ) {
			return 0;
		}

		return $media_id;
	}

	/**
	 * Sube la versión de plantilla y deja registro del cambio (Doc/04 §2.7).
	 * Los trabajos ya encolados no se alteran: la clave de idempotencia de un
	 * pedido impreso ya está fija en orp_jobs.
	 *
	 * @param string $reason Motivo (para el registro).
	 */
	private static function bump_version( string $reason = '' ): void {
		$new_version = self::version() + 1;

		update_option( 'orp_template_version', $new_version, false );

		Logger::log(
			'info',
			'template',
			sprintf( 'Plantilla guardada; orp_template_version = %d.', $new_version ),
			null,
			null,
			array(
				'fields'        => self::fields(),
				'footer_text'   => self::footer_text(),
				'logo_media_id' => self::logo_media_id(),
				'reason'        => $reason,
			)
		);
	}
}
