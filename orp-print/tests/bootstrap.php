<?php
/**
 * Arranque de las pruebas PHPUnit con la suite de WordPress.
 *
 * Uso (con wp-env, dentro de orp-print/):
 *   WP_TESTS_DIR=$(npx @wordpress/env run tests-cli wp eval 'echo ABSPATH;') vendor/bin/phpunit
 * o con una instalación local que tenga la suite de pruebas de WordPress.
 */

declare(strict_types=1);

$_tests_dir = getenv( 'WP_TESTS_DIR' );

if ( ! $_tests_dir ) {
	$_tests_dir = rtrim( sys_get_temp_dir(), '/\\' ) . '/wordpress-tests-lib';
}

if ( ! file_exists( $_tests_dir . '/includes/functions.php' ) ) {
	echo "No se encuentra la suite de pruebas de WordPress en {$_tests_dir}.\n";
	echo "Define WP_TESTS_DIR apuntando a wordpress-tests-lib y vuelve a intentarlo.\n";
	exit( 1 );
}

require_once $_tests_dir . '/includes/functions.php';

// Polyfills de PHPUnit (exigidas por la suite de WP). En desarrollo, vendor/
// vive dentro del plugin (composer install); si está, se carga su autoloader.
$orp_vendor_autoload = dirname( __DIR__ ) . '/vendor/autoload.php';
if ( is_file( $orp_vendor_autoload ) ) {
	require_once $orp_vendor_autoload;
}

// Carga el plugin y ejecuta su activación (esquema + capacidad) antes del bootstrap.
tests_add_filter(
	'muplugins_loaded',
	static function (): void {
		require_once dirname( __DIR__ ) . '/orp-print.php';
		\Orvita\PrintBridge\Install\Activator::activate();

		// WooCommerce (si está instalado) para las pruebas que usan pedidos
		// reales (RuleMatcher/LocationResolver con WC_Order e HPOS). Si no
		// existe, esas pruebas se saltan y el resto sigue en verde. Las
		// tablas se crean con pereza en la primera prueba que las necesita
		// (RuleMatcherTest::woocommerce_or_skip): WC_Install::install()
		// exige WordPress completamente cargado.
		$woo_main_file = WP_PLUGIN_DIR . '/woocommerce/woocommerce.php';
		if ( is_file( $woo_main_file ) ) {
			require_once $woo_main_file;
		}
	}
);

require $_tests_dir . '/includes/bootstrap.php';
