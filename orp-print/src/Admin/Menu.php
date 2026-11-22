<?php
/**
 * Menú de administración: menú propio de primer nivel (decisión F1-01).
 *
 * No va dentro del menú de WooCommerce: Orvita Print es un servicio con su
 * propia configuración y su propia capacidad (manage_orp_print), y así el
 * panel funciona incluso sin WooCommerce activo (vistas previas, locales).
 *
 * Páginas: Ajustes (3 capas, F1-01), Locales y reglas (F1-02) y Estado
 * (diagnóstico; la página completa de diagnóstico es F1-10).
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Admin;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class Menu {

	/** Slug del menú principal. */
	public const SLUG_SETTINGS  = 'orp-print';
	public const SLUG_LOCATIONS = 'orp-locations';
	public const SLUG_STATUS    = 'orp-status';

	/** Registra el hook de menú. */
	public static function register(): void {
		add_action( 'admin_menu', array( self::class, 'add_menu' ) );
	}

	/** Añade la página principal y sus subpáginas. */
	public static function add_menu(): void {
		add_menu_page(
			__( 'Orvita Print', 'orp-print' ),
			__( 'Orvita Print', 'orp-print' ),
			ORP_CAPABILITY,
			self::SLUG_SETTINGS,
			array( SettingsPage::class, 'render' ),
			'dashicons-printer'
		);

		add_submenu_page(
			self::SLUG_SETTINGS,
			__( 'Ajustes', 'orp-print' ),
			__( 'Ajustes', 'orp-print' ),
			ORP_CAPABILITY,
			self::SLUG_SETTINGS,
			array( SettingsPage::class, 'render' )
		);

		add_submenu_page(
			self::SLUG_SETTINGS,
			__( 'Locales y reglas', 'orp-print' ),
			__( 'Locales y reglas', 'orp-print' ),
			ORP_CAPABILITY,
			self::SLUG_LOCATIONS,
			array( LocationsPage::class, 'render' )
		);

		add_submenu_page(
			self::SLUG_SETTINGS,
			__( 'Estado', 'orp-print' ),
			__( 'Estado', 'orp-print' ),
			ORP_CAPABILITY,
			self::SLUG_STATUS,
			array( StatusPage::class, 'render' )
		);
	}
}
