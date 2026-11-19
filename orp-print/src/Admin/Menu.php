<?php
/**
 * Menú de administración. El panel completo (presets, locales, plantilla)
 * llega en F1-01; en F0 solo hay una página de estado.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Admin;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class Menu {

	/** Registra el hook de menú. */
	public static function register(): void {
		add_action( 'admin_menu', array( self::class, 'add_menu' ) );
	}

	/** Añade la página del plugin. */
	public static function add_menu(): void {
		add_menu_page(
			__( 'Orvita Print', 'orp-print' ),
			__( 'Orvita Print', 'orp-print' ),
			ORP_CAPABILITY,
			'orp-print',
			array( SettingsPage::class, 'render' ),
			'dashicons-printer'
		);
	}
}
