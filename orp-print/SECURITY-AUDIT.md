# Auditoría de seguridad — Orvita Print (F1-12)

> Verificación contra la matriz de `Doc/05` §4.6, el criterio de aceptación de
> `Doc/05` §3 («sin comprobación de licencia ni llamadas a servidores de
> Orvita») y las decisiones S-01…S-05 de `Doc/07` §4.
>
> Fecha: 2026-11-23 · Suite PHPUnit: **109 tests / 509 aserciones OK** ·
> Verificación dinámica HTTP: **tools/_verify_f1_10_12_13.sh — 30 PASS / 0 FAIL**.

## Matriz de `05` §4.6

| # | Criterio | Estado | Evidencia |
|---|---|---|---|
| 1 | Sin token → 401 en todos los endpoints protegidos | ✅ | HTTP real sobre wp-env: `GET /jobs`, `POST /jobs/1/claim`, `POST /jobs/1/ack`, `POST /jobs/1/release`, `POST /heartbeat`, `GET /config` → **401 `orp_invalid_token`** en los seis (guion `_verify_f1_10_12_13.sh`, sección «401 sin token»). Solo `/health` y `/pair` son públicos por contrato (`Doc/04` §5.1–5.2). Token desconocido → 401; token revocado → 401 `orp_token_revoked` (`Auth::check`, `src/Rest/Auth.php:105-107`). |
| 2 | Token del local A no lee ni reclama del local B | ✅ | Dinámico: dos locales sembrados con agente y trabajo cada uno. El poll del token A devuelve el trabajo propio (order 1101) y **no** el de B (order 2202); `POST /jobs/{id_de_A}/claim` con token de B → **404 `orp_not_found`** y el trabajo de A sigue `pending`. El aislamiento está en TODAS las consultas de `JobsController` (`WHERE … location_id = %d`, `find()`, `pending_for_location()`, `pending_count()`) y el `location_id` SIEMPRE sale del token (`Auth::agent()`), nunca del cuerpo. El 404 evita revelar la existencia del trabajo a otro local. |
| 3 | Código de emparejamiento caduca a 10 minutos | ✅ | `PairingCodes::TTL_MINUTES = 10` (`src/Domain/PairingCodes.php:24`); `create()` fija `expires_at = Clock::sql_offset_minutes(10)`. Dinámico: `expires_at` de un código recién creado está a **600 s** de `UTC_TIMESTAMP()` (comprobado en el guion). Caducado → **410 `orp_pairing_expired`** (`PairController::handle`, comprobación `(string) $row->expires_at <= $now`) y prueba PHPUnit `PairTest::test_expired_code_returns_410`. El cotejo es contra el reloj del servidor (UTC). |
| 4 | Sexto intento fallido bloquea el código | ✅ | `PairingCodes::MAX_ATTEMPTS = 5`; `bump_attempts()` usa `LEAST(attempts+1, 5)` (sin desbordar el TINYINT bajo fuerza bruta). El check `attempts >= MAX_ATTEMPTS` va antes de procesar el cuerpo → **429 `orp_pairing_locked`**. Dinámico: 5 fallos sin `agent_name` (cada uno sube `attempts`) y el sexto → 429. Pruebas: `PairTest::test_sixth_attempt_locks_the_code` (y verifica que no se creó ningún agente). |
| 5 | Rate limit responde 429 y el agente hace backoff | ✅ | Por token: 120 peticiones/min en transient `orp_rl_<agent_id>` con ventana que NO se reinicia (`Auth::rate_limit`, `src/Rest/Auth.php:204-229`); 429 `orp_rate_limited` con cabecera **`Retry-After`** añadida en `rest_post_dispatch` (`Auth::add_retry_after_header`) — dinámico: contador forzado a 120 → 429 con `Retry-After: 54`. Emparejamiento (S-02): 10 fallos/hora por IP en transient `orp_pair_rl_<md5(ip)>` → 429 con `Retry-After` aunque el código sea válido; el check va ANTES de leer el cuerpo (predicable). Dinámico: **429 con `Retry-After: 3568`**. El agente aplica backoff 10→20→40→60 s y se detiene en 401 (lado agente, F2-02 verificado en su fase). |
| 6 | El token no aparece en ningún registro | ✅ | Estático: los 24 call sites de `Logger::log` auditados (`grep Logger::log src/`); ninguno recibe el token. `/pair` registra solo `agent_name`, `location_id` y `token_prefix` — el prefijo de 8 caracteres es localizador público, ya vive en claro en `orp_agents` (`PairController.php:194-205`). `Auth::generate_token()` devuelve el token SOLO en memoria; en BD va su SHA-256 + prefijo. Las respuestas de error 401/429 no incluyen el token recibido. `heartbeat` no registra el token; los mensajes de excepción no lo componen (no hay `throw` con datos del request en `src/`). Dinámico: tras un emparejamiento real por HTTP, `SELECT message, data FROM orp_logs` no contiene ningún `orp_…` de token. Prueba: `PairTest::test_token_never_reaches_the_logs`. |
| 7 | HTTP plano rechazado | ✅ (lado agente, S-03) | Decisión de `Doc/07` §4: S-03 se implementa en el **agente** (F2-09): el CLI valida HTTPS antes de guardar `ServerUrl` en SQLite; HTTP solo con modo desarrollo explícito (verificado en su fase). El lado servidor no puede forzarlo (el piloto corre en un dominio real con TLS en el hosting; WordPress lo sirve con el esquema que reciba). **Criterio de `05` §4.6 cubierto por el componente que toca**; en el servidor no hay nada que rechazar sin romper wp-admin por HTTP en desarrollo. |

## Sin licencia ni llamadas externas (criterio `05` §3)

| Comprobación | Resultado |
|---|---|
| `grep -E 'wp_remote|file_get_contents\(|https?://'` sobre `orp-print.php`, `uninstall.php` y `src/` | **Limpio**: cero llamadas HTTP salientes en el código del plugin. Los únicos usos de `file_get_contents` leen un adjunto LOCAL de la biblioteca de medios para generar el logo (`DocumentBuilder.php:246`). |
| URLs presentes | Solo metadatos declarativos: `Plugin URI: https://orvitaprint.example` (marcador de posición pendiente del dominio comercial, Doc/00) y `License URI` de gnu.org. **Ninguna URL se contacta en tiempo de ejecución.** |
| Comprobación de licencia | No existe: sin opciones de licencia, sin validación offline, sin telemetría (`Doc/03` §11: sin telemetría remota en V1). El servicio de pago es el agente de Windows, pieza EXTERNA al plugin; su licencia vive en el agente (F3-02). |
| `tools/simulator.php` | Herramienta de desarrollo, no se empaqueta para producción; solo contacta la URL que se le pasa (`--url`, por defecto localhost). |

## Panel de administración

| Comprobación | Resultado |
|---|---|
| Permisos | Todas las páginas y acciones exigen `manage_orp_print` (`current_user_can` con `wp_die` o retorno temprano en SettingsPage/LocationsPage/JobsPage/TemplatePage/PreviewPage/StatusPage/Notices/OrderActions). La capacidad se concede al rol administrator en la activación (`Install/Activator`). |
| Nonces | `grep check_admin_referer` → 9 acciones cubiertas: cancelar y reencolar (Cola), guardar/borrar/activar/por-defecto/código-de-pair (Locales), preset e instancia (Ajustes) y plantilla. Las de fila van por enlace con `wp_nonce_url`; los formularios con `wp_nonce_field`. Las acciones de pedido pasan por el manejo nativo de WooCommerce (que protege con su propio nonce). |
| Sanitizado de entrada | Toda entrada pasa por `sanitize_key`/`sanitize_text_field`/`absint`/`(int)`; los acotados en `SettingsPage::sanitize_instance()` (probado: `poll_interval` 0 → 10, 99999 → 600; `paper_width_mm` 999 → 80; `retention_days` 999999 → 3650). El JSON de reglas se valida ANTES de guardar y los tipos/operadores van por lista blanca (`RuleMatcher`). `default_location_id` exige que el local exista. |
| Escape de salida | `esc_html`/`esc_attr`/`esc_url`/`esc_textarea`/`esc_js` en toda la interfaz (auditado página a página: StatusPage, JobsPage, LocationsPage, SettingsPage, TemplatePage, PreviewPage, Notices). La vista previa imprime a través de `TicketHtml`, que escapa el contenido dinámico (el JSON del documento nunca va crudo al HTML). El `onclick="window.print()"` de la vista previa es HTML estático. |
| SQL | Consultas propias con `$wpdb->prepare` en el 100 % de los casos con parámetros; identificadores de tabla interpolados solo de `$wpdb->prefix` + constantes. Los UPDATE de estado van acotados por `WHERE status IN (…)` o `claimed_by = %d` (idempotentes y sin carreras). |
| Datos personales | `orp_logs` registra solo nivel, contexto, job_id, agent_id y mensajes técnicos (sin nombres, emails ni direcciones). La página Estado no muestra datos personales (comprobado dinámicamente: sin emails en el HTML). Retención: trabajos `retention_days` (90), registros 30 días. |
| Rate limit del propio panel | Las acciones administrativas van tras login + capacidad + nonce; el riesgo de CSRF queda cubierto por el nonce y el de fuerza bruta de /pair por el throttling de IP + caducidad del código. |

## Recomendaciones de despliegue (fuera del código)

- Servir wp-admin y la REST API bajo HTTPS en producción (el agente, con S-03, ya lo exige).
- Si el hosting elimina la cabecera `Authorization`, el agente usa `X-Orp-Token` (respaldo ya implementado); comprobar en el servidor real del piloto (`Doc/06` §11).
- Vigilar `wp_cron` del reaper: la página Estado lo comprueba con ✅/⚠️; en hostings sin cron real, el lazy run de `rest_api_init` lo cubre mientras el agente haga poll.
