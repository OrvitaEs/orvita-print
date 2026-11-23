<?php
/**
 * TicketHtml — HTML imprimible del ticket (F1-04, Doc/03 §5.5).
 *
 * El MISMO documento neutro que alimenta orp_jobs alimenta esta vista:
 * «mismo contenido y mismo ancho», no fidelidad byte a byte (la fidelidad
 * ESC/POS es responsabilidad del agente).
 *
 * Reglas de negocio (Doc/03 §5.5):
 * - CSS `@page` con el ancho del papel (80 mm; 58 mm como variante).
 * - Fuente monoespaciada del sistema.
 * - CERO recursos externos: sin Google Fonts, sin imágenes remotas, sin
 *   hojas de estilo enlazadas. Solo estilos inline y data-URI del logo.
 * - El botón «Imprimir» llama a window.print(): acción del navegador, sin
 *   PDF ni servicios externos, y SIN crear trabajo en la cola.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Template;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class TicketHtml {

	/**
	 * Genera el HTML completo e imprimible de un documento neutro.
	 *
	 * @param array  $document Documento neutro v1 (Doc/04 §7.1).
	 * @param string $title    Título de la pestaña del navegador.
	 */
	public static function render( array $document, string $title ): string {
		$width = (int) ( $document['paper']['width_mm'] ?? 80 );
		$width = in_array( $width, array( 58, 80 ), true ) ? $width : 80;

		$blocks_html = '';
		foreach ( (array) ( $document['blocks'] ?? array() ) as $block ) {
			$blocks_html .= self::render_block( (array) $block );
		}

		$css = self::css( $width );

		return <<<HTML
<!doctype html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{$title}</title>
<style>{$css}</style>
</head>
<body>
<div class="orp-toolbar no-print">
	<button type="button" id="orp-print-button" onclick="window.print()">Imprimir</button>
</div>
<div class="orp-ticket">
{$blocks_html}</div>
</body>
</html>
HTML;
	}

	/* ---------------------------------------------------------------------
	 * Hoja de estilos: solo inline, cero recursos externos.
	 * ------------------------------------------------------------------- */

	private static function css( int $width_mm ): string {
		// font-size de referencia por ancho de papel: 58 mm aprieta el texto.
		$font = 58 === $width_mm ? '10px' : '12px';

		return <<<CSS
@page { size: {$width_mm}mm auto; margin: 0; }
* { box-sizing: border-box; }
html, body { margin: 0; padding: 0; background: #fff; }
body {
	width: {$width_mm}mm;
	margin: 0 auto;
	font-family: "Cascadia Mono", Consolas, "DejaVu Sans Mono", "Courier New", monospace;
	font-size: {$font};
	line-height: 1.35;
	color: #000;
	padding: 2mm;
	-webkit-print-color-adjust: exact;
	print-color-adjust: exact;
}
.orp-ticket { width: 100%; }
.orp-ticket p { margin: 1px 0; }
.orp-toolbar { text-align: center; margin-bottom: 8px; }
.orp-toolbar button {
	font-family: inherit; font-size: 13px; padding: 4px 14px; cursor: pointer;
}
.orp-align-left   { text-align: left; }
.orp-align-center { text-align: center; }
.orp-align-right  { text-align: right; }
.orp-bold         { font-weight: 700; }
.orp-size-double  { font-size: 1.7em; }
.orp-size-triple  { font-size: 2.4em; }
.orp-divider {
	border: 0; border-top: 1px dashed #000; margin: 3px 0;
}
.orp-kv { margin: 2px 0; }
.orp-kv .row {
	display: flex; gap: 4px; margin: 1px 0;
}
.orp-kv .row .label { flex: 0 0 auto; font-weight: 700; }
.orp-kv .row .value { flex: 1 1 auto; text-align: right; word-break: break-word; }
table.orp-items { width: 100%; border-collapse: collapse; margin: 3px 0; }
table.orp-items th { font-weight: 700; text-align: left; border-bottom: 1px dashed #000; padding: 1px 0; }
table.orp-items td { padding: 1px 0; vertical-align: top; word-break: break-word; }
table.orp-items td.num, table.orp-items th.num { text-align: right; white-space: nowrap; }
.orp-totals { margin: 3px 0; }
.orp-totals .row { display: flex; gap: 4px; margin: 1px 0; }
.orp-totals .row .label { flex: 1 1 auto; }
.orp-totals .row .value { flex: 0 0 auto; text-align: right; }
.orp-totals .row.total { font-weight: 700; border-top: 1px dashed #000; padding-top: 2px; margin-top: 2px; }
.orp-qr {
	margin: 4px 0; padding: 3px; border: 1px dashed #000; text-align: center;
}
.orp-qr .caption { font-weight: 700; }
.orp-qr .value { word-break: break-all; }
.orp-image { text-align: center; margin: 2px 0; }
.orp-image img {
	max-width: 100%; height: auto; image-rendering: pixelated;
}
.orp-cut { border-top: 1px dashed #000; margin: 4px 0 0; padding-top: 2px; text-align: center; font-size: 0.85em; }
.orp-feed { border-top: 1px dotted #000; margin: 3px 0 0; }
@media print {
	.no-print { display: none !important; }
	body { padding: 2mm; }
}
CSS;
	}

	/* ---------------------------------------------------------------------
	 * Bloques del documento neutro → HTML.
	 * ------------------------------------------------------------------- */

	/** Renderiza un bloque del documento. */
	private static function render_block( array $block ): string {
		return match ( (string) ( $block['type'] ?? '' ) ) {
			'text'    => self::block_text( $block ),
			'kv'      => self::block_kv( $block ),
			'items'   => self::block_items( $block ),
			'totals'  => self::block_totals( $block ),
			'divider' => '<hr class="orp-divider">',
			'image'   => self::block_image( $block ),
			'qr'      => self::block_qr( $block ),
			'barcode' => self::block_qr( array( 'value' => (string) ( $block['value'] ?? '' ) ) ),
			'feed'    => '<div class="orp-feed"></div>',
			'cut'     => '<div class="orp-cut">✂ corte</div>',
			default   => '',
		};
	}

	/** Bloque text: value, align, bold, size. */
	private static function block_text( array $block ): string {
		$value    = esc_html( (string) ( $block['value'] ?? '' ) );
		$raw      = (string) ( $block['align'] ?? 'left' );
		$align    = in_array( $raw, array( 'left', 'center', 'right' ), true ) ? $raw : 'left';
		$bold     = ! empty( $block['bold'] ) ? ' orp-bold' : '';
		$size     = 'double' === ( $block['size'] ?? '' ) ? ' orp-size-double' : ( 'triple' === ( $block['size'] ?? '' ) ? ' orp-size-triple' : '' );

		return '<p class="orp-align-' . $align . $bold . $size . '">' . nl2br( $value ) . '</p>';
	}

	/** Bloque kv: pares etiqueta/valor alineados. */
	private static function block_kv( array $block ): string {
		$rows = '';

		foreach ( (array) ( $block['items'] ?? array() ) as $item ) {
			$item  = (array) $item;
			$rows .= '<div class="row"><span class="label">' . esc_html( (string) ( $item['label'] ?? '' ) ) . '</span>'
				. '<span class="value">' . esc_html( (string) ( $item['value'] ?? '' ) ) . '</span></div>';
		}

		return '' === $rows ? '' : '<div class="orp-kv">' . $rows . '</div>';
	}

	/** Bloque items: tabla de productos con las alineaciones del documento. */
	private static function block_items( array $block ): string {
		$columns = array_values( (array) ( $block['columns'] ?? array() ) );
		$aligns  = array_values( (array) ( $block['align'] ?? array() ) );
		$rows    = (array) ( $block['rows'] ?? array() );

		if ( array() === $columns || array() === $rows ) {
			return '';
		}

		$head = '';
		foreach ( $columns as $i => $column ) {
			$is_right = isset( $aligns[ $i ] ) && 'right' === $aligns[ $i ];
			$head    .= '<th' . ( $is_right ? ' class="num"' : '' ) . '>' . esc_html( (string) $column ) . '</th>';
		}

		$body = '';
		foreach ( $rows as $row ) {
			$cells = '';
			foreach ( array_values( (array) $row ) as $i => $cell ) {
				$is_right = isset( $aligns[ $i ] ) && 'right' === $aligns[ $i ];
				$cells   .= '<td' . ( $is_right ? ' class="num"' : '' ) . '>' . esc_html( (string) $cell ) . '</td>';
			}
			$body .= '<tr>' . $cells . '</tr>';
		}

		return '<table class="orp-items"><thead><tr>' . $head . '</tr></thead><tbody>' . $body . '</tbody></table>';
	}

	/** Bloque totals: filas etiqueta/valor con negrita por fila. */
	private static function block_totals( array $block ): string {
		$rows = '';

		foreach ( (array) ( $block['items'] ?? array() ) as $item ) {
			$item  = (array) $item;
			$bold  = ! empty( $item['bold'] ) ? ' total' : '';
			$rows .= '<div class="row' . $bold . '"><span class="label">' . esc_html( (string) ( $item['label'] ?? '' ) ) . '</span>'
				. '<span class="value">' . esc_html( (string) ( $item['value'] ?? '' ) ) . '</span></div>';
		}

		return '' === $rows ? '' : '<div class="orp-totals">' . $rows . '</div>';
	}

	/**
	 * Bloque image: PNG base64 del propio documento como data-URI (recurso
	 * interno, no externo).
	 */
	private static function block_image( array $block ): string {
		$data = (string) ( $block['data'] ?? '' );

		if ( '' === $data ) {
			return '';
		}

		return '<div class="orp-image"><img src="data:image/png;base64,' . esc_attr( $data ) . '" alt=""></div>';
	}

	/**
	 * Bloque qr: en papel lo dibuja el agente (GS ( k); en la vista previa se
	 * muestra el valor codificado, sin generar imágenes externas.
	 */
	private static function block_qr( array $block ): string {
		$value = (string) ( $block['value'] ?? '' );

		if ( '' === $value ) {
			return '';
		}

		return '<div class="orp-qr"><div class="caption">QR</div><div class="value">' . esc_html( $value ) . '</div></div>';
	}
}
