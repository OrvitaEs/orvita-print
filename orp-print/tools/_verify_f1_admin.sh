#!/bin/sh
# Verificación HTTP de las páginas de administración (F1-01/F1-02) dentro del
# cli de wp-env. Login con admin/password (credenciales por defecto de wp-env)
# y comprobación de las tres páginas + guardado del formulario de instancia
# con nonce (subida de orp_config_version) y alta de local con nonce.
set -u

PASS=0
FAIL=0
ok()   { echo "PASS: $1"; PASS=$((PASS+1)); }
bad()  { echo "FAIL: $1"; FAIL=$((FAIL+1)); }
quote(){ echo "--- $1"; }

JAR=/tmp/orp_cookies.txt
BASE=http://wordpress

# ---------------------------------------------------------------- login
quote 'login wp-admin'
rm -f "$JAR"
curl -sS -c "$JAR" -o /dev/null "$BASE/wp-login.php"
LOGIN=$(curl -sS -b "$JAR" -c "$JAR" -o /tmp/orp_login.html -w '%{http_code}' \
	--data 'log=admin&pwd=password&wp-submit=Log+In&testcookie=1' \
	-H 'Cookie: wordpress_test_cookie=WP Cookie check' \
	"$BASE/wp-login.php")
if [ "$LOGIN" = "302" ]; then ok 'login correcto (302)'; else bad "login devolvio $LOGIN"; fi

# ------------------------------------------------- página Ajustes (F1-01)
quote 'página Ajustes'
curl -sS -b "$JAR" -o /tmp/orp_settings.html "$BASE/wp-admin/admin.php?page=orp-print"
for marker in 'Ajustes de Orvita Print' 'Capa 1' 'Capa 2' 'Capa 3' 'Aviso' 'Completo' 'Cocina + cliente' 'Personalizado' 'dedupe_scope' 'orp_settings_nonce'; do
	if grep -q "$marker" /tmp/orp_settings.html; then ok "marca en Ajustes: $marker"; else bad "falta marca en Ajustes: $marker"; fi
done

# ------------------------------------------- POST del formulario de instancia
quote 'guardar instancia con nonce'
NONCE=$(grep -o 'name="orp_settings_nonce" value="[a-f0-9]*"' /tmp/orp_settings.html | grep -o 'value="[a-f0-9]*"' | head -n1 | cut -d'"' -f2)
if [ -z "$NONCE" ]; then bad 'nonce de ajustes no encontrado'; fi

CFG_BEFORE=$(wp eval "echo (int) get_option('orp_config_version', 1);")

curl -sS -b "$JAR" -o /tmp/orp_post.html \
	--data-urlencode "orp_settings_nonce=$NONCE" \
	--data-urlencode "orp[trigger_states][]=processing" \
	--data-urlencode "orp[dedupe_scope]=trigger" \
	--data-urlencode "orp[paper_width_mm]=58" \
	--data-urlencode "orp[encoding]=CP858" \
	--data-urlencode "orp[cut_mode]=full" \
	--data-urlencode "orp[open_drawer]=1" \
	--data-urlencode "orp[poll_interval]=20" \
	--data-urlencode "orp[sound_alert]=1" \
	--data-urlencode "orp[job_expiry_minutes]=45" \
	--data-urlencode "orp[retention_days]=60" \
	--data-urlencode "orp[default_location_id]=0" \
	--data-urlencode "orp_settings_action=1" \
	"$BASE/wp-admin/admin.php?page=orp-print"

if grep -q 'Ajustes guardados' /tmp/orp_post.html; then ok 'mensaje de guardado tras el POST'; else bad 'sin mensaje de guardado'; fi

wp eval '
$s = get_option("orp_settings");
echo "saved: " . wp_json_encode($s);
' > /tmp/orp_saved.json
cat /tmp/orp_saved.json
echo ""

if grep -q '"paper_width_mm":58' /tmp/orp_saved.json && grep -q '"encoding":"CP858"' /tmp/orp_saved.json && grep -q '"dedupe_scope":"trigger"' /tmp/orp_saved.json; then
	ok 'ajustes de instancia persistidos con valores saneados'
else
	bad 'los ajustes no se persistieron como se envió'
fi

CFG_AFTER=$(wp eval "echo (int) get_option('orp_config_version', 1);")
if [ "$CFG_AFTER" = "$((CFG_BEFORE+1))" ]; then ok "orp_config_version subió de $CFG_BEFORE a $CFG_AFTER"; else bad "config_version: antes $CFG_BEFORE, después $CFG_AFTER"; fi

# ------------------------------------------------ POST del formulario de preset
quote 'aplicar preset Aviso'
PNONCE=$(grep -o 'name="orp_preset_nonce" value="[a-f0-9]*"' /tmp/orp_settings.html | grep -o 'value="[a-f0-9]*"' | head -n1 | cut -d'"' -f2)
curl -sS -b "$JAR" -o /tmp/orp_preset.html \
	--data-urlencode "orp_preset_nonce=$PNONCE" \
	--data-urlencode "orp_preset=aviso" \
	--data-urlencode "orp_preset_action=1" \
	"$BASE/wp-admin/admin.php?page=orp-print"

if grep -q 'aplicado' /tmp/orp_preset.html; then ok 'preset Aviso aplicado con mensaje'; else bad 'el POST del preset no aplicó'; fi
wp eval '
$s = get_option("orp_settings");
echo wp_json_encode(array("preset"=>$s["preset"],"dedupe_scope"=>$s["dedupe_scope"],"poll_interval"=>$s["poll_interval"]));
' > /tmp/orp_preset.json
cat /tmp/orp_preset.json
echo ""
if grep -q '"preset":"aviso"' /tmp/orp_preset.json && grep -q '"dedupe_scope":"order"' /tmp/orp_preset.json && grep -q '"poll_interval":10' /tmp/orp_preset.json; then
	ok 'el preset restauró su combinación típica (dedupe order, poll 10)'
else
	bad 'el preset no restauró los valores esperados'
fi

# --------------------------------------------------- página Locales (F1-02)
quote 'página Locales'
curl -sS -b "$JAR" -o /tmp/orp_locations.html "$BASE/wp-admin/admin.php?page=orp-locations"
for marker in 'Locales y reglas' 'Añadir local' 'Reglas (JSON)' 'local-centro'; do
	if grep -q "$marker" /tmp/orp_locations.html; then ok "marca en Locales: $marker"; else bad "falta marca en Locales: $marker"; fi
done

# Crear un local con JSON inválido → error y NO se guarda.
quote 'alta de local con JSON inválido'
# Limpia restos de ejecuciones anteriores del propio guion.
wp eval 'global $wpdb; $wpdb->delete("{$wpdb->prefix}orp_locations", array("code" => "local-http"), array("%s"));'
LNONCE=$(grep -o 'name="orp_location_nonce" value="[a-f0-9]*"' /tmp/orp_locations.html | grep -o 'value="[a-f0-9]*"' | head -n1 | cut -d'"' -f2)
curl -sS -b "$JAR" -o /tmp/orp_loc_bad.html \
	--data-urlencode "orp_location_nonce=$LNONCE" \
	--data-urlencode "orp_action=save" \
	--data-urlencode "orp[code]=local-http" \
	--data-urlencode "orp[name]=Local HTTP" \
	--data-urlencode "orp[priority]=15" \
	--data-urlencode "orp[enabled]=1" \
	--data-urlencode "orp[rules_json]=[{ roto" \
	"$BASE/wp-admin/admin.php?page=orp-locations"
if grep -q 'válido' /tmp/orp_loc_bad.html; then ok 'JSON inválido rechazado con mensaje de error'; else bad 'JSON inválido aceptado por la página'; fi
FOUND=$(wp eval 'global $wpdb; echo (int) $wpdb->get_var("SELECT COUNT(*) FROM {$wpdb->prefix}orp_locations WHERE code=\"local-http\"");')
if [ "$FOUND" = "0" ]; then ok 'el local con JSON inválido NO se guardó'; else bad 'se guardó un local con JSON inválido'; fi

# Crear un local válido → aparece en la tabla.
quote 'alta de local válido'
curl -sS -b "$JAR" -o /tmp/orp_loc_ok.html \
	--data-urlencode "orp_location_nonce=$LNONCE" \
	--data-urlencode "orp_action=save" \
	--data-urlencode "orp[code]=local-http" \
	--data-urlencode "orp[name]=Local HTTP" \
	--data-urlencode "orp[priority]=15" \
	--data-urlencode "orp[enabled]=1" \
	--data-urlencode 'orp[rules_json]=[{"type":"city","operator":"in","values":["Getafe"]}]' \
	"$BASE/wp-admin/admin.php?page=orp-locations"
if grep -q 'guardado' /tmp/orp_loc_ok.html; then ok 'local creado desde el panel'; else bad 'el alta del local falló'; fi
FOUND=$(wp eval 'global $wpdb; echo (int) $wpdb->get_var("SELECT COUNT(*) FROM {$wpdb->prefix}orp_locations WHERE code=\"local-http\" AND rules LIKE \"%Getafe%\"");')
if [ "$FOUND" = "1" ]; then ok 'el local válido quedó en la tabla con sus reglas'; else bad 'el local válido no está en la tabla'; fi

# --------------------------------------------------- página Estado
quote 'página Estado'
curl -sS -b "$JAR" -o /tmp/orp_status.html "$BASE/wp-admin/admin.php?page=orp-status"
for marker in 'Estado de Orvita Print' 'Trabajos por estado' 'Agentes'; do
	if grep -q "$marker" /tmp/orp_status.html; then ok "marca en Estado: $marker"; else bad "falta marca en Estado: $marker"; fi
done

# ---------------------------------------------------------------------- resumen
echo "=============================="
echo "Resultado: $PASS PASS / $FAIL FAIL"
exit 0
