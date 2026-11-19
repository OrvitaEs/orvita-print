#!/bin/sh
# Verificación end-to-end del rate limit (temporal; se borra tras la prueba).
# Uso: docker exec <cli> sh /var/www/html/wp-content/plugins/orp-print/tools/_e2e_ratelimit.sh

wp db query "DELETE FROM wp_options WHERE option_name LIKE '_transient_orp_rl_%' OR option_name LIKE '_transient_timeout_orp_rl_%'" --quiet

TOKEN='orp_a5d9afefb1060dd0a11b8fafd5f3d96e34a28154dffb8670'
URL='http://wordpress/wp-json/orp-print/v1/config'

i=1
n200=0
n429=0
notro=0
while [ "$i" -le 125 ]; do
    c=$(curl -sS -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $TOKEN" "$URL")
    if [ "$c" = "200" ]; then
        n200=$((n200+1))
    elif [ "$c" = "429" ]; then
        n429=$((n429+1))
    else
        notro=$((notro+1))
    fi
    i=$((i+1))
done

echo "Resultado end-to-end -> 200: $n200 | 429: $n429 | otro: $notro"
