<?php
/**
 * Arranque de las pruebas PHPUnit con la suite de WordPress.
 *
 * Uso (con wp-env, dentro de orp-print/):
 *   WP_TESTS_DIR=$(npx wp-env run tests-cli wp eval 'echo ABSPATH;') vendor/bin/phpunit
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

// Carga el plugin y ejecuta su activación (esquema + capacidad) antes del bootstrap.
tests_add_filter(
	'muplugins_loaded',
	static function (): void {
		require_once dirname( __DIR__ ) . '/orp-print.php';
		\Orvita\PrintBridge\Install\Activator::activate();
	}
);

require $_tests_dir . '/includes/bootstrap.php';
