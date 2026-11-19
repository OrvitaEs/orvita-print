#!/bin/sh
# Depuración temporal: 10 peticiones HTTP mostrando el transient entre cada una.
# Uso: docker exec <cli> sh /var/www/html/wp-content/plugins/orp-print/tools/_debug_rl.sh

wp db query "DELETE FROM wp_options WHERE option_name LIKE '_transient_orp_rl_%' OR option_name LIKE '_transient_timeout_orp_rl_%'" --quiet

TOKEN='orp_a5d9afefb1060dd0a11b8fafd5f3d96e34a28154dffb8670'
URL='http://wordpress/wp-json/orp-print/v1/config'

i=1
while [ "$i" -le 10 ]; do
    c=$(curl -sS -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $TOKEN" "$URL")
    echo "--- peticion $i -> HTTP $c"
    wp db query "SELECT option_name, option_value FROM wp_options WHERE option_name LIKE '%orp_rl%'" --skip-column-names
    i=$((i+1))
done
