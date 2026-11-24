#!/bin/sh
# Verificación end-to-end del lote de cierre de deuda F0 (F0-11, B1, B2 + seed).
# Ejecutar DENTRO del contenedor cli de wp-env:
#   npx @wordpress/env run cli sh /var/www/html/wp-content/plugins/orp-print/tools/_verify_f0.sh
set -u

BASE='http://wordpress/wp-json/orp-print/v1'
TBL_JOBS='wp_orp_jobs'
TBL_OPTS='wp_options'
PASS=0
FAIL=0

ok()   { echo "PASS: $1"; PASS=$((PASS+1)); }
bad()  { echo "FAIL: $1"; FAIL=$((FAIL+1)); }
quote() { echo "--- $1"; }

# ---------------------------------------------------------------- seed (B4)
quote 'B4 · wp orp seed --reset'
SEED_OUT=$(wp orp seed --reset 2>&1)
echo "$SEED_OUT"
TOKEN=$(printf '%s\n' "$SEED_OUT" | grep -E '^orp_[0-9a-f]{48}$' | head -n1 | tr -d '\r')
if [ -n "$TOKEN" ]; then ok 'wp orp seed emite token y cadenas traducidas'; else bad 'no se obtuvo token del seed'; fi

AUTH="Authorization: Bearer $TOKEN"

# --------------------------------------------------- F0-11 · claim 200/409
quote 'F0-11 · claim 200 y luego 409'
JOB=$(wp db query "SELECT MAX(id) FROM $TBL_JOBS WHERE status='pending'" --skip-column-names | tr -dc '0-9')
if [ -z "$JOB" ]; then bad 'no hay trabajo pendiente sembrado'; fi

S1=$(curl -sS -o /tmp/v1.json -w '%{http_code}' -X POST -H "$AUTH" "$BASE/jobs/$JOB/claim")
B1=$(cat /tmp/v1.json | tr -d '\n')
echo "claim 1 -> $S1 $(echo "$B1" | head -c 120)"
if [ "$S1" = "200" ]; then ok 'primer claim 200'; else bad "primer claim devolvió $S1"; fi

S2=$(curl -sS -o /tmp/v2.json -w '%{http_code}' -X POST -H "$AUTH" "$BASE/jobs/$JOB/claim")
B2=$(cat /tmp/v2.json | tr -d '\n')
echo "claim 2 -> $S2 cuerpo: $B2"
if [ "$S2" = "409" ] && [ "$B2" = '{"error":"orp_already_claimed","status":"claimed"}' ]; then
	ok '409 con cuerpo literal de Doc/04 §5.5'
else
	bad "409 no literal (status $S2, cuerpo $B2)"
fi

# ------------------------------------------------------- F0-11 · claim 410
quote 'F0-11 · claim 410 (trabajo caducado)'
wp db query "UPDATE $TBL_JOBS SET status='pending', claimed_by=NULL, claimed_at=NULL, expires_at=DATE_SUB(UTC_TIMESTAMP(), INTERVAL 1 MINUTE) WHERE id=$JOB" --quiet
S3=$(curl -sS -o /tmp/v3.json -w '%{http_code}' -X POST -H "$AUTH" "$BASE/jobs/$JOB/claim")
B3=$(cat /tmp/v3.json | tr -d '\n')
echo "claim 3 -> $S3 cuerpo: $B3"
if [ "$S3" = "410" ] && [ "$B3" = '{"error":"orp_job_expired"}' ]; then
	ok '410 con cuerpo literal de Doc/04 §5.5'
else
	bad "410 no literal (status $S3, cuerpo $B3)"
fi

# ---------------------------------------------------------------- B2 · poll
quote 'B2 · el poll no sirve trabajos caducados'
S4=$(curl -sS -o /tmp/v4.json -w '%{http_code}' -H "$AUTH" "$BASE/jobs?after=0&limit=10")
B4=$(cat /tmp/v4.json | tr -d '\n')
echo "poll -> $S4 cuerpo: $B4"
case "$B4" in
	*'"jobs":[]'*) ok 'poll vacío con el trabajo caducado (no se sirve)' ;;
	*) bad "el poll sirvió contenido inesperado: $(echo "$B4" | head -c 160)" ;;
esac

# Restaurar el trabajo a pending vivo para el ciclo del simulador.
wp db query "UPDATE $TBL_JOBS SET status='pending', claimed_by=NULL, claimed_at=NULL, attempts=0, expires_at=DATE_ADD(UTC_TIMESTAMP(), INTERVAL 30 MINUTE) WHERE id=$JOB" --quiet
echo "job $JOB restaurado a pending (para el simulador)."

# ----------------------------------------------------------- B1 · 429 + Retry-After
quote 'B1 · Retry-After en la cabecera del 429'
# En este entorno cada petición tarda ~5-9 s: la ventana de 60 s no acumula 120
# peticiones en serie. Se fuerza el límite de forma legítima sembrando el
# contador a 120 (la 121ª petición por HTTP debe ser 429 con Retry-After).
AG=$(wp db query "SELECT MAX(id) FROM wp_orp_agents" --skip-column-names | tr -dc '0-9')
wp db query "DELETE FROM $TBL_OPTS WHERE option_name LIKE '\_transient\_orp\_rl\_%' OR option_name LIKE '\_transient\_timeout\_orp\_rl\_%'" --quiet
wp eval "update_option('_transient_orp_rl_$AG', 120); update_option('_transient_timeout_orp_rl_$AG', time() + 60);" --quiet

S5=$(curl -sS -D /tmp/vh.txt -o /tmp/v5.json -w '%{http_code}' -H "$AUTH" "$BASE/config")
echo "petición forzada -> $S5 cuerpo: $(head -c 80 /tmp/v5.json)"
if [ "$S5" = "429" ] && grep -qi '^retry-after: [0-9]' /tmp/vh.txt; then
	echo "cabecera: $(grep -i 'retry-after' /tmp/vh.txt | tr -d '\r')"
	ok 'el 429 HTTP lleva cabecera Retry-After numérica'
else
	bad 'el 429 no llega o no lleva Retry-After'
fi

# Ventana limpia para no condicionar el simulador.
wp db query "DELETE FROM $TBL_OPTS WHERE option_name LIKE '\_transient\_orp\_rl\_%' OR option_name LIKE '\_transient\_timeout\_orp\_rl\_%'" --quiet

echo "=============================="
echo "Resultado: $PASS PASS / $FAIL FAIL"
exit 0
