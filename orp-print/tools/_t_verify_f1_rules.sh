#!/bin/sh
# QA temporal (_t_): verificación dinámica complementaria de F1-01/F1-02.
# - Selectividad de orp_config_version (retention_days NO sube; paper_width_mm SÍ).
# - Alta de locales con regla city (in / contains) desde el panel por HTTP.
# - Usuario sin capacidad manage_orp_print no ve las páginas.
# - Pedidos de prueba, normalización y local por defecto (via _t_verify_rules_orders.php).
# Solo crea datos con prefijo t- y limpia al final.
set -u

PASS=0
FAIL=0
ok()   { echo "PASS: $1"; PASS=$((PASS+1)); }
bad()  { echo "FAIL: $1"; FAIL=$((FAIL+1)); }
quote(){ echo "--- $1"; }

JAR=/tmp/_t_cookies.txt
BASE=http://wordpress
EPOCH=$(date +%s)
CODE_IN="t-alc-in-$EPOCH"
CODE_CONTAINS="t-alc-contains-$EPOCH"
USER_NOCAP="t_nocap_$EPOCH"

# ---------------------------------------------------------------- login admin
quote 'login admin'
rm -f "$JAR"
curl -sS -c "$JAR" -o /dev/null "$BASE/wp-login.php"
LOGIN=$(curl -sS -b "$JAR" -c "$JAR" -o /dev/null -w '%{http_code}' \
	--data 'log=admin&pwd=password&wp-submit=Log+In&testcookie=1' \
	-H 'Cookie: wordpress_test_cookie=WP Cookie check' \
	"$BASE/wp-login.php")
[ "$LOGIN" = "302" ] && ok 'login admin (302)' || bad "login admin devolvió $LOGIN"

# ------------------------------------------------- valores actuales de ajustes
CUR=$(wp eval '
$s = \Orvita\PrintBridge\Support\Settings::all();
echo "paper_width_mm={$s["paper_width_mm"]} encoding={$s["encoding"]} cut_mode={$s["cut_mode"]}"
   . " poll_interval={$s["poll_interval"]} job_expiry_minutes={$s["job_expiry_minutes"]}"
   . " retention_days={$s["retention_days"]} default_location_id={$s["default_location_id"]}"
   . " open_drawer=" . (empty($s["open_drawer"]) ? 0 : 1)
   . " sound_alert=" . (empty($s["sound_alert"]) ? 0 : 1)
   . " dedupe_scope={$s["dedupe_scope"]}";')
eval "$CUR"
echo "estado actual: $CUR"

# nonce de ajustes
curl -sS -b "$JAR" -o /tmp/_t_settings.html "$BASE/wp-admin/admin.php?page=orp-print"
NONCE=$(grep -o 'name="orp_settings_nonce" value="[a-f0-9]*"' /tmp/_t_settings.html | grep -o 'value="[a-f0-9]*"' | head -n1 | cut -d'"' -f2)
[ -n "$NONCE" ] && ok 'nonce de ajustes encontrado' || bad 'nonce de ajustes no encontrado'

BOOLS=""
[ "$open_drawer" = "1" ] && BOOLS="$BOOLS --data-urlencode orp[open_drawer]=1"
[ "$sound_alert" = "1" ] && BOOLS="$BOOLS --data-urlencode orp[sound_alert]=1"

CFG0=$(wp eval "echo (int) get_option('orp_config_version', 1);")

# --------------------------- POST 1: solo retention_days cambia → NO sube
quote 'solo retention_days cambia (no es clave del agente)'
RET_NEW=$((retention_days == 90 ? 89 : retention_days + 1))
curl -sS -b "$JAR" -o /tmp/_t_post1.html \
	--data-urlencode "orp_settings_nonce=$NONCE" \
	--data-urlencode "orp[dedupe_scope]=$dedupe_scope" \
	--data-urlencode "orp[paper_width_mm]=$paper_width_mm" \
	--data-urlencode "orp[encoding]=$encoding" \
	--data-urlencode "orp[cut_mode]=$cut_mode" \
	$BOOLS \
	--data-urlencode "orp[poll_interval]=$poll_interval" \
	--data-urlencode "orp[job_expiry_minutes]=$job_expiry_minutes" \
	--data-urlencode "orp[retention_days]=$RET_NEW" \
	--data-urlencode "orp[default_location_id]=$default_location_id" \
	--data-urlencode "orp_settings_action=1" \
	"$BASE/wp-admin/admin.php?page=orp-print"
CFG1=$(wp eval "echo (int) get_option('orp_config_version', 1);")
RET_SAVED=$(wp eval "echo (int) \\Orvita\\PrintBridge\\Support\\Settings::get('retention_days');")
[ "$CFG1" = "$CFG0" ] && ok "retention_days NO sube config_version ($CFG0 → $CFG1)" || bad "retention_days subió config_version ($CFG0 → $CFG1)"
[ "$RET_SAVED" = "$RET_NEW" ] && ok "retention_days persistido saneado ($RET_NEW)" || bad "retention_days no persistió ($RET_SAVED ≠ $RET_NEW)"

# --------------------------- POST 2: paper_width_mm cambia → SÍ sube (+1)
quote 'paper_width_mm cambia (clave del agente)'
PAPER_NEW=$((paper_width_mm == 80 ? 58 : 80))
curl -sS -b "$JAR" -o /tmp/_t_post2.html \
	--data-urlencode "orp_settings_nonce=$NONCE" \
	--data-urlencode "orp[dedupe_scope]=$dedupe_scope" \
	--data-urlencode "orp[paper_width_mm]=$PAPER_NEW" \
	--data-urlencode "orp[encoding]=$encoding" \
	--data-urlencode "orp[cut_mode]=$cut_mode" \
	$BOOLS \
	--data-urlencode "orp[poll_interval]=$poll_interval" \
	--data-urlencode "orp[job_expiry_minutes]=$job_expiry_minutes" \
	--data-urlencode "orp[retention_days]=$retention_days" \
	--data-urlencode "orp[default_location_id]=$default_location_id" \
	--data-urlencode "orp_settings_action=1" \
	"$BASE/wp-admin/admin.php?page=orp-print"
CFG2=$(wp eval "echo (int) get_option('orp_config_version', 1);")
PAPER_SAVED=$(wp eval "echo (int) \\Orvita\\PrintBridge\\Support\\Settings::get('paper_width_mm');")
[ "$CFG2" = "$((CFG1 + 1))" ] && ok "paper_width_mm sube config_version exactamente 1 ($CFG1 → $CFG2)" || bad "paper_width_mm: config_version $CFG1 → $CFG2 (esperaba +1)"
[ "$PAPER_SAVED" = "$PAPER_NEW" ] && ok "paper_width_mm persistido ($PAPER_NEW)" || bad "paper_width_mm no persistió"

# ------------------------------------------------ alta de locales por el panel
quote 'alta de local con regla city in'
curl -sS -b "$JAR" -o /tmp/_t_loc_in.html "$BASE/wp-admin/admin.php?page=orp-locations"
LNONCE=$(grep -o 'name="orp_location_nonce" value="[a-f0-9]*"' /tmp/_t_loc_in.html | grep -o 'value="[a-f0-9]*"' | head -n1 | cut -d'"' -f2)
[ -n "$LNONCE" ] && ok 'nonce de locales encontrado' || bad 'nonce de locales no encontrado'

curl -sS -b "$JAR" -o /tmp/_t_loc_in_post.html \
	--data-urlencode "orp_location_nonce=$LNONCE" \
	--data-urlencode "orp_action=save" \
	--data-urlencode "orp[code]=$CODE_IN" \
	--data-urlencode "orp[name]=QA Alcalá in" \
	--data-urlencode "orp[priority]=1" \
	--data-urlencode "orp[enabled]=1" \
	--data-urlencode 'orp[rules_json]=[{"type":"city","operator":"in","values":["Alcalá"]}]' \
	"$BASE/wp-admin/admin.php?page=orp-locations"
FOUND=$(wp eval "echo (int) (null !== \\Orvita\\PrintBridge\\Domain\\LocationRepository::find_by_code('$CODE_IN'));")
[ "$FOUND" = "1" ] && ok "local $CODE_IN creado por HTTP con regla in" || bad "local $CODE_IN no creado por HTTP"

quote 'alta de local con regla city contains'
curl -sS -b "$JAR" -o /tmp/_t_loc_ct_post.html \
	--data-urlencode "orp_location_nonce=$LNONCE" \
	--data-urlencode "orp_action=save" \
	--data-urlencode "orp[code]=$CODE_CONTAINS" \
	--data-urlencode "orp[name]=QA Alcalá contains" \
	--data-urlencode "orp[priority]=2" \
	--data-urlencode "orp[enabled]=1" \
	--data-urlencode 'orp[rules_json]=[{"type":"city","operator":"contains","values":["Alcalá"]}]' \
	"$BASE/wp-admin/admin.php?page=orp-locations"
FOUND=$(wp eval "echo (int) (null !== \\Orvita\\PrintBridge\\Domain\\LocationRepository::find_by_code('$CODE_CONTAINS'));")
[ "$FOUND" = "1" ] && ok "local $CODE_CONTAINS creado por HTTP con regla contains" || bad "local $CODE_CONTAINS no creado por HTTP"

# ------------------------------ usuario sin capacidad: acceso denegado
quote 'usuario sin manage_orp_print'
wp user create "$USER_NOCAP" "t_nocap@qa.local" --role=subscriber --user_pass=t_nocap_pass >/dev/null 2>&1 && ok 'usuario subscriptor creado' || bad 'no se pudo crear el usuario de prueba'
JAR2=/tmp/_t_cookies_nocap.txt
rm -f "$JAR2"
curl -sS -c "$JAR2" -o /dev/null "$BASE/wp-login.php"
LOGIN2=$(curl -sS -b "$JAR2" -c "$JAR2" -o /dev/null -w '%{http_code}' \
	--data "log=$USER_NOCAP&pwd=t_nocap_pass&wp-submit=Log+In&testcookie=1" \
	-H 'Cookie: wordpress_test_cookie=WP Cookie check' \
	"$BASE/wp-login.php")
[ "$LOGIN2" = "302" ] && ok 'login subscriptor (302)' || bad "login subscriptor devolvió $LOGIN2"
curl -sS -b "$JAR2" -L -o /tmp/_t_nocap_settings.html "$BASE/wp-admin/admin.php?page=orp-print"
if grep -q 'Ajustes de Orvita Print' /tmp/_t_nocap_settings.html; then
	bad 'usuario sin capacidad VE la página de ajustes'
else
	ok 'usuario sin capacidad NO ve la página de ajustes'
fi
if grep -qiE 'No tienes permisos|not allowed' /tmp/_t_nocap_settings.html; then
	ok 'acceso denegado con mensaje explícito'
else
	bad 'sin mensaje de denegación reconocible'
fi
for P in orp-locations orp-status; do
	curl -sS -b "$JAR2" -L -o /tmp/_t_nocap_page.html "$BASE/wp-admin/admin.php?page=$P"
	if grep -qE 'Locales y reglas|Estado de Orvita Print' /tmp/_t_nocap_page.html && ! grep -qiE 'not allowed|No tienes permisos'; then
		bad "usuario sin capacidad VE la página $P"
	else
		ok "usuario sin capacidad NO ve la página $P"
	fi
done

# ------------------------------- pedidos, normalización y local por defecto
quote 'pedidos y reglas (eval-file)'
wp eval-file tools/_t_verify_rules_orders.php run "$CODE_IN" "$CODE_CONTAINS"
RC=$?
[ "$RC" = "0" ] && ok 'suite de pedidos/reglas dentro de umbrales' || bad "suite de pedidos/reglas devolvió $RC"

# --------------------------------------------------------------------- cleanup
quote 'cleanup'
wp eval-file tools/_t_verify_rules_orders.php cleanup "$CODE_IN" "$CODE_CONTAINS" "$USER_NOCAP"
LEFT=$(wp eval "global \$wpdb; echo (int) \$wpdb->get_var(\"SELECT COUNT(*) FROM {\$wpdb->prefix}orp_locations WHERE code LIKE 't-alc-%'\");")
[ "$LEFT" = "0" ] && ok 'sin residuos de locales t-alc-*' || bad "quedan $LEFT locales t-alc-*"

echo "=============================="
echo "Resultado driver: $PASS PASS / $FAIL FAIL"
exit 0
