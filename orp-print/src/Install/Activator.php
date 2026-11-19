<?php
/**
 * Activación del plugin: esquema de base de datos y capacidades.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Install;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class Activator {

	/** Se ejecuta en register_activation_hook. */
	public static function activate(): void {
		Schema::install();

		// Capacidad propia del plugin, concedida al rol administrator (Doc 06 §7).
		$role = get_role( 'administrator' );
		if ( $role instanceof \WP_Role && ! $role->has_cap( ORP_CAPABILITY ) ) {
			$role->add_cap( ORP_CAPABILITY, true );
		}
	}
}
