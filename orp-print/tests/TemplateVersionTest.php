<?php
/**
 * Pruebas de la versión de plantilla en la clave de idempotencia (F1-03) y
 * de la reimpresión manual (F1-04, Doc/04 §3):
 * - Guardar la plantilla con cambios sube orp_template_version.
 * - La versión entra en la clave: un pedido con trabajo ya encolado genera
 *   un trabajo nuevo si vuelve a dispararse tras un cambio de plantilla,
 *   pero los trabajos existentes no se alteran ni se duplican.
 * - La reimpresión manual usa una clave distinta (order_id | manual | role |
 *   timestamp | user_id) y siempre genera trabajo nuevo.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Tests;

use Orvita\PrintBridge\Domain\JobFactory;
use Orvita\PrintBridge\Support\Clock;
use Orvita\PrintBridge\Template\DocumentBuilder;
use Orvita\PrintBridge\Template\TemplateSettings;

final class TemplateVersionTest extends \WP_UnitTestCase {

	/** @var int Local de pruebas. */
	private int $location_id;

	public function set_up(): void {
		parent::set_up();

		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$wpdb->insert(
			$wpdb->prefix . 'orp_locations',
			array(
				'code'       => 'test-template',
				'name'       => 'Local de pruebas de plantilla',
				'enabled'    => 1,
				'priority'   => 100,
				'rules'      => null,
				'is_default' => 0,
				'created_at' => Clock::now_sql(),
				'updated_at' => Clock::now_sql(),
			),
			array( '%s', '%s', '%d', '%d', '%s', '%d', '%s', '%s' )
		);
		$this->location_id = (int) $wpdb->insert_id;
	}

	public function tear_down(): void {
		delete_option( TemplateSettings::OPTION );
		delete_option( 'orp_template_version' );
		parent::tear_down();
	}

	/** Trabajos de un pedido en el local de pruebas. */
	private function jobs_for_order( int $order_id ): array {
		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		return (array) $wpdb->get_results(
			$wpdb->prepare(
				'SELECT id, idempotency_key, payload FROM ' . $wpdb->prefix . 'orp_jobs
				  WHERE location_id = %d AND order_id = %d ORDER BY id ASC',
				$this->location_id,
				$order_id
			)
		);
	}

	/** Documento mínimo de prueba. */
	private function document(): array {
		return array( 'version' => 1, 'blocks' => array() );
	}

	/* ---------------------------------------------------------------------
	 * orp_template_version en la clave de idempotencia.
	 * ------------------------------------------------------------------- */

	/** Guardar la plantilla con cambios reales sube la versión. */
	public function test_saving_template_with_changes_bumps_version(): void {
		$before = (int) get_option( 'orp_template_version', 1 );

		$changed = TemplateSettings::save(
			array(
				'fields'        => array( 'items' => true ),
				'footer_text'   => '',
				'logo_media_id' => 0,
			)
		);
		$this->assertTrue( $changed );

		$this->assertSame( $before + 1, (int) get_option( 'orp_template_version', 1 ) );
	}

	/** Guardar sin cambios no sube la versión (evita claves nuevas sin motivo). */
	public function test_saving_identical_template_does_not_bump_version(): void {
		$data = array(
			'fields'        => array( 'items' => true ),
			'footer_text'   => '',
			'logo_media_id' => 0,
		);

		TemplateSettings::save( $data );
		$after_first = (int) get_option( 'orp_template_version', 1 );

		$changed = TemplateSettings::save( $data );
		$this->assertFalse( $changed );
		$this->assertSame( $after_first, (int) get_option( 'orp_template_version', 1 ) );
	}

	/**
	 * Doc/04 §3: la versión de plantilla entra en la clave. Un pedido con
	 * trabajo encolado genera un trabajo NUEVO tras cambiar la plantilla
	 * (disparo posterior), y el trabajo original sigue intacto.
	 */
	public function test_template_version_participates_in_idempotency_key(): void {
		$order_id = 777;

		$first = JobFactory::create( $this->location_id, $order_id, 'default', 1, $this->document() );
		$this->assertNotNull( $first );

		// Guardar la plantilla sin cambios: la misma clave, no se duplica.
		TemplateSettings::save(
			array(
				'fields'        => TemplateSettings::fields(),
				'footer_text'   => TemplateSettings::footer_text(),
				'logo_media_id' => TemplateSettings::logo_media_id(),
			)
		);
		$repeat = JobFactory::create( $this->location_id, $order_id, 'default', 1, $this->document() );
		$this->assertNull( $repeat, 'Sin cambio de plantilla la deduplicación sigue activa.' );

		// Cambio real de plantilla: sube la versión y cambia la clave.
		$version_before = (int) get_option( 'orp_template_version', 1 );
		TemplateSettings::save(
			array(
				'fields'        => array( 'items' => true, 'totals' => true ),
				'footer_text'   => '',
				'logo_media_id' => 0,
			)
		);
		$this->assertSame( $version_before + 1, (int) get_option( 'orp_template_version', 1 ) );

		$second = JobFactory::create( $this->location_id, $order_id, 'default', 1, $this->document() );
		$this->assertNotNull( $second, 'Con plantilla nueva el mismo pedido genera trabajo nuevo.' );

		$jobs = $this->jobs_for_order( $order_id );
		$this->assertCount( 2, $jobs );
		$this->assertNotSame( $jobs[0]->idempotency_key, $jobs[1]->idempotency_key );

		// La clave nueva lleva la versión de plantilla vigente.
		$expected_key = hash(
			'sha256',
			$order_id . '|' . $this->location_id . '|default|' . (int) get_option( 'orp_template_version', 1 )
		);
		$this->assertSame( $expected_key, $jobs[1]->idempotency_key );
	}

	/* ---------------------------------------------------------------------
	 * Reimpresión manual (Doc/04 §3).
	 * ------------------------------------------------------------------- */

	/** La reimpresión manual genera trabajo nuevo con clave distinta y completa. */
	public function test_manual_reprint_creates_new_job_with_distinct_key(): void {
		$order_id = 888;
		$user_id  = 12;

		// Trabajo automático para comparar claves.
		$auto = JobFactory::create( $this->location_id, $order_id, 'default', 1, $this->document() );
		$this->assertNotNull( $auto );

		$manual_one = JobFactory::create_manual(
			$this->location_id,
			$order_id,
			'default',
			1,
			$this->document(),
			$user_id
		);
		$this->assertNotNull( $manual_one, 'La reimpresión manual siempre crea un trabajo nuevo.' );

		// Dos reimpresiones del mismo usuario: claves distintas (timestamp).
		$manual_two = JobFactory::create_manual(
			$this->location_id,
			$order_id,
			'default',
			1,
			$this->document(),
			$user_id
		);
		$this->assertNotNull( $manual_two );

		$jobs = $this->jobs_for_order( $order_id );
		$this->assertCount( 3, $jobs, 'Automático + dos reimpresiones: tres trabajos.' );

		$keys = array( $jobs[0]->idempotency_key, $jobs[1]->idempotency_key, $jobs[2]->idempotency_key );
		$this->assertCount( 3, array_unique( $keys ), 'Las tres claves son distintas.' );

		// Cada clave es sha256 (64 hex).
		foreach ( $keys as $key ) {
			$this->assertMatchesRegularExpression( '/^[0-9a-f]{64}$/', $key );
		}
	}

	/** La clave manual sigue la forma literal de Doc/04 §3. */
	public function test_manual_key_matches_contract_shape(): void {
		$order_id = 889;
		$user_id  = 34;

		$job = JobFactory::create_manual(
			$this->location_id,
			$order_id,
			'default',
			2,
			$this->document(),
			$user_id
		);
		$this->assertNotNull( $job );

		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$key = (string) $wpdb->get_var(
			$wpdb->prepare(
				'SELECT idempotency_key FROM ' . $wpdb->prefix . 'orp_jobs WHERE id = %d',
				$job
			)
		);

		// El timestamp y el usuario entran en la clave: no puede colisionar con
		// la forma automática (order_id | location | role | template_version)
		// ni con la de otro disparo.
		$automatic_key = hash(
			'sha256',
			$order_id . '|' . $this->location_id . '|default|' . (int) get_option( 'orp_template_version', 1 )
		);
		$this->assertNotSame( $automatic_key, $key );
		$this->assertNotSame(
			hash( 'sha256', $order_id . '|created|' . $this->location_id . '|default|' . (int) get_option( 'orp_template_version', 1 ) ),
			$key
		);
	}

	/** El disparador manual lleva el documento de la plantilla vigente. */
	public function test_manual_reprint_uses_current_template(): void {
		$this->markTestSkippedIfWooMissing();

		TemplateSettings::save(
			array(
				'fields'        => array( 'items' => true, 'footer' => true ),
				'footer_text'   => 'PIE DE PRUEBA',
				'logo_media_id' => 0,
			)
		);

		$product = new \WC_Product_Simple();
		$product->set_name( 'Pizza Barbacoa' );
		$product->set_regular_price( '11.00' );
		$product_id = $product->save();

		$order = \wc_create_order();
		$this->assertInstanceOf( \WC_Order::class, $order );
		$order->add_product( \wc_get_product( $product_id ), 1 );
		$order->calculate_totals();
		$order->save();

		$document = DocumentBuilder::build_from_order( $order, array() );

		$job = JobFactory::create_manual(
			$this->location_id,
			(int) $order->get_id(),
			'default',
			1,
			$document,
			1
		);
		$this->assertNotNull( $job );

		global $wpdb;

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery
		$payload = (string) $wpdb->get_var(
			$wpdb->prepare( 'SELECT payload FROM ' . $wpdb->prefix . 'orp_jobs WHERE id = %d', $job )
		);

		$this->assertStringContainsString( 'Pizza Barbacoa', $payload );
		$this->assertStringContainsString( 'PIE DE PRUEBA', $payload );
		$this->assertStringNotContainsString( 'No válido como factura', $payload );
	}

	/** Comprueba WooCommerce y crea sus tablas si faltan. */
	private function markTestSkippedIfWooMissing(): void {
		if ( ! class_exists( \WC_Order::class ) ) {
			$this->markTestSkipped( 'WooCommerce no está activo en la suite de pruebas.' );
		}

		if ( ! get_option( 'woocommerce_db_version' ) ) {
			\WC_Install::install();
		}
	}
}
