<?php
/**
 * Saneado de la capa de instancia de Ajustes (F1-13): valores fuera de rango
 * del formulario vuelven al límite o al valor por defecto. El método es el
 * MISMO que usa el guardado real (SettingsPage::save_instance), de modo que
 * lo que se prueba aquí es lo que se ejecuta en el panel.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Tests;

use Orvita\PrintBridge\Admin\SettingsPage;
use Orvita\PrintBridge\Support\Settings;

final class SettingsSanitizeTest extends \WP_UnitTestCase {

	public function set_up(): void {
		parent::set_up();
	}

	public function tear_down(): void {
		parent::tear_down();
	}

	/** Entrada cruda completa (los checkboxes ausentes van a false). */
	private function raw_input( array $overrides ): array {
		return array_merge(
			array(
				'trigger_states'      => array(),
				'dedupe_scope'        => 'order',
				'paper_width_mm'      => '80',
				'encoding'            => 'CP850',
				'cut_mode'            => 'partial',
				'poll_interval'       => '10',
				'job_expiry_minutes'  => '30',
				'retention_days'      => '90',
				'default_location_id' => '0',
			),
			$overrides
		);
	}

	/** poll_interval 0 y fuera de rango: 0 → valor por defecto 10; 99999 → límite 600. */
	public function test_poll_interval_out_of_range_is_bounded(): void {
		$clean = SettingsPage::sanitize_instance( $this->raw_input( array( 'poll_interval' => '0' ) ) );
		$this->assertSame( 10, $clean['poll_interval'], '0 está por debajo del mínimo: valor por defecto 10.' );

		$clean = SettingsPage::sanitize_instance( $this->raw_input( array( 'poll_interval' => '99999' ) ) );
		$this->assertSame( 600, $clean['poll_interval'], 'Por encima del máximo se acota al límite 600.' );

		$clean = SettingsPage::sanitize_instance( $this->raw_input( array( 'poll_interval' => '45' ) ) );
		$this->assertSame( 45, $clean['poll_interval'], 'Un valor dentro de rango pasa tal cual.' );
	}

	/** paper_width_mm 999 (o cualquier cosa que no sea 58/80) → 80. */
	public function test_paper_width_invalid_falls_back_to_80(): void {
		$clean = SettingsPage::sanitize_instance( $this->raw_input( array( 'paper_width_mm' => '999' ) ) );
		$this->assertSame( 80, $clean['paper_width_mm'] );

		$clean = SettingsPage::sanitize_instance( $this->raw_input( array( 'paper_width_mm' => '58' ) ) );
		$this->assertSame( 58, $clean['paper_width_mm'], '58 mm es válido y se conserva.' );
	}

	/** Enumeraciones inválidas (encoding, corte, dedupe) → valor por defecto. */
	public function test_enumerations_fall_back_to_defaults(): void {
		$clean = SettingsPage::sanitize_instance(
			$this->raw_input(
				array(
					'encoding'     => 'UTF-9',
					'cut_mode'     => 'guillotina',
					'dedupe_scope' => 'nada',
				)
			)
		);

		$this->assertSame( 'CP850', $clean['encoding'] );
		$this->assertSame( 'partial', $clean['cut_mode'] );
		$this->assertSame( 'order', $clean['dedupe_scope'] );
	}

	/** Caducidad y retención también acotadas; estados inválidos descartados. */
	public function test_other_bounds_and_trigger_states(): void {
		$clean = SettingsPage::sanitize_instance(
			$this->raw_input(
				array(
					'job_expiry_minutes' => '0',
					'retention_days'     => '999999',
					'trigger_states'     => array( 'completed', 'no-existe', 'EXTRA<script>' ),
				)
			)
		);

		$this->assertSame( 30, $clean['job_expiry_minutes'], '0 → valor por defecto 30.' );
		$this->assertSame( 3650, $clean['retention_days'], '999999 se acota al máximo 3650.' );
		$this->assertSame( array( 'created', 'completed' ), $clean['trigger_states'], 'Solo estados reales de WooCommerce; «created» siempre presente.' );
	}

	/** La salida es EXACTAMENTE el juego de claves que consume Settings::save(). */
	public function test_sanitize_output_keys_match_settings(): void {
		$clean    = SettingsPage::sanitize_instance( $this->raw_input( array() ) );
		$defaults = Settings::defaults();

		unset( $defaults['preset'], $defaults['uninstall_purge'] );

		$expected = array_keys( $defaults );
		$actual   = array_keys( $clean );
		sort( $expected );
		sort( $actual );

		$this->assertSame( $expected, $actual, 'sanitize_instance devuelve las claves de instancia (sin preset ni uninstall_purge).' );
	}
}
