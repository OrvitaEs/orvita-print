#!/bin/sh
# Verificación HTTP de las páginas F1-03/F1-04 dentro del cli de wp-env:
#   npx wp-env run cli sh /var/www/html/wp-content/plugins/orp-print/tools/_verify_f1_03_04_admin.sh
#
# Comprueba: página «Plantilla del ticket» con sus campos, guardado con nonce
# (subida de orp_template_version), vista previa limpia (demo y por pedido) sin
# recursos externos, y los botones «Ver ticket» / «Reimprimir ticket» en la
# ficha del pedido (HPOS). Restaura la plantilla al final.
set -u

PASS=0
FAIL=0
ok()   { echo "PASS: $1"; PASS=$((PASS+1)); }
bad()  { echo "FAIL: $1"; FAIL=$((FAIL+1)); }
quote(){ echo "--- $1"; }

JAR=/tmp/orp_f1_03_04_cookies.txt
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

# Backup de plantilla y versión para restaurar al final.
wp eval 'file_put_contents("/tmp/orp_template_backup.json", wp_json_encode(array(
	"template" => get_option("orp_template", false),
	"tv" => (int) get_option("orp_template_version", 1),
)));'

# ------------------------------------------- submenú Plantilla y Vista previa
quote 'menú con las entradas nuevas'
curl -sS -b "$JAR" -o /tmp/orp_main.html "$BASE/wp-admin/admin.php?page=orp-print"
for marker in 'Plantilla del ticket' 'Vista previa' 'Locales y reglas'; do
	if grep -q "$marker" /tmp/orp_main.html; then ok "entrada de menú: $marker"; else bad "falta entrada de menú: $marker"; fi
done

# --------------------------------------------------- página Plantilla (F1-03)
quote 'página Plantilla del ticket'
curl -sS -b "$JAR" -o /tmp/orp_template.html "$BASE/wp-admin/admin.php?page=orp-template"
for marker in 'Plantilla del ticket' 'Campos del ticket' 'Texto del pie' 'Logo (PNG)' 'Registro de cambios' 'orp_template_nonce' 'Ver vista previa' 'Elegir logo'; do
	if grep -q "$marker" /tmp/orp_template.html; then ok "marca en Plantilla: $marker"; else bad "falta marca en Plantilla: $marker"; fi
done
# Los 12 campos del panel con su checkbox.
COUNT=$(grep -c 'name="orp\[fields\]\[' /tmp/orp_template.html)
if [ "$COUNT" -ge 12 ]; then ok "los campos del ticket están en el panel ($COUNT checkboxes)"; else bad "checkboxes de campo: $COUNT (se esperaban >= 12)"; fi

TV_BEFORE=$(wp eval "echo (int) get_option('orp_template_version', 1);")

quote 'guardar plantilla con nonce (solo dos campos + pie)'
TNONCE=$(grep -o 'name="orp_template_nonce" value="[a-f0-9]*"' /tmp/orp_template.html | grep -o 'value="[a-f0-9]*"' | head -n1 | cut -d'"' -f2)
if [ -z "$TNONCE" ]; then bad 'nonce de plantilla no encontrado'; fi

curl -sS -b "$JAR" -o /tmp/orp_template_post.html \
	--data-urlencode "orp_template_nonce=$TNONCE" \
	--data-urlencode "orp[fields][business_name]=1" \
	--data-urlencode "orp[fields][items]=1" \
	--data-urlencode "orp[fields][not_invoice]=1" \
	--data-urlencode "orp[footer_text]=Pie de verificación HTTP" \
	--data-urlencode "orp[logo_media_id]=0" \
	--data-urlencode "orp_template_action=1" \
	"$BASE/wp-admin/admin.php?page=orp-template"

if grep -q 'Plantilla guardada' /tmp/orp_template_post.html; then ok 'mensaje de guardado de plantilla'; else bad 'sin mensaje de guardado de plantilla'; fi

TV_AFTER=$(wp eval "echo (int) get_option('orp_template_version', 1);")
if [ "$TV_AFTER" = "$((TV_BEFORE+1))" ]; then ok "orp_template_version subió de $TV_BEFORE a $TV_AFTER"; else bad "template_version: antes $TV_BEFORE, después $TV_AFTER"; fi

SAVED=$(wp eval 'echo wp_json_encode(get_option("orp_template"));')
echo "$SAVED"
if echo "$SAVED" | grep -q '"business_name":true' && echo "$SAVED" | grep -q '"items":true' && echo "$SAVED" | grep -q '"customer":false'; then
	ok 'selección de campos persistida saneada (desactivados a false)'
else
	bad 'la selección de campos no se persistió como se envió'
fi

# ----------------------------------------------- vista previa demo (F1-04)
quote 'vista previa con datos de ejemplo (sin recursos externos)'
curl -sS -b "$JAR" -o /tmp/orp_preview_demo.html "$BASE/wp-admin/admin.php?page=orp-preview"
for marker in '@page' '80mm auto' 'window.print()' 'ORVITA PRINT · DEMO' 'monospace'; do
	if grep -q "$marker" /tmp/orp_preview_demo.html; then ok "vista previa demo: $marker"; else bad "falta en vista previa demo: $marker"; fi
done
if grep -q '<link' /tmp/orp_preview_demo.html || grep -q '@import' /tmp/orp_preview_demo.html || grep -q 'fonts.googleapis' /tmp/orp_preview_demo.html; then
	bad 'la vista previa tiene recursos externos'
else
	ok 'vista previa demo sin hojas externas ni fuentes remotas'
fi
# Página limpia: sin chrome de wp-admin (nada de adminbar ni wrap).
if grep -q 'wp-admin' /tmp/orp_preview_demo.html || grep -q 'admin-bar' /tmp/orp_preview_demo.html; then
	bad 'la vista previa arrastra chrome de wp-admin'
else
	ok 'vista previa limpia (sin chrome de wp-admin)'
fi

# ---------------------------------------------- pedido de prueba para el HTTP
quote 'pedido de prueba'
ORDER_ID=$(wp eval '
$product = new WC_Product_Simple();
$product->set_name("Pizza HTTP");
$product->set_regular_price("9.90");
$pid = $product->save();
$order = wc_create_order();
$order->add_product(wc_get_product($pid), 1);
$order->set_shipping_city("Madrid");
$order->set_shipping_address_1("Calle HTTP 1");
$order->calculate_totals();
$order->save();
echo (int) $order->get_id();
')
echo "order_id=$ORDER_ID"

# ------------------------------------------- vista previa por pedido (F1-04)
quote 'vista previa del pedido real'
curl -sS -b "$JAR" -o /tmp/orp_preview_order.html "$BASE/wp-admin/admin.php?page=orp-preview&order_id=$ORDER_ID"
for marker in '@page' 'window.print()' 'Pizza HTTP' 'No válido como factura'; do
	if grep -q "$marker" /tmp/orp_preview_order.html; then ok "vista previa pedido: $marker"; else bad "falta en vista previa pedido: $marker"; fi
done
# La plantilla guardada arriba (business_name + items + nota) no lleva kv:
# los datos del cliente no deben salir.
if grep -q 'class="orp-kv"' /tmp/orp_preview_order.html; then
	bad 'la plantilla activa debería ocultar el kv del pedido'
else
	ok 'el HTML respeta la selección de campos (kv oculto con esos campos)'
fi

# ----------------------------------------- ficha del pedido: botones (F1-04)
quote 'ficha del pedido (HPOS): Ver ticket y Reimprimir ticket en el desplegable'
curl -sS -b "$JAR" -o /tmp/orp_order_edit.html "$BASE/wp-admin/admin.php?page=wc-orders&action=edit&id=$ORDER_ID"
for marker in 'Ver ticket (Orvita Print)' 'Reimprimir ticket (Orvita Print)'; do
	if grep -q "$marker" /tmp/orp_order_edit.html; then ok "ficha del pedido: $marker"; else bad "falta en ficha del pedido: $marker"; fi
done

quote 'tabla de pedidos: botón Ver ticket con el enlace a la vista previa'
curl -sS -b "$JAR" -o /tmp/orp_order_list.html "$BASE/wp-admin/admin.php?page=wc-orders"
for marker in 'Ver ticket' "orp-preview&#038;order_id=$ORDER_ID"; do
	if grep -q "$marker" /tmp/orp_order_list.html; then ok "tabla de pedidos: $marker"; else bad "falta en tabla de pedidos: $marker"; fi
done

# --------------------------------------------------- limpieza y restauración
quote 'restauración'
wp eval "if (class_exists('WC_Order')) { \$o = wc_get_order($ORDER_ID); if (\$o) { \$o->delete(true); } }"
wp eval '
$backup = json_decode(file_get_contents("/tmp/orp_template_backup.json"), true);
if (false === $backup["template"]) { delete_option("orp_template"); } else { update_option("orp_template", $backup["template"], false); }
update_option("orp_template_version", $backup["tv"], false);
echo "template restaurada, tv=" . (int) get_option("orp_template_version", 1) . "\n";
'

# ---------------------------------------------------------------------- resumen
echo "=============================="
echo "Resultado: $PASS PASS / $FAIL FAIL"
exit 0
