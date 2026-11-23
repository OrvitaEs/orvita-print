<?php
/**
 * Auditoría i18n (F1-11): comprueba que TODAS las llamadas de traducción de
 * src/ llevan el text domain orp-print y que no hay texto visible fuera de
 * funciones de traducción. Uso:
 *   npx @wordpress/env run cli php /var/www/html/wp-content/plugins/orp-print/tools/_audit_i18n.php
 */

declare(strict_types=1);

$dir        = __DIR__ . '/../src';
$domain     = 'orp-print';
$files      = new RecursiveIteratorIterator( new RecursiveDirectoryIterator( $dir ) );
$issues     = 0;
$translated = 0;

// Funciones de traducción que exigen el text domain como último argumento.
$trans_fns = array( '__', '_e', '_x', '_n', 'esc_html__', 'esc_html_e', 'esc_attr__', 'esc_attr_e', 'esc_js' );

foreach ( new RegexIterator( $files, '/\.php$/' ) as $file ) {
	$code = file_get_contents( $file->getPathname() );

	// 1. Cada función de traducción con literales debe llevar el dominio.
	foreach ( $trans_fns as $fn ) {
		if ( ! preg_match_all( '/' . $fn . '\s*\(([^\)]+)\)/', $code, $matches, PREG_OFFSET_CAPTURE ) ) {
			continue;
		}
		foreach ( $matches[1] as $match ) {
			$args   = $match[0];
			$has_lit = (bool) preg_match( '/\'[^\']+\'/', $args );

			if ( ! $has_lit ) {
				continue; // Argumentos variables: no se puede auditar estáticamente.
			}

			++$translated;
			$line = substr_count( substr( $code, 0, $match[1] ), "\n" ) + 1;

			if ( '_x' === $fn || '_n' === $fn ) {
				// El dominio es el último argumento de _n/_x.
				if ( ! preg_match( '/,\s*\'' . $domain . '\'\s*$/', trim( $args ) ) ) {
					echo "SIN DOMINIO {$file->getPathname()}:{$line} {$fn}(...)\n";
					++$issues;
				}
				continue;
			}

			if ( ! preg_match( '/,\s*\'' . $domain . '\'\s*\z/', trim( $args ) ) ) {
				echo "SIN DOMINIO {$file->getPathname()}:{$line} {$fn}( {$args} )\n";
				++$issues;
			}
		}
	}

	// 2. Texto visible fuera de funciones de traducción: echo/printf/wp_die
	//    con literales que empiezan por letra y no van por __()/esc_*__().
	//    (sprintf dentro de Logger::log es texto técnico de registro: no es UI.)
	if ( preg_match_all( '/(?:echo|(?<!s)printf|wp_die)\s*\(\s*[\'"]([A-ZÁÉÍÓÚÑ¿¡][^\'"]{2,})[\'"]/', $code, $raw, PREG_OFFSET_CAPTURE ) ) {
		foreach ( $raw[1] as $match ) {
			$line = substr_count( substr( $code, 0, $match[1] ), "\n" ) + 1;
			echo "TEXTO CRUDO {$file->getPathname()}:{$line} «{$match[0]}»\n";
			++$issues;
		}
	}
}

echo "---------------------------------------------\n";
echo "Llamadas de traducción auditadas: {$translated}\n";
echo "Problemas: {$issues}\n";
exit( $issues > 0 ? 1 : 0 );
