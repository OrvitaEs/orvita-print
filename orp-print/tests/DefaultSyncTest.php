<?php
/**
 * Pruebas de la sincronía bidireccional del local por defecto (hallazgos del
 * tester 2026-11-23):
 * - Guardar «Ninguno» (default_location_id = 0) limpia el is_default huérfano
 *   de orp_locations (SettingsPage → LocationRepository::clear_default()).
 * - LocationRepository::save() en UPDATE fuerza el default único y sincroniza
 *   orp_settings.default_location_id (antes solo INSERT lo hacía).
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Tests;

use Orvita\PrintBridge\Domain\LocationRepository;
use Orvita\PrintBridge\Support\Clock;
use Orvita\PrintBridge\Support\Settings;

final class DefaultSyncTest extends \WP_UnitTestCase {

	/** @var int Ids de los locales de pruebas. */
	private int $id_a;
	private int $id_b;

	public function set_up(): void {
		parent::set_up();

		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->query( "UPDATE {$wpdb->prefix}orp_locations SET is_default = 0" );

		$this->id_a = $this->insert_location( 'sync-fix-a', 'Local A' );
		$this->id_b = $this->insert_location( 'sync-fix-b', 'Local B' );
	}

	/** Local de pruebas insertado a pelo (sin marcar por defecto). */
	private function insert_location( string $code, string $name ): int {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->insert(
			$wpdb->prefix . 'orp_locations',
			array(
				'code'       => $code,
				'name'       => $name,
				'enabled'    => 1,
				'priority'   => 100,
				'rules'      => null,
				'is_default' => 0,
				'created_at' => Clock::now_sql(),
				'updated_at' => Clock::now_sql(),
			),
			array( '%s', '%s', '%d', '%d', '%s', '%d', '%s', '%s' )
		);

		return (int) $wpdb->insert_id;
	}

	/** Marca is_default directamente en la tabla (deja la opción aparte). */
	private function force_is_default( int $id ): void {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->update(
			$wpdb->prefix . 'orp_locations',
			array( 'is_default' => 1 ),
			array( 'id' => $id ),
			array( '%d' ),
			array( '%d' )
		);
	}

	/** El is_default de una fila, por id. */
	private function is_default_of( int $id ): int {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		return (int) $wpdb->get_var(
			$wpdb->prepare( 'SELECT is_default FROM ' . $wpdb->prefix . 'orp_locations WHERE id = %d', $id )
		);
	}

	/*
	 ---------------------------------------------------------------------
	 * Fix 1: «Ninguno» limpia el huérfano (sincronía bidireccional completa).
	 * ------------------------------------------------------------------- */

	public function test_clear_default_removes_orphan_flag_and_option(): void {
		// Escenario del hallazgo: la opción vale 0 pero la tabla conserva un
		// is_default = 1 huérfano que el resolver seguía usando.
		$this->force_is_default( $this->id_a );

		$this->assertSame( 1, $this->is_default_of( $this->id_a ) );
		$this->assertSame( 0, (int) Settings::get( 'default_location_id' ) );

		LocationRepository::clear_default();

		$this->assertSame( 0, $this->is_default_of( $this->id_a ), 'El is_default huérfano queda limpio.' );
		$this->assertSame( 0, (int) Settings::get( 'default_location_id' ) );
	}

	public function test_clear_default_also_resets_the_option(): void {
		// Escenario inverso: la opción apunta a un local que ya no es default.
		$this->force_is_default( $this->id_b );
		Settings::save( array( 'default_location_id' => $this->id_b ) );

		$this->assertSame( 1, $this->is_default_of( $this->id_b ) );

		LocationRepository::clear_default();

		$this->assertSame( 0, $this->is_default_of( $this->id_b ) );
		$this->assertSame( 0, (int) Settings::get( 'default_location_id' ), 'La opción vuelve a «Ninguno».' );
	}

	/*
	 ---------------------------------------------------------------------
	 * Fix 2: save() en UPDATE fuerza el default único (misma lógica que INSERT).
	 * ------------------------------------------------------------------- */

	public function test_update_save_forces_sole_default_and_syncs_option(): void {
		// A era el default marcado a pelo; guardar B como default debe
		// desmarcarlo y sincronizar la opción (UPDATE, no INSERT).
		$this->force_is_default( $this->id_a );

		$result = LocationRepository::save(
			array(
				'id'         => $this->id_b,
				'code'       => 'sync-fix-b',
				'name'       => 'Local B',
				'enabled'    => true,
				'priority'   => 100,
				'rules_json' => '',
				'is_default' => true,
			)
		);
		$this->assertSame( array( 'id' => $this->id_b ), $result );

		$this->assertSame( 1, $this->is_default_of( $this->id_b ), 'B es el default tras el UPDATE.' );
		$this->assertSame( 0, $this->is_default_of( $this->id_a ), 'A pierde la marca: solo hay un default.' );
		$this->assertSame( $this->id_b, (int) Settings::get( 'default_location_id' ), 'La opción queda sincronizada con la tabla.' );
	}

	public function test_update_save_unchecking_default_clears_the_option(): void {
		// B era el default; al desmarcarlo en el UPDATE no queda nada marcado
		// y la opción vuelve a «Ninguno» (sincronía bidireccional).
		LocationRepository::save(
			array(
				'id'         => $this->id_b,
				'code'       => 'sync-fix-b',
				'name'       => 'Local B',
				'enabled'    => true,
				'priority'   => 100,
				'rules_json' => '',
				'is_default' => true,
			)
		);
		$this->assertSame( $this->id_b, (int) Settings::get( 'default_location_id' ) );

		LocationRepository::save(
			array(
				'id'         => $this->id_b,
				'code'       => 'sync-fix-b',
				'name'       => 'Local B',
				'enabled'    => true,
				'priority'   => 100,
				'rules_json' => '',
				'is_default' => false,
			)
		);

		$this->assertSame( 0, $this->is_default_of( $this->id_b ) );
		$this->assertSame( 0, (int) Settings::get( 'default_location_id' ), 'Desmarcar el default sincroniza la opción a 0.' );
	}

	public function test_insert_save_with_default_syncs_option(): void {
		// INSERT con is_default también sincroniza la opción (coherencia del
		// repositorio con la página de locales).
		$result = LocationRepository::save(
			array(
				'code'       => 'sync-fix-c',
				'name'       => 'Local C',
				'enabled'    => true,
				'priority'   => 100,
				'rules_json' => '',
				'is_default' => true,
			)
		);

		$this->assertArrayNotHasKey( 'error', $result );
		$this->assertSame( $result['id'], (int) Settings::get( 'default_location_id' ) );
		$this->assertSame( 1, $this->is_default_of( (int) $result['id'] ) );
		$this->assertSame( 0, $this->is_default_of( $this->id_a ), 'El default previo queda desmarcado.' );
	}
}
