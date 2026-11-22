<?php
/**
 * Pruebas del RuleMatcher y de la resolución de locales (F1-02):
 * cada tipo y operador, semántica AND, prioridad, fallback al local por
 * defecto con aviso, normalización de acentos y saneado del JSON de reglas.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Tests;

use Orvita\PrintBridge\Domain\LocationRepository;
use Orvita\PrintBridge\Domain\LocationResolver;
use Orvita\PrintBridge\Domain\RuleMatcher;
use Orvita\PrintBridge\Support\Clock;
use Orvita\PrintBridge\Support\Settings;

final class RuleMatcherTest extends \WP_UnitTestCase {

	/**
	 * Hechos de pedido por defecto para la lógica pura.
	 *
	 * @return array<string, mixed>
	 */
	private function facts(): array {
		return array(
			'city'             => array( 'alcala', 'alcala de henares' ),
			'postcode'         => array( '28801' ),
			'shipping_method'  => array( 'local_pickup:3', 'local_pickup' ),
			'product_category' => array( 'pizzas', 'Pizzas' ),
			'meta'             => array(
				'_shipping_zona' => 'zona norte',
				'orp_local_code' => 'local-a',
			),
		);
	}

	/** Helper: regla JSON → array decodificado. */
	private function rule( string $json ): array {
		$rules = json_decode( $json, true );
		$this->assertIsArray( $rules );
		return $rules[0];
	}

	/*
	 ---------------------------------------------------------------------
	 * Normalización.
	 * ------------------------------------------------------------------- */

	/** «Alcalá» y «alcala» son el mismo valor tras normalizar (Doc/04 §2.1). */
	public function test_normalize_strips_accents_and_lowercases(): void {
		$this->assertSame( 'alcala', RuleMatcher::normalize( 'Alcalá' ) );
		$this->assertSame( 'alcala de henares', RuleMatcher::normalize( '  Alcalá de Henáres ' ) );
		$this->assertSame( 'sevilla', RuleMatcher::normalize( 'SEVILLA' ) );
		$this->assertSame( 'a coruna', RuleMatcher::normalize( 'A Coruña' ) );
	}

	/*
	 ---------------------------------------------------------------------
	 * Operadores y tipos (lógica pura).
	 * ------------------------------------------------------------------- */

	public function test_city_in_matches_normalized_values(): void {
		$rule = $this->rule( '[{"type":"city","operator":"in","values":["Alcalá"]}]' );
		$this->assertTrue( RuleMatcher::matches_facts( $this->facts(), array( $rule ) ) );

		// Ciudad que no está: no coincide.
		$rule_bilbao = $this->rule( '[{"type":"city","operator":"in","values":["Bilbao"]}]' );
		$this->assertFalse( RuleMatcher::matches_facts( $this->facts(), array( $rule_bilbao ) ) );
	}

	public function test_city_equals_and_contains_and_prefix(): void {
		$equals   = $this->rule( '[{"type":"city","operator":"equals","values":["Alcalá"]}]' );
		$contains = $this->rule( '[{"type":"city","operator":"contains","values":["HENARES"]}]' );
		$prefix   = $this->rule( '[{"type":"city","operator":"prefix","values":["Alca"]}]' );
		$noprefix = $this->rule( '[{"type":"city","operator":"prefix","values":["28"]}]' );

		$this->assertTrue( RuleMatcher::matches_facts( $this->facts(), array( $equals ) ) );
		$this->assertTrue( RuleMatcher::matches_facts( $this->facts(), array( $contains ) ) );
		$this->assertTrue( RuleMatcher::matches_facts( $this->facts(), array( $prefix ) ) );
		$this->assertFalse( RuleMatcher::matches_facts( $this->facts(), array( $noprefix ) ) );
	}

	public function test_postcode_prefix_matches(): void {
		$rule = $this->rule( '[{"type":"postcode","operator":"prefix","values":["28"]}]' );
		$this->assertTrue( RuleMatcher::matches_facts( $this->facts(), array( $rule ) ) );

		$other = $this->rule( '[{"type":"postcode","operator":"prefix","values":["41"]}]' );
		$this->assertFalse( RuleMatcher::matches_facts( $this->facts(), array( $other ) ) );
	}

	public function test_shipping_method_in_matches_with_instance(): void {
		$with_instance = $this->rule( '[{"type":"shipping_method","operator":"in","values":["local_pickup:3"]}]' );
		$without       = $this->rule( '[{"type":"shipping_method","operator":"in","values":["flat_rate:1"]}]' );

		$this->assertTrue( RuleMatcher::matches_facts( $this->facts(), array( $with_instance ) ) );
		$this->assertFalse( RuleMatcher::matches_facts( $this->facts(), array( $without ) ) );
	}

	public function test_product_category_in_matches(): void {
		$rule = $this->rule( '[{"type":"product_category","operator":"in","values":["pizzas"]}]' );
		$this->assertTrue( RuleMatcher::matches_facts( $this->facts(), array( $rule ) ) );

		$other = $this->rule( '[{"type":"product_category","operator":"in","values":["postres"]}]' );
		$this->assertFalse( RuleMatcher::matches_facts( $this->facts(), array( $other ) ) );
	}

	public function test_order_meta_in_and_exists(): void {
		$in     = $this->rule( '[{"type":"order_meta","operator":"in","values":["_shipping_zona","Zona Norte"]}]' );
		$exists = $this->rule( '[{"type":"order_meta","operator":"exists","values":["_shipping_zona"]}]' );
		$absent = $this->rule( '[{"type":"order_meta","operator":"exists","values":["_meta_que_no"]}]' );

		$this->assertTrue( RuleMatcher::matches_facts( $this->facts(), array( $in ) ) );
		$this->assertTrue( RuleMatcher::matches_facts( $this->facts(), array( $exists ) ) );
		$this->assertFalse( RuleMatcher::matches_facts( $this->facts(), array( $absent ) ) );
	}

	/*
	 ---------------------------------------------------------------------
	 * Semántica del conjunto de reglas.
	 * ------------------------------------------------------------------- */

	/** Todas las reglas del local deben cumplirse (AND). */
	public function test_all_rules_must_match(): void {
		$rules = json_decode(
			'[{"type":"city","operator":"in","values":["Alcalá"]},{"type":"postcode","operator":"prefix","values":["28"]}]',
			true
		);
		$this->assertTrue( RuleMatcher::matches_facts( $this->facts(), $rules ) );

		$rules_bad = json_decode(
			'[{"type":"city","operator":"in","values":["Alcalá"]},{"type":"postcode","operator":"prefix","values":["41"]}]',
			true
		);
		$this->assertFalse( RuleMatcher::matches_facts( $this->facts(), $rules_bad ) );
	}

	/** Un local sin reglas no puede ganar por regla: solo puede ser el por defecto. */
	public function test_empty_rules_never_match(): void {
		$this->assertFalse( RuleMatcher::matches_facts( $this->facts(), array() ) );
	}

	/** Un tipo u operador desconocido invalida la regla. */
	public function test_unknown_type_or_operator_fails(): void {
		$bad_type     = $this->rule( '[{"type":"pais","operator":"in","values":["ES"]}]' );
		$bad_operator = $this->rule( '[{"type":"city","operator":"like","values":["Alcalá"]}]' );

		$this->assertFalse( RuleMatcher::matches_facts( $this->facts(), array( $bad_type ) ) );
		$this->assertFalse( RuleMatcher::matches_facts( $this->facts(), array( $bad_operator ) ) );
	}

	/*
	 ---------------------------------------------------------------------
	 * Saneado del JSON de reglas.
	 * ------------------------------------------------------------------- */

	public function test_sanitize_rules_json_accepts_valid_and_meta_key(): void {
		$valid = RuleMatcher::sanitize_rules_json(
			'[{"type":"order_meta","operator":"in","values":["_shipping_zona","Zona Norte"]}]'
		);

		$this->assertNull( $valid['error'] );
		$this->assertSame( 'order_meta', $valid['rules'][0]['type'] );
		$this->assertSame( '_shipping_zona', $valid['rules'][0]['key'] );
	}

	public function test_sanitize_rules_json_rejects_invalid(): void {
		$this->assertSame( 'invalid_json', RuleMatcher::sanitize_rules_json( '{ no es json' )['error'] );
		$this->assertSame( 'invalid_rule', RuleMatcher::sanitize_rules_json( '[{"type":"pais","operator":"in","values":["ES"]}]' )['error'] );
		$this->assertSame( 'invalid_rule', RuleMatcher::sanitize_rules_json( '[{"type":"city","operator":"like","values":["x"]}]' )['error'] );
		$this->assertNull( RuleMatcher::sanitize_rules_json( '' )['error'] );
		$this->assertSame( array(), RuleMatcher::sanitize_rules_json( '' )['rules'] );
	}

	/*
	 ---------------------------------------------------------------------
	 * Resolución completa (necesita WooCommerce para el pedido).
	 * ------------------------------------------------------------------- */

	/** ¿Está WooCommerce activo en el entorno de pruebas? Crea sus tablas si faltan. */
	private function woocommerce_or_skip(): void {
		if ( ! class_exists( \WC_Order::class ) ) {
			$this->markTestSkipped( 'WooCommerce no está activo en la suite de pruebas.' );
		}

		// Primera ejecución: instala las tablas (HPOS incluidas). Se exige
		// WordPress cargado del todo: de ahí que no vaya en el bootstrap.
		if ( ! get_option( 'woocommerce_db_version' ) ) {
			\WC_Install::install();
		}
	}

	/** Local por id. */
	private function insert_location( string $code, string $name, int $priority, string $rules_json, int $is_default ): int {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->insert(
			$wpdb->prefix . 'orp_locations',
			array(
				'code'       => $code,
				'name'       => $name,
				'enabled'    => 1,
				'priority'   => $priority,
				'rules'      => $rules_json,
				'is_default' => $is_default,
				'created_at' => Clock::now_sql(),
				'updated_at' => Clock::now_sql(),
			),
			array( '%s', '%s', '%d', '%d', '%s', '%d', '%s', '%s' )
		);

		return (int) $wpdb->insert_id;
	}

	/** Pedido de prueba con ciudad de envío. */
	private function make_order( string $shipping_city, string $billing_city = '' ): \WC_Order {
		$order = \wc_create_order();
		$this->assertInstanceOf( \WC_Order::class, $order );

		$order->set_shipping_city( $shipping_city );
		if ( '' !== $billing_city ) {
			$order->set_billing_city( $billing_city );
		}
		$order->save();

		return $order;
	}

	/** El primer local por prioridad ascendente que coincide gana. */
	public function test_resolver_picks_first_matching_location_by_priority(): void {
		$this->woocommerce_or_skip();

		$id_norte = $this->insert_location( 'local-norte', 'Norte', 10, '[{"type":"city","operator":"in","values":["Madrid"]}]', 0 );
		$this->insert_location( 'local-sur', 'Sur', 20, '[{"type":"city","operator":"in","values":["Madrid"]}]', 0 );

		$order    = $this->make_order( 'Madrid' );
		$resolved = LocationResolver::resolve_for_order( $order );

		$this->assertInstanceOf( \stdClass::class, $resolved );
		$this->assertSame( $id_norte, (int) $resolved->id );
	}

	/** Si ninguna regla coincide, va al local por defecto y se deja aviso. */
	public function test_resolver_falls_back_to_default_and_records_notice(): void {
		$this->woocommerce_or_skip();

		$this->insert_location( 'local-a', 'A', 10, '[{"type":"city","operator":"in","values":["Madrid"]}]', 0 );
		$id_default = $this->insert_location( 'local-b', 'B', 20, '[]', 1 );

		// Transient limpio antes de la resolución.
		delete_transient( 'orp_fallback_notice' );

		$order    = $this->make_order( 'Sevilla' );
		$resolved = LocationResolver::resolve_for_order( $order );

		$this->assertInstanceOf( \stdClass::class, $resolved );
		$this->assertSame( $id_default, (int) $resolved->id );

		// El aviso quedó en cola (transient) y se consume una sola vez.
		$notices = LocationResolver::take_fallback_notices();
		$this->assertCount( 1, $notices );
		$this->assertSame( $order->get_id(), (int) $notices[0]['order_id'] );
		$this->assertSame( 'local-b', (string) $notices[0]['location_code'] );
		$this->assertSame( array(), LocationResolver::take_fallback_notices() );
	}

	/** Shipping primero: billing solo como respaldo si shipping viene vacío. */
	public function test_shipping_city_takes_precedence_over_billing(): void {
		$this->woocommerce_or_skip();

		$this->insert_location( 'local-shipping', 'Por shipping', 10, '[{"type":"city","operator":"in","values":["Madrid"]}]', 0 );
		$this->insert_location( 'local-billing', 'Por billing', 20, '[{"type":"city","operator":"in","values":["Bilbao"]}]', 0 );

		// El checkout del piloto desactiva billing_city: no debe enrutar por él.
		$order    = $this->make_order( 'Madrid', 'Bilbao' );
		$resolved = LocationResolver::resolve_for_order( $order );

		$this->assertInstanceOf( \stdClass::class, $resolved );
		$this->assertSame( 'local-shipping', (string) $resolved->code );
	}

	/** Sin shipping_city, el billing actúa como respaldo. */
	public function test_billing_used_as_fallback_when_shipping_empty(): void {
		$this->woocommerce_or_skip();

		$this->insert_location( 'local-billing', 'Por billing', 10, '[{"type":"city","operator":"in","values":["Bilbao"]}]', 0 );

		$order    = $this->make_order( '', 'Bilbao' );
		$resolved = LocationResolver::resolve_for_order( $order );

		$this->assertInstanceOf( \stdClass::class, $resolved );
		$this->assertSame( 'local-billing', (string) $resolved->code );
	}

	/** Con shipping definido, billing no entra en la resolución. */
	public function test_billing_ignored_when_shipping_set(): void {
		$this->woocommerce_or_skip();

		$this->insert_location( 'local-billing', 'Por billing', 10, '[{"type":"city","operator":"in","values":["Bilbao"]}]', 0 );

		// Shipping presente y sin coincidencia: billing NO rescata el pedido.
		$order = $this->make_order( 'Sevilla', 'Bilbao' );

		delete_transient( 'orp_fallback_notice' );
		$resolved = LocationResolver::resolve_for_order( $order );

		if ( null === $resolved ) {
			$this->assertNull( $resolved, 'Sin default activo: null es válido.' );
			return;
		}

		$this->assertNotSame( 'local-billing', (string) $resolved->code, 'Billing no debe enrutar con shipping definido.' );
	}

	/** La regla order_meta se lee por la API CRUD (HPOS): get_meta. */
	public function test_order_meta_rule_via_crud_api(): void {
		$this->woocommerce_or_skip();

		$this->insert_location( 'local-meta', 'Meta', 10, '[{"type":"order_meta","operator":"in","values":["orp_local_code","local-a"]}]', 0 );

		$order = $this->make_order( 'Sevilla' );
		$order->update_meta_data( 'orp_local_code', 'local-a' );
		$order->save();

		$resolved = LocationResolver::resolve_for_order( $order );

		$this->assertInstanceOf( \stdClass::class, $resolved );
		$this->assertSame( 'local-meta', (string) $resolved->code );
	}

	/*
	 ---------------------------------------------------------------------
	 * Local por defecto sincronizado con orp_settings.
	 * ------------------------------------------------------------------- */

	public function test_set_default_updates_setting_and_clears_others(): void {
		$id_a = $this->insert_location( 'sync-a', 'A', 10, '[]', 0 );
		$id_b = $this->insert_location( 'sync-b', 'B', 20, '[]', 0 );

		$this->assertTrue( LocationRepository::set_default( $id_a ) );
		$this->assertSame( $id_a, (int) Settings::get( 'default_location_id' ) );

		$row_b = LocationRepository::get( $id_b );
		$this->assertSame( 0, (int) $row_b['is_default'] );

		LocationRepository::set_default( $id_b );
		$row_a = LocationRepository::get( $id_a );
		$this->assertSame( 0, (int) $row_a['is_default'], 'Solo puede haber un local por defecto.' );
	}
}
