#!/bin/sh
# Prepara la suite de pruebas de WordPress en /wordpress-phpunit (tests-cli de wp-env).
# La carpeta /wordpress-phpunit ya existe (creada por wp-env) y no se puede borrar,
# así que se llena en sitio en vez de usar install-wp-tests.sh completo.
set -e

if [ ! -f /wordpress-phpunit/includes/functions.php ]; then
	echo "Descargando wordpress-develop (trunk)…"
	cd /tmp
	curl -L -s https://github.com/WordPress/wordpress-develop/archive/refs/heads/trunk.tar.gz -o wd.tar.gz
	tar -zxmf wd.tar.gz -C /tmp

	cp -r /tmp/wordpress-develop-trunk/tests/phpunit/includes /wordpress-phpunit/includes
	cp -r /tmp/wordpress-develop-trunk/tests/phpunit/data /wordpress-phpunit/data

	if [ ! -f /wordpress-phpunit/wp-tests-config.php ]; then
		cp /tmp/wordpress-develop-trunk/wp-tests-config-sample.php /wordpress-phpunit/wp-tests-config.php
		# Núcleo de wp-env: /var/www/html del contenedor tests-cli.
		sed -i "s|dirname( __FILE__ ) . '/src/'|'/var/www/html/'|" /wordpress-phpunit/wp-tests-config.php
		sed -i "s|__DIR__ . '/src/'|'/var/www/html/'|" /wordpress-phpunit/wp-tests-config.php
		sed -i "s/youremptytestdbnamehere/tests-wordpress/" /wordpress-phpunit/wp-tests-config.php
		sed -i "s/yourusernamehere/root/" /wordpress-phpunit/wp-tests-config.php
		sed -i "s/yourpasswordhere/password/" /wordpress-phpunit/wp-tests-config.php
		sed -i "s|'localhost'|'tests-mysql'|" /wordpress-phpunit/wp-tests-config.php
	fi

	rm -rf /tmp/wordpress-develop-trunk /tmp/wd.tar.gz
	echo "Suite copiada a /wordpress-phpunit."
else
	echo "Suite ya presente."
fi
