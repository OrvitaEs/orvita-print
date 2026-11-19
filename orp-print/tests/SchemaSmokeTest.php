<?php
/**
 * Prueba de humo del esquema (F0-02): las 6 tablas existen, las opciones por
 * defecto están creadas y reactivar no duplica nada (Doc/06 §10).
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Tests;

use Orvita\PrintBridge\Install\Schema;
use Orvita\PrintBridge\Support\Settings;

final class SchemaSmokeTest extends \WP_UnitTestCase {

	/** Sufijos de las 6 tablas del modelo de datos (Doc/04 §2). */
	private const TABLE_SUFFIXES = array( 'locations', 'agents', 'pairing_codes', 'jobs', 'destinations', 'logs' );

	/** Las 6 tablas se crean. */
	public function test_tables_created(): void {
		global $wpdb;

		foreach ( self::TABLE_SUFFIXES as $suffix ) {
			$table = $wpdb->prefix . 'orp_' . $suffix;
			$found = $wpdb->get_var(
				$wpdb->prepare( 'SHOW TABLES LIKE %s', $table )
			);
			$this->assertSame( $table, $found, "Falta la tabla {$table}." );
		}
	}

	/** Opciones por defecto creadas en la activación. */
	public function test_default_options_created(): void {
		$this->assertSame( ORP_DB_VERSION, (string) get_option( 'orp_db_version' ) );
		$this->assertGreaterThanOrEqual( 1, (int) get_option( 'orp_template_version' ) );
		$this->assertGreaterThanOrEqual( 1, (int) get_option( 'orp_config_version' ) );

		$settings = get_option( Settings::OPTION );
		$this->assertIsArray( $settings );
		$this->assertSame( 'order', $settings['dedupe_scope'] ?? null );
		$this->assertSame( 80, $settings['paper_width_mm'] ?? null );
		$this->assertFalse( $settings['uninstall_purge'] ?? true );
	}

	/** Reinstalar (desactivar y reactivar) no duplica tablas ni opciones. */
	public function test_reinstall_does_not_duplicate(): void {
		global $wpdb;

		Schema::install();
		Schema::install();

		// phpcs:ignore WordPress.DB.PreparedSQL.NotPrepared
		$orp_tables = (array) $wpdb->get_col(
			$wpdb->prepare( 'SHOW TABLES LIKE %s', $wpdb->esc_like( $wpdb->prefix . 'orp_' ) . '%' )
		);

		$this->assertCount( count( self::TABLE_SUFFIXES ), $orp_tables, 'El esquema no debe duplicar tablas al reactivar.' );

		// La versión de plantilla no se reinicia con cada install().
		update_option( 'orp_template_version', 7 );
		Schema::install();
		$this->assertSame( 7, (int) get_option( 'orp_template_version' ) );
	}

	/** La capacidad propia se concedió al rol administrator en la activación. */
	public function test_administrator_has_capability(): void {
		$role = get_role( 'administrator' );
		$this->assertInstanceOf( \WP_Role::class, $role );
		$this->assertTrue( (bool) $role->has_cap( ORP_CAPABILITY ), 'administrator debe tener manage_orp_print.' );
	}
}
