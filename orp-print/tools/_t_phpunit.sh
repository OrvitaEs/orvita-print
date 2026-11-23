#!/usr/bin/env bash
# Ejecuta la suite PHPUnit del plugin dentro del contenedor tests-cli.
set -e
cd /var/www/html/wp-content/plugins/orp-print
WP_TESTS_DIR=/wordpress-phpunit vendor/bin/phpunit "$@"
