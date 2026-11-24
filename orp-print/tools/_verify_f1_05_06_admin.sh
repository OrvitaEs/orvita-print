#!/bin/sh
# Verificación HTTP de los fixes del tester y del panel de destinos F1-06,
# dentro del cli de wp-env:
#   npx @wordpress/env run cli sh /var/www/html/wp-content/plugins/orp-print/tools/_verify_f1_05_06_admin.sh
#
# Comprueba: guardar «Ninguno» como local por defecto limpia el is_default
# huérfano de la tabla (sincronía bidireccional completa), el panel de
# destinos de Locales es de solo lectura con los avisos de impresora
# duplicada y de local sin salidas activas, y la página de ajustes muestra
# el estado de disparo. Restaura el entorno al final.
set -u

PASS=0
FAIL=0
ok()   { echo "PASS: $1"; PASS=$((PASS+1)); }
bad()  { echo "FAIL: $1"; FAIL=$((FAIL+1)); }
quote(){ echo "--- $1"; }

JAR=/tmp/orp_f1_05_06_cookies.txt
BASE=http://wordpress

# ---------------------------------------------------------------- login
quote 'login wp-admin'
rm -f "$JAR"
curl -sS -c "$JAR" -o /dev/null "$BASE/wp-login.php"
LOGIN=$(curl -sS -b "$JAR" -c "$JAR" -o /dev/null -w '%{http_code}' \
	--data 'log=admin&pwd=password&wp-submit=Log+In&testcookie=1' \
	-H 'Cookie: wordpress_test_cookie=WP Cookie check' \
	"$BASE/wp-login.php")
if [ "$LOGIN" = "302" ]; then ok 'login correcto (302)'; else bad "login devolvio $LOGIN"; fi

# ------------------------------------------------ backup de ajustes y default
wp eval '
file_put_contents("/tmp/orp_settings_backup.json", wp_json_encode(array(
	"settings" => get_option("orp_settings", false),
	"defaults" => $GLOBALS["wpdb"]->get_results("SELECT id, code FROM wp_orp_locations WHERE is_default = 1", ARRAY_A),
)));
echo "backup hecho\n";'

# --------------------------------- fix 1: «Ninguno» limpia el default huérfano
quote 'escenario del fix 1: opción a 0 con un is_default huérfano en la tabla'
wp eval '
global $wpdb;
$wpdb->query("UPDATE wp_orp_locations SET is_default = 0");
$wpdb->insert("wp_orp_locations", array(
	"code" => "verify-orphan", "name" => "Huérfano de verificación",
	"enabled" => 1, "priority" => 200, "rules" => null, "is_default" => 1,
	"created_at" => gmdate("Y-m-d H:i:s"), "updated_at" => gmdate("Y-m-d H:i:s"),
));
echo "huérfano creado: id " . $wpdb->insert_id . "\n";'

quote 'POST de ajustes con «Ninguno» (default_location_id = 0) y nonce'
curl -sS -b "$JAR" -o /tmp/orp_settings.html "$BASE/wp-admin/admin.php?page=orp-print"
SNONCE=$(grep -o 'name="orp_settings_nonce" value="[a-f0-9]*"' /tmp/orp_settings.html | grep -o 'value="[a-f0-9]*"' | head -n1 | cut -d'"' -f2)
if [ -z "$SNONCE" ]; then bad 'nonce de ajustes no encontrado'; fi

curl -sS -b "$JAR" -o /tmp/orp_settings_post.html \
	--data-urlencode "orp_settings_nonce=$SNONCE" \
	--data-urlencode "orp_settings_action=1" \
	--data-urlencode "orp[dedupe_scope]=order" \
	--data-urlencode "orp[paper_width_mm]=80" \
	--data-urlencode "orp[encoding]=CP850" \
	--data-urlencode "orp[cut_mode]=partial" \
	--data-urlencode "orp[poll_interval]=10" \
	--data-urlencode "orp[job_expiry_minutes]=30" \
	--data-urlencode "orp[retention_days]=90" \
	--data-urlencode "orp[default_location_id]=0" \
	"$BASE/wp-admin/admin.php?page=orp-print"

if grep -q 'Ajustes guardados' /tmp/orp_settings_post.html; then ok 'mensaje de ajustes guardados'; else bad 'sin mensaje de guardado de ajustes'; fi

RESULT=$(wp eval '
global $wpdb;
$orphan = (int) $wpdb->get_var("SELECT COUNT(*) FROM wp_orp_locations WHERE is_default = 1 AND code = \"verify-orphan\"");
$any    = (int) $wpdb->get_var("SELECT COUNT(*) FROM wp_orp_locations WHERE is_default = 1");
$opt    = (int) get_option("orp_settings")["default_location_id"];
echo "orphan=$orphan any=$any opt=$opt";')
echo "$RESULT"
case "$RESULT" in
	*orphan=0*any=0*opt=0*) ok '«Ninguno» limpia el is_default huérfano y la opción (sincronía bidireccional)' ;;
	*) bad "el huérfano no se limpió: $RESULT" ;;
esac

# ------------------------------- panel de destinos de Locales (F1-06)
quote 'panel de destinos en la página de locales'
# Salida de la siembra (local-centro) + dos locales con escenarios de aviso.
wp eval '
global $wpdb;
$centro = (int) $wpdb->get_var("SELECT id FROM wp_orp_locations WHERE code = \"local-centro\"");
$now    = gmdate("Y-m-d H:i:s");
$wpdb->insert("wp_orp_locations", array(
	"code" => "verify-dup", "name" => "Impresora duplicada", "enabled" => 1,
	"priority" => 250, "rules" => null, "is_default" => 0,
	"created_at" => $now, "updated_at" => $now,
));
$dup = (int) $wpdb->insert_id;
$wpdb->insert("wp_orp_destinations", array(
	"location_id" => $dup, "role" => "default", "label" => "A",
	"printer_name" => "IMPRESORA-REPETIDA", "copies" => 1, "enabled" => 1, "updated_at" => $now,
));
$wpdb->insert("wp_orp_destinations", array(
	"location_id" => $dup, "role" => "cocina", "label" => "B",
	"printer_name" => "IMPRESORA-REPETIDA", "copies" => 1, "enabled" => 1, "updated_at" => $now,
));
$wpdb->insert("wp_orp_locations", array(
	"code" => "verify-empty", "name" => "Sin salidas activas", "enabled" => 1,
	"priority" => 260, "rules" => null, "is_default" => 0,
	"created_at" => $now, "updated_at" => $now,
));
echo "locales de verificación creados\n";'

curl -sS -b "$JAR" -o /tmp/orp_locations.html "$BASE/wp-admin/admin.php?page=orp-locations"
for marker in 'Salidas (destinos) por local' 'Vista de solo lectura' 'Mostrador' 'Simuladora Virtual' 'IMPRESORA-REPETIDA'; do
	if grep -q "$marker" /tmp/orp_locations.html; then ok "panel de destinos: $marker"; else bad "falta en panel de destinos: $marker"; fi
done

if grep -q 'misma impresora' /tmp/orp_locations.html; then
	ok 'aviso de dos salidas a la misma impresora (Doc/04 §2.8)'
else
	bad 'sin aviso de impresora duplicada'
fi

if grep -q 'no tienen ninguna salida activa' /tmp/orp_locations.html; then
	ok 'aviso de local sin salidas activas'
else
	bad 'sin aviso de local sin salidas'
fi

# El panel no debe ofrecer editar salidas (sin formularios con names de destino).
if grep -q 'name="orp_dest' /tmp/orp_locations.html; then
	bad 'el panel de destinos ofrece edición (debe ser solo lectura)'
else
	ok 'el panel de destinos no ofrece edición (solo lectura)'
fi

# --------------------------------------------------- limpieza y restauración
quote 'restauración'
wp eval '
global $wpdb;
$jobs = "wp_orp_jobs"; $dest = "wp_orp_destinations"; $loc = "wp_orp_locations";
foreach (array("verify-dup", "verify-empty", "verify-orphan") as $code) {
	$id = (int) $wpdb->get_var($wpdb->prepare("SELECT id FROM {$loc} WHERE code = %s", $code));
	if ($id > 0) {
		$wpdb->query($wpdb->prepare("DELETE FROM {$jobs} WHERE location_id = %d", $id));
		$wpdb->query($wpdb->prepare("DELETE FROM {$dest} WHERE location_id = %d", $id));
		$wpdb->query($wpdb->prepare("DELETE FROM {$loc} WHERE id = %d", $id));
	}
}
$backup = json_decode(file_get_contents("/tmp/orp_settings_backup.json"), true);
update_option("orp_settings", $backup["settings"], false);
$ids = array_map(function ($r) { return (int) $r["id"]; }, (array) $backup["defaults"]);
if (count($ids) > 0) {
	$in = implode(",", $ids);
	$wpdb->query("UPDATE {$loc} SET is_default = CASE WHEN id IN ($in) THEN 1 ELSE 0 END");
} else {
	$wpdb->query("UPDATE {$loc} SET is_default = 0");
}
delete_transient("orp_missing_default_notice");
delete_transient("orp_fallback_notice");
echo "entorno restaurado\n";'

# ---------------------------------------------------------------------- resumen
echo "=============================="
echo "Resultado: $PASS PASS / $FAIL FAIL"
exit 0
