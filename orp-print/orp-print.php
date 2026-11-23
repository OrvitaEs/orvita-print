<?php
/**
 * Plugin Name:       Orvita Print
 * Plugin URI:        https://orvitaprint.example
 * Description:       Cola de impresión para locales físicos: convierte pedidos de WooCommerce en documentos neutros que un agente reclama e imprime en el TPV.
 * Version:           1.0.0
 * Requires at least: 6.8
 * Requires PHP:      8.1
 * Author:            Orvita
 * License:           GPL v2 or later
 * License URI:       https://www.gnu.org/licenses/gpl-2.0.html
 * Text Domain:       orp-print
 * Domain Path:       /languages
 */

declare(strict_types=1);

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

define( 'ORP_VERSION', '1.0.0' );
define( 'ORP_DB_VERSION', '1' );
define( 'ORP_FILE', __FILE__ );
define( 'ORP_PATH', plugin_dir_path( __FILE__ ) );
define( 'ORP_CAPABILITY', 'manage_orp_print' );

require_once ORP_PATH . 'src/Plugin.php';

// Autoloader PSR-4 propio sobre src/ (sin vendor/ en producción).
Orvita\PrintBridge\Plugin::register_autoloader();

// Activación: esquema con dbDelta y capacidad manage_orp_print para administrator.
register_activation_hook( __FILE__, array( 'Orvita\PrintBridge\Install\Activator', 'activate' ) );

// Desactivación: retira el cron del Reaper (F1-09).
register_deactivation_hook( __FILE__, array( 'Orvita\PrintBridge\Domain\Reaper', 'deactivate' ) );

// Arranque del plugin cuando todos los plugins están cargados.
add_action( 'plugins_loaded', static function (): void {
	Orvita\PrintBridge\Plugin::boot();
} );

// Compatibilidad HPOS declarada (Doc 03 §5.3).
add_action( 'before_woocommerce_init', static function (): void {
	if ( class_exists( \Automattic\WooCommerce\Utilities\FeaturesUtil::class ) ) {
		\Automattic\WooCommerce\Utilities\FeaturesUtil::declare_compatibility( 'custom_order_tables', __FILE__, true );
	}
} );
