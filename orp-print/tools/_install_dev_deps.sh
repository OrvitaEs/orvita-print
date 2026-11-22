#!/bin/sh
# Instala las dependencias de desarrollo del plugin DENTRO del contenedor tests-cli
# (phpunit + polyfills del conjunto de pruebas de WP). Solo para desarrollo; el
# ZIP de producción del plugin no lleva vendor/.
set -e
cd /var/www/html/wp-content/plugins/orp-print

if [ ! -d vendor/yoast/phpunit-polyfills ]; then
	composer config --no-plugins allow-plugins.dealerdirect/phpcodesniffer-composer-installer true
	composer update --no-interaction --quiet
fi
echo 'vendor/ listo.'
