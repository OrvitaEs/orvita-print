#!/bin/sh
# Prepara dos trabajos para verificar B7/B8: uno «cocina» (salida que el
# simulador no reporta) y uno «default» (su salida activa).
set -u

LOC=$(wp db query "SELECT id FROM wp_orp_locations WHERE code='local-centro' LIMIT 1" --skip-column-names | tr -dc '0-9')
echo "local-centro = $LOC"

wp db query "DELETE FROM wp_orp_jobs WHERE idempotency_key LIKE 'dbg-%'" --quiet

wp db query "INSERT INTO wp_orp_jobs (location_id, order_id, printer_role, copies, status, payload, idempotency_key, attempts, max_attempts, created_at, expires_at)
VALUES ($LOC, 0, 'cocina', 1, 'pending', '{\"version\":1,\"blocks\":[]}', 'dbg-cocina', 0, 5, UTC_TIMESTAMP(), DATE_ADD(UTC_TIMESTAMP(), INTERVAL 30 MINUTE)),
       ($LOC, 0, 'default', 1, 'pending', '{\"version\":1,\"blocks\":[]}', 'dbg-default', 0, 5, UTC_TIMESTAMP(), DATE_ADD(UTC_TIMESTAMP(), INTERVAL 30 MINUTE))" --quiet

wp db query "SELECT id, printer_role, status FROM wp_orp_jobs WHERE idempotency_key LIKE 'dbg-%' ORDER BY id" --skip-column-names
