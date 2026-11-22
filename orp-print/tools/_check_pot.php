<?php
/**
 * Comprobación temporal del .pot (se borra tras la verificación).
 */
$t = file_get_contents( __DIR__ . '/../languages/orp-print.pot' );
echo 'valid utf8: ', ( mb_check_encoding( $t, 'UTF-8' ) ? 'yes' : 'NO' ), PHP_EOL;
echo 'has Det+U+00E9: ', ( str_contains( $t, "Det\u{00E9}n" ) ? 'yes' : 'no' ), PHP_EOL;
echo 'has Det+U+01F9: ', ( str_contains( $t, "Det\u{01F9}n" ) ? 'yes' : 'no' ), PHP_EOL;

$lines = file( __DIR__ . '/../languages/orp-print.pot', FILE_IGNORE_NEW_LINES );
foreach ( $lines as $i => $line ) {
	if ( str_contains( $line, 'Det' ) ) {
		echo 'line ', ( $i + 1 ), ': ', $line, PHP_EOL;
	}
}
