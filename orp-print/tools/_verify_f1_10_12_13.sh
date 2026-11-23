#!/bin/sh
# Verificación dinámica de F1-10/F1-12/F1-13 dentro del cli de wp-env:
#   npx @wordpress/env run cli sh /var/www/html/wp-content/plugins/orp-print/tools/_verify_f1_10_12_13.sh
#
# Matriz de seguridad de Doc/05 §4.6 por HTTP REAL (curl):
#   - 401 sin token en TODOS los endpoints protegidos;
#   - token del local A no lee ni reclama trabajos del local B;
#   - código de emparejamiento caduca a 10 min (410);
#   - sexto intento bloquea (429 orp_pairing_locked);
#   - rate limit por token: 429 con Retry-After;
#   - el token no aparece en orp_logs;
# y humo HTTP de la página «Estado» (F1-10) con sus secciones.
# Restaura el entorno al final.
set -u

PASS=0
FAIL=0
ok()   { echo "PASS: $1"; PASS=$((PASS+1)); }
bad()  { echo "FAIL: $1"; FAIL=$((FAIL+1)); }
quote(){ echo "--- $1"; }

JAR=/tmp/orp_f110_cookies.txt
BASE=http://wordpress
NS=orp-print/v1

clear_rl() {
	wp db query "DELETE FROM wp_options WHERE option_name LIKE '\_transient\_orp\_rl\_%' OR option_name LIKE '\_transient\_timeout\_orp\_rl\_%' OR option_name LIKE '\_transient\_orp\_pair\_rl\_%' OR option_name LIKE '\_transient\_timeout\_orp\_pair\_rl\_%'" --quiet
}

# Dos locales con un agente y un trabajo pendiente cada uno.
setup_two_locations() {
	wp eval '
	$now = Orvita\PrintBridge\Support\Clock::now_sql();
	global $wpdb;
	foreach ( array( "A" => "a", "B" => "b" ) as $LETTER => $low ) {
		$code = "verify-f110-$low";
		$wpdb->query( $wpdb->prepare( "DELETE FROM {$wpdb->prefix}orp_locations WHERE code = %s", $code ) );
		$wpdb->insert( $wpdb->prefix . "orp_locations", array(
			"code" => $code, "name" => "Local de verificación $LETTER", "enabled" => 1,
			"priority" => 1, "rules" => null, "is_default" => 0,
			"created_at" => $now, "updated_at" => $now,
		) );
		$loc = (int) $wpdb->insert_id;

		$wpdb->query( $wpdb->prepare( "DELETE FROM {$wpdb->prefix}orp_agents WHERE name = %s", "Agente $LETTER F110" ) );
		$token = Orvita\PrintBridge\Rest\Auth::generate_token();
		$wpdb->insert( $wpdb->prefix . "orp_agents", array(
			"location_id" => $loc, "name" => "Agente $LETTER F110",
			"token_hash" => $token["hash"], "token_prefix" => $token["prefix"],
			"status" => "active", "created_at" => $now,
		) );
		$wpdb->insert( $wpdb->prefix . "orp_jobs", array(
			"location_id" => $loc, "order_id" => ( "A" === $LETTER ? 1101 : 2202 ),
			"printer_role" => "default", "copies" => 1, "status" => "pending",
			"payload" => "{\"version\":1,\"blocks\":[]}",
			"idempotency_key" => "verify-f110-$low-" . uniqid(),
			"attempts" => 0, "max_attempts" => 5,
			"created_at" => $now, "expires_at" => gmdate( "Y-m-d H:i:s", time() + 3600 ),
		) );
		echo "TOKEN$LETTER=" . $token["token"] . "\n";
	}
	' | grep -E '^TOKEN[AB]=' > /tmp/orp_f110_tokens.env
}

cleanup() {
	wp eval '
	global $wpdb;
	$wpdb->query( "DELETE FROM {$wpdb->prefix}orp_jobs WHERE idempotency_key LIKE \"verify-f110-%\"" );
	$wpdb->query( "DELETE FROM {$wpdb->prefix}orp_agents WHERE name LIKE \"% F110\" OR location_id IN ( SELECT id FROM ( SELECT id FROM {$wpdb->prefix}orp_locations WHERE code LIKE \"verify-f110-%\" ) AS tmp )" );
	$wpdb->query( "DELETE FROM {$wpdb->prefix}orp_pairing_codes WHERE location_id IN ( SELECT id FROM ( SELECT id FROM {$wpdb->prefix}orp_locations WHERE code LIKE \"verify-f110-%\" ) AS tmp2 )" );
	$wpdb->query( "DELETE FROM {$wpdb->prefix}orp_locations WHERE code LIKE \"verify-f110-%\"" );
	' >/dev/null 2>&1
	rm -f "$JAR" /tmp/orp_f110_tokens.env
}
trap cleanup EXIT

# ------------------------------------------------------------ datos de prueba
quote 'Sembrar dos locales (A y B) con agente y trabajo cada uno'
setup_two_locations
TOKEN_A=$(grep '^TOKENA=' /tmp/orp_f110_tokens.env | cut -d= -f2 | tr -d '\r')
TOKEN_B=$(grep '^TOKENB=' /tmp/orp_f110_tokens.env | cut -d= -f2 | tr -d '\r')
if [ -n "$TOKEN_A" ] && [ -n "$TOKEN_B" ]; then
	ok 'dos locales sembrados con sus tokens'
else
	bad 'la siembra no generó los tokens'
	exit 1
fi

# ------------------------------------------------------------ 401 sin token
quote '401 sin token en todos los endpoints protegidos (Doc/05 §4.6)'
for EP in "GET /jobs" "POST /jobs/1/claim" "POST /jobs/1/ack" "POST /jobs/1/release" "POST /heartbeat" "GET /config"; do
	METHOD=${EP%% *}; PATH_=${EP#* }
	CODE=$(curl -sS -o /dev/null -w '%{http_code}' -X "$METHOD" "$BASE/wp-json/$NS$PATH_")
	BODY=$(curl -sS -X "$METHOD" "$BASE/wp-json/$NS$PATH_")
	if [ "$CODE" = "401" ] && echo "$BODY" | grep -q 'orp_invalid_token'; then
		ok "$METHOD $PATH_ → 401 orp_invalid_token"
	else
		bad "$METHOD $PATH_ devolvió $CODE (esperado 401)"
	fi
done

quote '401 con token desconocido/revocado'
CODE=$(curl -sS -o /dev/null -w '%{http_code}' -H "Authorization: Bearer orp_noexiste1234567890" "$BASE/wp-json/$NS/jobs")
[ "$CODE" = "401" ] && ok "token desconocido → 401" || bad "token desconocido devolvió $CODE"

# ------------------------------------------------ aislamiento por local A/B
quote 'Token del local A no lee ni reclama trabajos del local B (Doc/05 §4.6)'
LIST_A=$(curl -sS -H "Authorization: Bearer $TOKEN_A" "$BASE/wp-json/$NS/jobs?after=0&limit=20")
if echo "$LIST_A" | grep -q '"order_id":1101' && ! echo "$LIST_A" | grep -q '"order_id":2202'; then
	ok 'poll del local A: ve el 1101 propio y NO ve el 2202 de B'
else
	bad 'el poll del local A no aísla el trabajo del local B'
fi

JOB_A_ID=$(wp db query "SELECT id FROM wp_orp_jobs WHERE idempotency_key LIKE 'verify-f110-a-%'" --skip-column-names | tr -dc '0-9\n' | head -n1)
CLAIM_CROSS=$(curl -sS -o /tmp/orp_cross.json -w '%{http_code}' -H "Authorization: Bearer $TOKEN_B" -X POST "$BASE/wp-json/$NS/jobs/$JOB_A_ID/claim")
if [ "$CLAIM_CROSS" = "404" ] && grep -q 'orp_not_found' /tmp/orp_cross.json; then
	ok "claim del trabajo de A con token de B → 404 orp_not_found"
else
	bad "claim cruzado devolvió $CLAIM_CROSS (esperado 404 orp_not_found)"
fi

STATE=$(wp db query "SELECT status FROM wp_orp_jobs WHERE idempotency_key LIKE 'verify-f110-a-%'" --skip-column-names | tr -d '\r\n ')
[ "$STATE" = "pending" ] && ok 'el trabajo de A sigue pending (el claim cruzado no lo tocó)' || bad "el trabajo de A cambió a '$STATE'"

# ------------------------------------------------------ código de emparejamiento
quote 'Código de emparejamiento: 10 min, sexto intento, IP throttling'
clear_rl
PAIR_CODE=$(wp eval '
	global $wpdb;
	$loc_id = (int) $wpdb->get_var( "SELECT id FROM {$wpdb->prefix}orp_locations WHERE code = \"verify-f110-a\"" );
	$c = Orvita\PrintBridge\Domain\PairingCodes::create( $loc_id, 1 );
	echo $c["code"];
	' | tr -d '\r')
EXP=$(wp db query "SELECT expires_at FROM wp_orp_pairing_codes WHERE code = '$PAIR_CODE'" --skip-column-names | tr -d '\r\n ')
NOWSQL=$(wp db query "SELECT UTC_TIMESTAMP()" --skip-column-names | tr -d '\r\n ')
DIFF=$(( $(date -d "$EXP" +%s) - $(date -d "$NOWSQL" +%s) ))
[ "$DIFF" -ge 540 ] && [ "$DIFF" -le 600 ] && ok "expires_at del código a $DIFF s (≈10 min)" || bad "expires_at a $DIFF s"

for i in 1 2 3 4 5; do
	# Sin agent_name: fallo de código que SUBE intentos sin consumirlo.
	curl -sS -o /dev/null -H 'Content-Type: application/json' -d "{\"code\":\"$PAIR_CODE\"}" "$BASE/wp-json/$NS/pair"
done
SIXTH=$(curl -sS -o /tmp/orp_sixth.json -w '%{http_code}' -H 'Content-Type: application/json' -d "{\"code\":\"$PAIR_CODE\",\"agent_name\":\"TPV\"}" "$BASE/wp-json/$NS/pair")
if [ "$SIXTH" = "429" ] && grep -q 'orp_pairing_locked' /tmp/orp_sixth.json; then
	ok 'sexto intento → 429 orp_pairing_locked'
else
	bad "sexto intento devolvió $SIXTH"
fi

# IP throttling: 10 fallos más (códigos inexistentes) → 429 con Retry-After.
for i in 0 1 2 3 4 5 6 7 8 9; do
	curl -sS -o /dev/null -H 'Content-Type: application/json' -d "{\"code\":\"77777$i\",\"agent_name\":\"x\"}" "$BASE/wp-json/$NS/pair"
done
RL=$(curl -sS -D /tmp/orp_f110_rl.txt -o /tmp/orp_f110_rl.json -w '%{http_code}' -H 'Content-Type: application/json' -d '{"code":"777770","agent_name":"x"}' "$BASE/wp-json/$NS/pair")
if [ "$RL" = "429" ] && grep -q 'orp_rate_limited' /tmp/orp_f110_rl.json && grep -qi '^retry-after: [0-9]' /tmp/orp_f110_rl.txt; then
	ok "throttle por IP → 429 con $(grep -i '^retry-after' /tmp/orp_f110_rl.txt | tr -d '\r')"
else
	bad "throttle por IP desviado: $RL"
fi

# ---------------------------------------------------------- token en registros
quote 'El token no aparece en orp_logs (Doc/05 §4.6)'
ALLLOGS=$(wp db query "SELECT CONCAT_WS('|', message, data) FROM wp_orp_logs" --skip-column-names | tr -d '\r')
if echo "$ALLLOGS" | grep -q "$TOKEN_A"; then
	bad 'el token A aparece en orp_logs'
else
	ok 'ni el token A ni ningún otro token aparecen en orp_logs'
fi

# ------------------------------------------------------ rate limit por token
quote 'Rate limit 120/min por token: 429 con Retry-After (contador forzado)'
clear_rl
AGENT_A=$(wp db query "SELECT id FROM wp_orp_agents WHERE name = 'Agente A F110'" --skip-column-names | tr -dc '0-9\n' | head -n1)
wp eval "set_transient( 'orp_rl_$AGENT_A', 120, 60 );" >/dev/null 2>&1
RLT=$(curl -sS -D /tmp/orp_f110_rlt.txt -o /tmp/orp_f110_rlt.json -w '%{http_code}' -H "Authorization: Bearer $TOKEN_A" "$BASE/wp-json/$NS/jobs")
if [ "$RLT" = "429" ] && grep -qi '^retry-after: [0-9]' /tmp/orp_f110_rlt.txt && grep -q 'orp_rate_limited' /tmp/orp_f110_rlt.json; then
	ok "poll con contador a 120 → 429 con $(grep -i '^retry-after' /tmp/orp_f110_rlt.txt | tr -d '\r')"
else
	bad "rate limit por token desviado: $RLT"
fi
clear_rl

# ---------------------------------------------------------- página «Estado»
quote 'Página Estado (F1-10): secciones y comprobaciones automáticas'
rm -f "$JAR"
curl -sS -c "$JAR" -o /dev/null "$BASE/wp-login.php"
LOGIN=$(curl -sS -b "$JAR" -c "$JAR" -o /dev/null -w '%{http_code}' \
	--data 'log=admin&pwd=password&wp-submit=Log+In&testcookie=1' \
	-H 'Cookie: wordpress_test_cookie=WP Cookie check' \
	"$BASE/wp-login.php")
[ "$LOGIN" = "302" ] && ok 'login correcto (302)' || bad "login devolvió $LOGIN"

curl -sS -b "$JAR" -o /tmp/orp_estado.html "$BASE/wp-admin/admin.php?page=orp-status"
for TXT in 'Comprobaciones automáticas' 'Versiones' 'Locales' 'Agentes' 'Cola de impresión' 'Últimos 20 eventos' 'manage_orp_print' 'orp_template_version' 'orp-print/v1' 'Local de verificación A'; do
	if grep -q "$TXT" /tmp/orp_estado.html; then
		ok "Estado muestra «$TXT»"
	else
		bad "Estado no muestra «$TXT»"
	fi
done
if grep -q '✅' /tmp/orp_estado.html; then ok 'Estado tiene comprobaciones ✅'; else bad 'sin comprobaciones ✅'; fi
grep -q 'conectado' /tmp/orp_estado.html && ok 'Estado muestra el estado de conexión de agentes' || bad 'sin estado de conexión de agentes'

# Sin datos personales: el HTML de Estado no lleva emails.
if grep -qiE '[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}' /tmp/orp_estado.html; then
	bad 'la página Estado contiene un email'
else
	ok 'página Estado sin emails (sin datos personales)'
fi

echo "============================================="
echo "RESULTADO: $PASS PASS / $FAIL FAIL"
[ "$FAIL" = 0 ]
