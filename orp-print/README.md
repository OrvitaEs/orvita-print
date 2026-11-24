# Orvita Print

Plugin de WordPress/WooCommerce que convierte pedidos en **documentos neutros** que un agente del TPV reclama e imprime. Licencia GPL v2 o posterior.

- Namespace PHP: `Orvita\PrintBridge` · Prefijo: `orp_`
- Contrato REST congelado: `wp-json/orp-print/v1` (ver `../Doc/04-modelo-de-datos-y-contrato.md`)
- Estado actual: **F1 completo** — panel (Ajustes/Locales/Plantilla/Vista previa/Cola/Estado), disparo automático, `POST /pair`, Reaper, i18n y auditoría de seguridad. El agente de Windows y el instalador van por su cuenta (F2/F3).
- Documentos de release: `readme.txt` (WordPress.org), `CHANGELOG.md`, `SECURITY-AUDIT.md`, `RELEASE-CHECKLIST.md`.

## Arrancar y probar en 5 comandos

Desde la carpeta `orp-print/` (requiere Docker):

```bash
# 1. Arrancar WordPress con el plugin activado (puerto 8888)
npx @wordpress/env start

# 2. Sembrar el entorno: 3 locales, 1 salida, 1 agente y 1 trabajo de prueba.
#    El TOKEN del agente se imprime en consola y solo se muestra una vez: cópialo.
npx @wordpress/env run cli wp orp seed

# 3. Comprobar la salud de la API (público, sin token)
curl -s http://localhost:8888/wp-json/orp-print/v1/health

# 4. Ejecutar el ciclo completo con el simulador: heartbeat → poll → claim → documento en var/jobs/ → ack
php tools/simulator.php --url=http://localhost:8888 --token=TOKEN --once

# 5. Ver la configuración del documento que consume el agente (sin token debe responder 401)
curl -s -H "Authorization: Bearer TOKEN" http://localhost:8888/wp-json/orp-print/v1/config
```

Consejo: si no tienes PHP local, los comandos 4/5 también funcionan dentro del contenedor:
`npx @wordpress/env run cli php /var/www/html/wp-content/plugins/orp-print/tools/simulator.php --url=http://localhost:8888 --token=TOKEN --once`

## Qué probar además (definición de hecho de F0, `Doc/06` §10)

| Caso | Cómo |
|---|---|
| Segundo agente recibe 409 | Lanza dos simuladores a la vez; solo uno obtiene el trabajo |
| Ack repetido idempotente | Repite el POST de ack; la respuesta mantiene `job_status` |
| Trabajo caducado → 410 | Cambia `expires_at` al pasado con SQL y vuelve a hacer claim |
| Token revocado → 401 | `UPDATE wp_orp_agents SET status='revoked'` y reintenta |
| Fallo simulado | `php tools/simulator.php --url=... --token=TOKEN --once --fail=orp_printer_not_found` |
| Respaldo de cabecera | Envía el token en `X-Orp-Token` en lugar de `Authorization` |
| Regenerar la siembra | `npx @wordpress/env run cli wp orp seed --reset` |

## Estructura

```
orp-print/
├── orp-print.php              # cabecera GPL v2+, constantes, autoload, HPOS
├── uninstall.php              # no borra datos salvo orp_settings.uninstall_purge
├── src/
│   ├── Plugin.php             # autoloader PSR-4 propio y arranque
│   ├── Install/               # Activator (capacidad manage_orp_print) + Schema (dbDelta, 6 tablas)
│   ├── Rest/                  # Router (/health), Auth (Bearer + X-Orp-Token, rate limit 120/min),
│   │                          # JobsController (poll/claim/ack/release), Heartbeat, Config
│   ├── Domain/                # JobFactory (INSERT IGNORE + manual), DestinationRepository, LocationResolver
│   │                          # RuleMatcher, LocationRepository
│   ├── Template/              # TemplateSettings (campos del ticket), DocumentBuilder (bloques 04 §7.2),
│   │                          # TicketHtml (vista previa imprimible, sin recursos externos)
│   ├── Admin/                 # menú, Ajustes (3 capas), Locales y reglas, Plantilla, Vista previa,
│   │                          #   Cola (cancelar/reencolar), Estado (diagnóstico F1-10), acciones del pedido, avisos
│   ├── Cli/SeedCommand.php    # wp orp seed
│   └── Support/               # Clock (UTC → wp_date), Logger, Settings
├── tools/simulator.php        # simulador de agente sin dependencias
├── tests/                     # PHPUnit: 13 ficheros (esquema, claim, idempotencia, pair, reaper…)
└── languages/                 # traducciones (text domain orp-print)
```

## Pruebas PHPUnit

Necesitan la suite de pruebas de WordPress (109 tests / 509 aserciones: esquema, reclamo atómico, idempotencia, cursor, matching de reglas, disparo con dedupe, plantilla, pair, reaper, acciones de cola y saneado de ajustes):

```bash
composer install
WP_TESTS_DIR=/ruta/a/wordpress-tests-lib vendor/bin/phpunit
# o con wp-env:
npx @wordpress/env run tests-cli bash tools/_t_phpunit.sh
```

## Notas del contrato

- Autenticación: `Authorization: Bearer <token>` con respaldo `X-Orp-Token`. Salvo `/health`, todos los endpoints la exigen.
- El token solo se guarda como SHA-256 + prefijo de 8 caracteres; en F0 se obtiene con `wp orp seed`.
- Errores y estados HTTP según `Doc/04` §6: 401 `orp_invalid_token`/`orp_token_revoked`, 404 `orp_not_found`, 409 `orp_already_claimed`, 410 `orp_job_expired`, 429 `orp_rate_limited` (con `Retry-After`), 400 `orp_invalid_payload`.
- La base de datos va en UTC; las fechas del contrato salen en ISO-8601 con `wp_date()`.
- Un cambio de esquema sube `orp_db_version` y añade su migración; nunca se edita una ya publicada.
