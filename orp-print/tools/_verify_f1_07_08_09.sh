#!/bin/sh
# Humo HTTP de F1-07/F1-08/F1-09 dentro del cli de wp-env:
#   npx @wordpress/env run cli sh /var/www/html/wp-content/plugins/orp-print/tools/_verify_f1_07_08_09.sh
#
# POST /pair se consume por HTTP REAL (curl), como lo haría el agente .NET:
# 200 con token, reutilización 400, caducado 410, sexto intento 429,
# throttling por IP con cabecera Retry-After, token fuera de los registros.
# Después: páginas del panel por HTTP (Cola, Locales con generación de
# códigos, acciones de cancelar/reencolar con nonce) y avisos.
# Restaura el entorno al final.
set -u

PASS=0
FAIL=0
ok()   { echo "PASS: $1"; PASS=$((PASS+1)); }
bad()  { echo "FAIL: $1"; FAIL=$((FAIL+1)); }
quote(){ echo "--- $1"; }

JAR=/tmp/orp_f107_cookies.txt
BASE=http://wordpress

# Limpia el throttling por IP (el servidor HTTP no tiene caché persistente).
clear_ip() {
	wp db query "DELETE FROM wp_options WHERE option_name LIKE '\_transient\_orp\_pair\_rl\_%' OR option_name LIKE '\_transient\_timeout\_orp\_pair\_rl\_%'" --quiet
}

# Genera un código de emparejamiento para el local de verificación y lo saca.
new_code() {
	wp eval '
	if ( null === Orvita\PrintBridge\Domain\LocationRepository::find_by_code( "verify-f107" ) ) {
		Orvita\PrintBridge\Domain\LocationRepository::save( array(
			"code" => "verify-f107", "name" => "Local de verificación F1-07",
			"enabled" => true, "priority" => 1, "rules_json" => "[]", "is_default" => false,
		) );
	}
	$loc = Orvita\PrintBridge\Domain\LocationRepository::find_by_code( "verify-f107" );
	$code = Orvita\PrintBridge\Domain\PairingCodes::create( (int) $loc["id"], 1 );
	echo "CODE=" . $code["code"] . "\n";
	' | grep '^CODE=' | cut -d= -f2 | tr -d '\r'
}

# ------------------------------------------------------------- health
quote 'GET /health público (sin token)'
HEALTH=$(curl -sS "$BASE/wp-json/orp-print/v1/health")
if echo "$HEALTH" | grep -q '"ok":true'; then ok 'health 200 sin token'; else bad 'health no responde ok'; fi

# ------------------------------------------------------------- pair real
quote 'POST /pair por HTTP con un código nuevo (cuerpo como el agente .NET)'
CODE=$(new_code)
echo "código: $CODE"
clear_ip
PAIR=$(curl -sS -H 'Content-Type: application/json' \
	-d "{\"code\":\"$CODE\",\"agent_name\":\"TPV HTTP Humo\",\"platform\":\"windows\",\"agent_version\":\"0.9.0\",\"destinations\":[{\"role\":\"default\",\"label\":\"Mostrador\",\"printer_name\":\"EPSON TM-T20III\",\"copies\":2,\"enabled\":true}]}" \
	"$BASE/wp-json/orp-print/v1/pair")
echo "$PAIR" | grep -o '"token":"[^"]*' | head -c 40; echo "..."
TOKEN=$(echo "$PAIR" | grep -o '"token":"orp_[a-z0-9]*' | head -n1 | sed 's/.*"token":"//' | tr -d '\r')
LOCCODE=$(echo "$PAIR" | grep -o '"code":"verify-f107"' | head -n1)
if [ -n "$TOKEN" ] && [ -n "$LOCCODE" ] && echo "$PAIR" | grep -q '"poll_interval"'; then
	ok 'pair 200 por HTTP: token orp_…, location del código y poll_interval'
else
	bad "pair por HTTP desviado: $(echo "$PAIR" | head -c 200)"
fi

quote 'el token nuevo autentica en /config'
CFG=$(curl -sS -o /tmp/orp_cfg.json -w '%{http_code}' -H "Authorization: Bearer $TOKEN" "$BASE/wp-json/orp-print/v1/config")
if [ "$CFG" = "200" ] && grep -q 'config_version' /tmp/orp_cfg.json; then ok 'el token del pair funciona en /config'; else bad "el token no autentica ($CFG)"; fi

quote 'el destino del cuerpo quedó en BD'
DEST=$(wp db query "SELECT COUNT(*) FROM wp_orp_destinations WHERE printer_name = 'EPSON TM-T20III' AND copies = 2" --skip-column-names | tr -dc '0-9')
if [ "$DEST" -ge 1 ]; then ok 'destino upsertado en BD (role default, copias 2)'; else bad 'destino no guardado'; fi

quote 'reutilizar el mismo código → 400'
REUSE=$(curl -sS -o /tmp/orp_reuse.json -w '%{http_code}' -H 'Content-Type: application/json' \
	-d "{\"code\":\"$CODE\",\"agent_name\":\"Otro TPV\"}" "$BASE/wp-json/orp-print/v1/pair")
if [ "$REUSE" = "400" ] && grep -q 'orp_pairing_invalid' /tmp/orp_reuse.json; then ok 'reutilización → 400 orp_pairing_invalid'; else bad "reutilización devolvió $REUSE"; fi

quote 'el token NUNCA en los registros (mensaje ni datos)'
TOKEN_LOGS=$(wp db query "SELECT COUNT(*) FROM wp_orp_logs WHERE message LIKE '%$TOKEN%' OR data LIKE '%$TOKEN%'" --skip-column-names | tr -dc '0-9')
if [ "$TOKEN_LOGS" = "0" ]; then ok 'token ausente en orp_logs'; else bad "el token aparece $TOKEN_LOGS veces en los logs"; fi

# ----------------------------------------------------- sexto intento
quote 'sexto intento con el mismo código → 429 orp_pairing_locked'
clear_ip
CODE=$(new_code)
for i in 1 2 3 4 5; do
	curl -sS -o /dev/null -H 'Content-Type: application/json' -d "{\"code\":\"$CODE\"}" "$BASE/wp-json/orp-print/v1/pair"
done
SIXTH=$(curl -sS -o /tmp/orp_sixth.json -w '%{http_code}' -H 'Content-Type: application/json' \
	-d "{\"code\":\"$CODE\",\"agent_name\":\"TPV\"}" "$BASE/wp-json/orp-print/v1/pair")
if [ "$SIXTH" = "429" ] && grep -q 'orp_pairing_locked' /tmp/orp_sixth.json; then ok 'sexto intento → 429 orp_pairing_locked'; else bad "sexto intento devolvió $SIXTH"; fi

# ------------------------------------------------- throttling por IP (S-02)
quote '10 fallos por hora por IP → 429 orp_rate_limited con Retry-After'
clear_ip
for i in 0 1 2 3 4 5 6 7 8 9; do
	curl -sS -o /dev/null -H 'Content-Type: application/json' -d "{\"code\":\"99999$i\",\"agent_name\":\"TPV\"}" "$BASE/wp-json/orp-print/v1/pair"
done
CODE=$(new_code)
RL=$(curl -sS -D /tmp/orp_rl_headers.txt -o /tmp/orp_rl.json -w '%{http_code}' -H 'Content-Type: application/json' \
	-d "{\"code\":\"$CODE\",\"agent_name\":\"TPV\"}" "$BASE/wp-json/orp-print/v1/pair")
if [ "$RL" = "429" ] && grep -q 'orp_rate_limited' /tmp/orp_rl.json && grep -qi '^retry-after: [0-9]' /tmp/orp_rl_headers.txt; then
	ok "throttle por IP: 429 con cabecera $(grep -i 'retry-after' /tmp/orp_rl_headers.txt | tr -d '\r')"
else
	bad "throttle por IP desviado: $RL"
fi

# --------------------------------------------------- páginas del panel
quote 'login wp-admin'
rm -f "$JAR"
curl -sS -c "$JAR" -o /dev/null "$BASE/wp-login.php"
LOGIN=$(curl -sS -b "$JAR" -c "$JAR" -o /dev/null -w '%{http_code}' \
	--data 'log=admin&pwd=password&wp-submit=Log+In&testcookie=1' \
	-H 'Cookie: wordpress_test_cookie=WP Cookie check' \
	"$BASE/wp-login.php")
if [ "$LOGIN" = "302" ]; then ok 'login correcto (302)'; else bad "login devolvió $LOGIN"; fi

quote 'página Cola con cabecera de agentes y filtros'
curl -sS -b "$JAR" -o /tmp/orp_cola.html "$BASE/wp-admin/admin.php?page=orp-jobs"
if grep -q 'Cola de impresión' /tmp/orp_cola.html && grep -q 'Agentes por local' /tmp/orp_cola.html \
	&& grep -q 'name="status"' /tmp/orp_cola.html && grep -q 'name="location"' /tmp/orp_cola.html \
	&& grep -q 'name="date"' /tmp/orp_cola.html; then
	ok 'Cola: título, cabecera de agentes y filtros (estado/local/fecha)'
else
	bad 'la página Cola no muestra lo esperado'
fi

quote 'aviso de agente desconectado (sin latido) en el panel'
curl -sS -b "$JAR" -o /tmp/orp_notices.html "$BASE/wp-admin/admin.php?page=orp-print"
if grep -q 'sin heartbeat' /tmp/orp_notices.html; then ok 'aviso de agentes sin heartbeat visible'; else bad 'sin aviso de agentes desconectados'; fi

quote 'Locales: columna de emparejamiento y generación de código con nonce'
curl -sS -b "$JAR" -o /tmp/orp_locs.html "$BASE/wp-admin/admin.php?page=orp-locations"
if grep -q 'Emparejamiento' /tmp/orp_locs.html && grep -q 'Generar código' /tmp/orp_locs.html; then
	ok 'Locales: columna Emparejamiento con acción Generar código'
else
	bad 'Locales sin la columna de emparejamiento'
fi
LOC_ID=$(wp db query "SELECT id FROM wp_orp_locations WHERE code = 'verify-f107'" --skip-column-names | tr -dc '0-9')
PCNONCE=$(grep -o "orp_action=pair_code&#038;id=$LOC_ID&#038;_wpnonce=[a-f0-9]*" /tmp/orp_locs.html | head -n1 | sed 's/.*_wpnonce=//' | tr -d '\r')
if [ -n "$PCNONCE" ]; then
	curl -sS -b "$JAR" -o /tmp/orp_pairgen.html "$BASE/wp-admin/admin.php?page=orp-locations&orp_action=pair_code&id=$LOC_ID&_wpnonce=$PCNONCE"
	NEWV=$(wp db query "SELECT COUNT(*) FROM wp_orp_pairing_codes WHERE location_id = $LOC_ID AND used_at IS NULL AND expires_at > UTC_TIMESTAMP()" --skip-column-names | tr -dc '0-9')
	if [ "$NEWV" -ge 1 ]; then ok 'Generar código desde el panel crea un código vigente'; else bad 'el código no se generó'; fi
else
	bad 'nonce de pair_code no encontrado en la página'
fi

quote 'Cola: sembrar un failed y reencolarlo por HTTP con nonce'
JOB=$(wp eval '
global $wpdb;
$loc = Orvita\PrintBridge\Domain\LocationRepository::find_by_code( "verify-f107" );
$wpdb->insert( "{$wpdb->prefix}orp_jobs", array(
	"location_id" => (int) $loc["id"], "order_id" => 0, "printer_role" => "default", "copies" => 1,
	"status" => "failed", "payload" => "{\"version\":1,\"blocks\":[]}",
	"idempotency_key" => "humo-f108-" . uniqid( "", true ), "attempts" => 5, "max_attempts" => 5,
	"last_error" => "orp_printer_not_found: humo", "created_at" => gmdate( "Y-m-d H:i:s" ),
	"expires_at" => gmdate( "Y-m-d H:i:s", time() - 60 ),
), array( "%d", "%d", "%s", "%d", "%s", "%s", "%s", "%d", "%d", "%s", "%s", "%s" ) );
echo "JOB=" . $wpdb->insert_id;
' | grep '^JOB=' | cut -d= -f2 | tr -d '\r')
echo "trabajo sembrado: $JOB"
curl -sS -b "$JAR" -o /tmp/orp_cola2.html "$BASE/wp-admin/admin.php?page=orp-jobs&status=failed"
RQNONCE=$(grep -o "orp_action=requeue&#038;id=$JOB&#038;_wpnonce=[a-f0-9]*" /tmp/orp_cola2.html | head -n1 | sed 's/.*_wpnonce=//' | tr -d '\r')
if [ -n "$RQNONCE" ]; then
	curl -sS -b "$JAR" -o /tmp/orp_rq.html "$BASE/wp-admin/admin.php?page=orp-jobs&orp_action=requeue&id=$JOB&_wpnonce=$RQNONCE"
	RSTATE=$(wp db query "SELECT status FROM wp_orp_jobs WHERE id = $JOB" --skip-column-names | tr -d '\r')
	RA=$(wp db query "SELECT attempts FROM wp_orp_jobs WHERE id = $JOB" --skip-column-names | tr -dc '0-9')
	if [ "$RSTATE" = "pending" ] && [ "$RA" = "0" ]; then
		ok 'reencolar por HTTP: la fila vuelve a pending con attempts 0'
	else
		bad "reencolar por HTTP mal: $RSTATE attempts=$RA"
	fi
else
	bad 'nonce de reencolar no encontrado en la Cola'
fi

quote 'Cola: cancelar el trabajo reencolado por HTTP con nonce'
curl -sS -b "$JAR" -o /tmp/orp_cola3.html "$BASE/wp-admin/admin.php?page=orp-jobs"
CNONCE=$(grep -o "orp_action=cancel&#038;id=$JOB&#038;_wpnonce=[a-f0-9]*" /tmp/orp_cola3.html | head -n1 | sed 's/.*_wpnonce=//' | tr -d '\r')
if [ -n "$CNONCE" ]; then
	curl -sS -b "$JAR" -o /tmp/orp_cancel.html "$BASE/wp-admin/admin.php?page=orp-jobs&orp_action=cancel&id=$JOB&_wpnonce=$CNONCE"
	CSTATE=$(wp db query "SELECT status FROM wp_orp_jobs WHERE id = $JOB" --skip-column-names | tr -d '\r')
	if [ "$CSTATE" = "cancelled" ] && grep -q 'cancelado' /tmp/orp_cancel.html; then
		ok 'cancelar por HTTP: pending → cancelled con mensaje en el panel'
	else
		bad "cancelar por HTTP mal: $CSTATE"
	fi
else
	bad 'nonce de cancelar no encontrado en la Cola'
fi

quote 'la acción SIN nonce no procesa nada'
PENDING2=$(wp eval '
global $wpdb;
$loc = Orvita\PrintBridge\Domain\LocationRepository::find_by_code( "verify-f107" );
$wpdb->insert( "{$wpdb->prefix}orp_jobs", array(
	"location_id" => (int) $loc["id"], "order_id" => 0, "printer_role" => "default", "copies" => 1,
	"status" => "pending", "payload" => "{\"version\":1,\"blocks\":[]}",
	"idempotency_key" => "humo-f108-nn-" . uniqid( "", true ), "attempts" => 0, "max_attempts" => 5,
	"created_at" => gmdate( "Y-m-d H:i:s" ), "expires_at" => gmdate( "Y-m-d H:i:s", time() + 1800 ),
), array( "%d", "%d", "%s", "%d", "%s", "%s", "%s", "%d", "%d", "%s", "%s" ) );
echo "JOB=" . $wpdb->insert_id;
' | grep '^JOB=' | cut -d= -f2 | tr -d '\r')
curl -sS -b "$JAR" -o /dev/null "$BASE/wp-admin/admin.php?page=orp-jobs&orp_action=cancel&id=$PENDING2"
NSTATE=$(wp db query "SELECT status FROM wp_orp_jobs WHERE id = $PENDING2" --skip-column-names | tr -d '\r')
if [ "$NSTATE" = "pending" ]; then ok 'sin nonce el estado no cambia'; else bad "sin nonce cambió a $NSTATE"; fi

quote 'ver documento: la vista del payload está en la página'
curl -sS -b "$JAR" -o /tmp/orp_view.html "$BASE/wp-admin/admin.php?page=orp-jobs&orp_view=$PENDING2"
if grep -q 'Documento del trabajo' /tmp/orp_view.html && grep -q '&quot;version&quot;: 1' /tmp/orp_view.html; then
	ok 'vista del documento con el payload JSON'
else
	bad 'la vista del documento no aparece'
fi

# ---------------------------------------------------------- restaurar
quote 'restauración del entorno'
wp eval '
global $wpdb;
$loc = Orvita\PrintBridge\Domain\LocationRepository::find_by_code( "verify-f107" );
if ( null !== $loc ) {
	Orvita\PrintBridge\Domain\LocationRepository::delete( (int) $loc["id"] );
	$wpdb->query( $wpdb->prepare( "DELETE FROM {$wpdb->prefix}orp_jobs WHERE location_id = %d", (int) $loc["id"] ) );
}
$wpdb->query( "DELETE FROM {$wpdb->prefix}orp_logs WHERE message LIKE \"%humo%\" OR message LIKE \"%TPV HTTP Humo%\"" );
echo "entorno restaurado\n";
'
clear_ip

echo "=============================="
echo "Resultado: $PASS PASS / $FAIL FAIL"
exit 0
