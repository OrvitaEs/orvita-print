<?php
/**
 * Pruebas del HTML imprimible (F1-04, Doc/03 §5.5):
 * contiene los datos del documento, CSS @page con el ancho del papel
 * (80/58 mm), fuente monoespaciada y CERO recursos externos.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Tests;

use Orvita\PrintBridge\Support\Settings;
use Orvita\PrintBridge\Template\DocumentBuilder;
use Orvita\PrintBridge\Template\TicketHtml;

final class TicketHtmlTest extends \WP_UnitTestCase {

	public function tear_down(): void {
		// Restaura el ajuste de ancho de papel por si algún test lo cambia.
		$settings          = get_option( Settings::OPTION, array() );
		$settings['paper_width_mm'] = 80;
		update_option( Settings::OPTION, $settings, false );

		parent::tear_down();
	}

	/** Documento de demostración para las pruebas. */
	private function demo_document(): array {
		return DocumentBuilder::build_demo(
			array(
				'order_id'     => 0,
				'location_code' => 'local-centro',
				'printer_role' => 'preview',
			)
		);
	}

	/** El HTML contiene los datos del documento y la regla @page a 80 mm. */
	public function test_html_contains_document_data_and_page_rule(): void {
		$document = $this->demo_document();
		$html     = TicketHtml::render( $document, 'Ticket de prueba' );

		$this->assertStringContainsString( '@page', $html );
		$this->assertStringContainsString( '80mm auto', $html );
		$this->assertStringContainsString( 'window.print()', $html, 'El botón Imprimir es acción del navegador.' );

		// Los datos del documento están en el HTML (mismo contenido).
		$this->assertStringContainsString( 'ORVITA PRINT · DEMO', $html );
		$this->assertStringContainsString( '36,00', $html );
		$this->assertStringContainsString( 'No válido como factura', $html );
		$this->assertStringContainsString( 'Pizza Margarita · jamón, piña, ñ, €, áéíóú', $html );
		$this->assertStringContainsString( 'Pizza Barbacoa', $html );

		// Fuente monoespaciada del sistema, sin fuentes remotas.
		$this->assertStringContainsString( 'monospace', $html );

		// Alineaciones del bloque items respetadas.
		$this->assertStringContainsString( 'class="num"', $html );
	}

	/** Variante de 58 mm según el ajuste de ancho de papel. */
	public function test_html_renders_58mm_variant(): void {
		$settings                    = get_option( Settings::OPTION, array() );
		$settings['paper_width_mm'] = 58;
		update_option( Settings::OPTION, $settings, false );

		$html = TicketHtml::render( $this->demo_document(), 'Ticket 58' );

		$this->assertStringContainsString( '58mm auto', $html );
		$this->assertStringNotContainsString( '80mm auto', $html );
	}

	/** CERO recursos externos: nada enlazado ni cargado fuera del documento. */
	public function test_html_has_no_external_resources(): void {
		$html = TicketHtml::render( $this->demo_document(), 'Ticket sin recursos externos' );

		$this->assertStringNotContainsString( '<link', $html, 'Sin hojas de estilo externas.' );
		$this->assertStringNotContainsString( '<script src', $html, 'Sin scripts externos.' );
		$this->assertStringNotContainsString( '@import', $html );
		$this->assertStringNotContainsString( 'fonts.googleapis', $html );
		$this->assertStringNotContainsString( '<iframe', $html );

		// Ni src ni href que apunten fuera del documento (data: permitido).
		$this->assertSame(
			0,
			(int) preg_match( '/(src|href)\s*=\s*["\'](?!data:)[^"\']*["\']/i', $html ),
			'Solo data-URI internos: nada remoto.'
		);
	}

	/** El logo del documento va como data-URI PNG (recurso interno). */
	public function test_html_embeds_logo_as_data_uri(): void {
		$document          = $this->demo_document();
		$document['blocks'] = array_merge(
			array(
				array(
					'type'       => 'image',
					'data'       => 'UE5HcHJ1ZWJh', // base64 de prueba, no PNG real: solo la vista.
					'width_dots' => 64,
				),
			),
			$document['blocks']
		);

		$html = TicketHtml::render( $document, 'Ticket con logo' );

		$this->assertStringContainsString( 'src="data:image/png;base64,UE5HcHJ1ZWJh"', $html );
		$this->assertStringContainsString( 'image-rendering', $html );
	}

	/** Bloques desconocidos se ignoran sin romper el HTML (Doc/04 §8). */
	public function test_unknown_blocks_are_ignored(): void {
		$document          = $this->demo_document();
		$document['blocks'] = array_merge(
			array( array( 'type' => 'raw', 'data' => 'cHJ1ZWJh' ) ),
			$document['blocks']
		);

		$html = TicketHtml::render( $document, 'Ticket con bloque desconocido' );

		$this->assertStringContainsString( 'ORVITA PRINT · DEMO', $html );
		$this->assertStringNotContainsString( 'cHJ1ZWJh', $html );
	}
}
