# Planificación técnica y tareas

> Orvita Print · v1.0 · Noviembre 2026
> Este documento convierte las fases (`05`) y las horas (`02`) en una lista de tareas con identificador, estimación y entregable. No sustituye a los demás documentos: los referencia.

| Si necesitas… | Ve a |
|---|---|
| El contrato REST y el documento neutro | `04-modelo-de-datos-y-contrato.md` |
| ADRs, seguridad, despliegue | `03-arquitectura-tecnica.md` |
| Criterios de aceptación y matriz de pruebas | `05-plan-de-ejecucion-y-pruebas.md` |
| Entorno, estructura del plugin y definición de hecho de F0 | `06-guia-de-trabajo-f0.md` |
| Horas totales, coste y calendario | `02-costes-y-planificacion.md` |
| Convenciones de ramas y commits | `06` §12 |

**Regla de oro:** el contrato (`04`) está congelado. Si algo del contrato parece mal, se discute **antes** de escribir código, no durante.

---

## 1. Decisiones ratificadas en esta revisión

Nuevas o modificadas respecto a la v1.0 de los docs. Ya están aplicadas en `03` y `04`.

| ID | Decisión | Referencia |
|---|---|---|
| ADR-09 (revisado) | El agente es un **servicio headless**: funcional y configurable por sí solo, sin pantallas | `03` §4 |
| ADR-12 | Núcleo portable (sin referencias a Windows); spooler/enumeración en proyecto separado tras `IPrintTransport`. V1 se prueba y soporta **solo Windows**, pero multiplataforma se decide al codificar | `03` §4, §6.1 |
| ADR-13 | Gestión por **CLI sobre named pipe**. La GUI futura (estado tipo bandeja) será otra cliente de la misma pipe; en el backlog | `03` §4, §6.1; `05` §8 |
| Config · servidor | Todo lo configurable por el usuario vive en **BD** (`wp_options`, tablas propias), editable desde el panel. Nada hardcoded | `04` §2.7 |
| Config · agente | Tres capas: `appsettings.json` (solo infra: rutas, log) · SQLite local (`ServerUrl`, token DPAPI, destinos) · `GET /config` remoto (documento) | `03` §6.2.1 |
| Config · `ServerUrl` | Va en **SQLite**, no en appsettings; la establece `orvitaprint pair --server`. Un cambio de transporte (directo → relay) es un dato, no una reinstalación | `03` §6.2.1, §7 |
| Seguridad | Token del agente protegido con **DPAPI** (nunca JSON plano). `POST /pair` con **throttling por IP** además del límite por código | `03` §8; tareas S-01/S-02 |
| Agente · dominio de impresión | `Orvita.Print.Printing.Windows` aísla `winspool.drv`, GDI y enumeración WMI | `03` §6.1 |

---

## 2. Piezas de código

### 2.1 Plugin WordPress (`orp-print/`)

Estructura en `06` §6. Namespace `Orvita\PrintBridge`, prefijo `orp_`, PHP 8.1+, HPOS declarado, sin dependencias de Composer en producción.

### 2.2 Agente (.NET 10, solución única)

| Proyecto | Tipo | Contenido |
|---|---|---|
| `Orvita.Print.Contracts` | lib | Modelos del contrato: documento neutro v1, `JobBatch`, `JobResult`, códigos de error |
| `Orvita.Print.Core` | lib (portable) | `ITransport`, `DirectTransport` (HttpClient), poller con backoff/jitter, cola SQLite, renderizador ESC/POS, validación de licencia. **Sin referencias a Windows** |
| `Orvita.Print.Printing.Windows` | lib | `IPrintTransport`: spooler RAW (P/Invoke), fallback GDI, TCP 9100, enumeración `Win32_Printer` |
| `Orvita.Print.Service` | exe | Worker + `UseWindowsService()`, heartbeat, pipe de gestión (servidor) |
| `Orvita.Print.Cli` | exe | Comando `orvitaprint`: cliente de la pipe de gestión |

Dependencia prohibida: `Core` no puede referenciar `Printing.Windows` ni nada de `System.Drawing.Printing` en su API pública; la selección del transporte se inyecta en el arranque del `Service`.

---

## 3. Tareas por fase

Estimaciones en horas asistidas, alineadas con `02` §3. Los totales por fase cuadran con su calendario.

### F0 · Contrato y simulador — 5 h · 2 días

> Detalle operativo y definición de hecho: `06` §7 y §10.

| ID | Tarea | Entregable | Est. |
|---|---|---|---|
| F0-01 | Esqueleto del plugin: cabecera, constantes, autoload PSR-4, capacidad `manage_orp_print` | Plugin activable sin avisos | 1,5 |
| F0-02 | Esquema con `dbDelta`: 6 tablas + `orp_db_version` + opciones por defecto + `uninstall.php` | Tablas creadas, no duplicadas al reactivar | — |
| F0-03 | Autenticación: Bearer + respaldo `X-Orp-Token`, rate limit por transients | `/config` responde 401 sin token | 1 |
| F0-04 | `GET /health` público | 200 sin token | — |
| F0-05 | `GET /jobs?after&limit`, `POST /jobs/{id}/claim` (atómico), `/ack` (idempotente), `/release` | Segundo agente recibe 409 | 1,5 |
| F0-06 | `POST /heartbeat` (guarda `last_seen_at` y salidas) + `GET /config` | `config_version` visible | — |
| F0-07 | `DocumentBuilder` v1 (bloques base) → `payload` | Documento neutro en BD | 0,5 |
| F0-08 | `wp orp seed`: 3 locales, 1 agente, 1 trabajo | Entorno listo en un comando | 0,25 |
| F0-09 | `tools/simulator.php`: poll→claim→ack, `--once`, `--fail` | Contrato probado sin impresora | 0,75 |
| F0-10 | README: arrancar y probar en 5 comandos | — | — |
| F0-11 | Alinear los cuerpos de error 409/410 al formato literal del contrato (`{"error":…}`) en vez del envoltorio estándar de `WP_Error` | El agente F2 parsea la forma de `04` sin adaptaciones | 0,5 |

**DoD F0:** checklist completa de `06` §10 (incluye: dos simuladores a la vez → un solo claim; ack repetido idempotente; caducado → 410; token revocado → 401; cursor avanza aunque falle).

**Estado F0 (2026-11-20):** F0-01…F0-10 **implementados** en `orp-print/` (24 ficheros) y **verificados estáticamente** por el tester (contrato literal, esquema campo a campo, seguridad verificable, convenciones). Entorno wp-env montado (Docker: WordPress 7.1.5, WooCommerce 11.3.1, HPOS activo). **Verificación dinámica (DoD `06` §10) ejecutada**: activación sin avisos ✅, `/health` 200 sin token ✅, `/config` 401 sin token ✅, ciclo completo del simulador ✅, doble claim concurrente → un solo ganador + 409 ✅, `ack` idempotente ✅, caducado → 410 ✅, token revocado → 401 ✅, cursor avanza tras fallo ✅, rate limit **120 ok → 429 desde la petición 121** ✅ (in-process; confirmación HTTP end-to-end en curso).

**Bugs cazados por la verificación dinámica (corregidos el 2026-11-20):**
- **Crítico:** `Auth::find_by_hash()` usaba `$row instanceof object` — `object` no es una clase en PHP; ningún token validaba nunca. Corregido.
- **Crítico:** `WP_REST_Request::set_attribute()` no existe en WordPress — fatal error 500 tras autenticar. Sustituido por propiedad estática por-petición en `Auth`. Corregido.

Hallazgos estáticos de la ronda 1: M1 → cerrado con F0-11; M2 → desviación documentada en `04` §4 (reloj PHP UTC); M3 → ya agendado como F1-09 (reaper/lease). Menores pendientes (al primer lote de F1): `Retry-After` vía headers de respuesta (B1), filtrar caducados en el poll (B2), prueba de idempotencia de `JobFactory` (B3), cadenas de `SeedCommand` sin traducir + mojibake en `.pot` (B4), upsert no atómico de destinos (B5), limpieza de capacidad y transients en `uninstall.php` (B6), simulador no filtra por salidas (B7), `--fail` sin argumento registra `error_code` vacío (B8). **Bug 3 encontrado en la verificación HTTP del rate limit (corregido el 2026-11-20):** WordPress reinvoca el `permission_callback` para la cabecera `Allow` (`rest_send_allow_header`), así que `Auth::check()` corría dos veces por petición y el límite real era 60/min; corregido con retorno temprano si el agente ya está resuelto en la petición (regla añadida a `06` §11). Pendiente de decisión: HTTP plano rechazado (S-03, lado servidor). Nota de entorno: el bind-mount de OneDrive a Docker añade latencia por petición (TTFB variable 0,3–3,3 s) — no afecta a la corrección, solo a la velocidad de las pruebas.

### F1 · Plugin completo — 82 h · 1 semana

> Criterios de aceptación: `05` §3 (Plugin). Pregunta previa no técnica: ¿quién valida la plantilla? (`06` §13).

| ID | Tarea | Entregable | Est. |
|---|---|---|---|
| F1-01 | Panel de administración: menú, `SettingsPage` en 3 capas (producto/preset/instancia), los 4 presets | Ajustes persistentes en `orp_settings` | 10 |
| F1-02 | Locales y reglas: alta, `RuleMatcher` (city, postcode, shipping_method, product_category, order_meta), prioridad, local por defecto + aviso `admin_notices` | Pedido → `location_id` | 8 |
| F1-03 | Plantilla: `TemplateSettings` (selección de campos) + `DocumentBuilder` completo (todos los bloques de `04` §7.2) + `orp_template_version` | Documento fiel a la plantilla | 10 |
| F1-04 | Vista previa HTML imprimible (80/58 mm, `@page`, sin recursos externos) en admin y ficha del pedido + impresión manual `window.print()` **sin crear trabajo** | Plugin útil sin agente | 8 |
| F1-05 | Disparo: `woocommerce_new_order`, `woocommerce_store_api_checkout_order_processed`, estados configurables; dedupe por `idempotency_key` con `INSERT IGNORE` y `dedupe_scope` | Exactamente un trabajo por pedido | 6 |
| F1-06 | Fan-out: un trabajo por salida activa del local, `copies` congeladas, cancelación de pendientes al desactivar salida | Multi-salida correcto | 12 |
| F1-07 | `POST /pair`: código de 6 dígitos, 10 min, 5 intentos, lock 15 min, **throttling por IP (S-02)**, token mostrado una vez | Emparejamiento seguro desde el panel | 5 |
| F1-08 | `JobsPage`: cola con estados, cancelar, reencolar; acción «Reimprimir ticket» en el pedido | Panel operativo | 9 |
| F1-09 | `Reaper`: lease de 120 s, `expired`, limpieza 30/90 días | Sin trabajos zombis | 4 |
| F1-10 | Página de diagnóstico: versión, HPOS, agentes vistos, errores | Soporte facilitado | 3 |
| F1-11 | i18n completo (`orp-print`, español), `readme.txt` con sección de servicio, GPL v2+ | Listo para WordPress.org | 3 |
| F1-12 | Seguridad del plugin: rate limit 120/min, permisos `orp_manage_print`, validación TLS asumida del lado agente | `05` §4.6 sin sorpresas | 3 |
| F1-13 | PHPUnit: humo de esquema, claim atómico, idempotencia, cursor | Suite verde | 1 |
| F1-14 | Colchón de bugs | — | 2 |

**DoD F1:** todos los criterios de la sección «Plugin» de `05` §3 en verde.

### F2 · Agente e impresión — 78 h · 1,5 semanas

> Criterios: `05` §3 (Agente) y §3 (Contrato). Las pruebas físicas de esta fase son el hito H1.

| ID | Tarea | Entregable | Est. |
|---|---|---|---|
| F2-01 | Solución y `Contracts`: modelos del documento neutro, `JobBatch`, `JobResult`, códigos de error (`04` §6) | Compila, modelos con tests de serialización | 3 |
| F2-02 | `DirectTransport` (HttpClient, Bearer) + poller: 10 s ± 20 %, backoff 10→20→40→60, parada en 401 | Contrato consumido íntegro | 5 |
| F2-03 | Cola local SQLite: `received→printing→done/error`; arranque con trabajo en `printing` → reimprime + `duplicate_suspected` | Recuperación tras corte | 5 |
| F2-04 | Renderizador ESC/POS: todos los bloques, CP850/858/437 con sustitución segura, corte, cajón, logo raster, QR, barcode | Cadena «ñ, €, áéíóú» correcta en las 3 tablas | 14 |
| F2-05 | `Printing.Windows`: spooler RAW (P/Invoke `StartDocPrinter`), fallback GDI, TCP 9100 | Ticket físico en Epson y Xprinter | 10 |
| F2-06 | Enumeración de impresoras (`Win32_Printer`), estado como pista, nombre exacto guardado | Sin impresora → texto explícito | 6 |
| F2-07 | `Service`: Worker + `UseWindowsService()`, arranque automático, heartbeat 60 s con salidas, re-sincronización por `config_version` | Imprime tras reiniciar sin sesión | 5 |
| F2-08 | Pipe de gestión (servidor): operaciones `pair`, `destination`, `status`, `test`, `doctor` | El servicio es gestionable sin UI | 6 |
| F2-09 | `Orvita.Print.Cli`: subcomandos de la pipe, salidas claras en español, códigos de salida para scripts | `orvitaprint` completo | 6 |
| F2-10 | Almacén local: esquema SQLite (`ServerUrl`, token **DPAPI (S-01)**, destinos, cola) | Nada secreto en JSON plano | 4 |
| F2-11 | Actualización automática: `latest.json`, firma **Ed25519 del manifiesto**, hash SHA-256 del MSI, rollback conservando la versión actual | Update sin Authenticode, seguro | 7 |
| F2-12 | `orvitaprint doctor --zip`: logs (7 días), config sin secretos, versiones, estado | Diagnóstico exportable | 3 |
| F2-13 | Colchón de bugs | — | 4 |

**DoD F2:** criterios «Agente» y «Contrato» de `05` §3 en verde + H1 (tres impresoras de la matriz §4.4 imprimen bien).

### F3 · Instalador y licencia — 20 h · 3 días

| ID | Tarea | Entregable | Est. |
|---|---|---|---|
| F3-01 | MSI (WiX): instalación por máquina, registro del servicio auto-start, permisos de `ProgramData` | `msiexec /qn` operativo | 8 |
| F3-02 | Licencia: generador (firma ECDSA P-256), `license.dat`, validación offline con clave pública embebida | Los 3 SKU funcionan | 6 |
| F3-03 | Endpoint de actualizaciones estático (`latest.json`) y verificación hash + firma | Auto-update seguro | 3 |
| F3-04 | Guía de instalación con capturas (incluye el aviso de SmartScreen) + publicación del SHA-256 del MSI | Instalable por un profesional | 3 |

**DoD F3:** instalación en máquina limpia sin pasos externos; actualización fallida conserva la versión anterior.

### F4 · QA y despliegue del piloto — 20 h · 1 semana + 30 días

| ID | Tarea | Entregable | Est. |
|---|---|---|---|
| F4-01 | Ejecutar la matriz funcional y de resiliencia (`05` §4.1–4.3, §4.7) en WordPress 6.8/7.1 × Woo 9.5/11.2 × PHP 8.1/8.3 × HPOS on/off | Matriz documentada | 8 |
| F4-02 | Sesión 0 y TPV reales: arranque sin sesión, corte de luz, spooler, drivers de las 3 impresoras | H2 y H3 superados | 6 |
| F4-03 | Despliegue en la pizzería según `05` §5: plugin + 3 locales + reglas `city`, plantilla **validada por el cliente**, agente piloto, **email de respaldo activo** | 1 TPV imprimiendo | 6 |

**Regla del piloto:** el reloj de 30 días empieza el día que los tres TPV imprimen con la versión candidata (`02` §9.4). El email antiguo no se retira hasta 14 días limpios.

### F5 · Empaquetado comercial — 10 h · 3 días

| ID | Tarea | Entregable | Est. |
|---|---|---|---|
| F5-01 | Landing + descarga (subdominio + Cloudflare Pages/R2), los 3 SKU vía MoR (Lemon Squeezy/Paddle), términos con cláusula de 12 meses | Se puede cobrar | 4 |
| F5-02 | Generación de licencias por venta + entrega del MSI con SHA-256 | Compra → licencia → descarga | 2 |
| F5-03 | Checklist de release completa (`05` §7): versiones alineadas, CHANGELOG, documento validado contra esquema, plan de vuelta atrás | Release package | 2 |
| F5-04 | Publicación en WordPress.org (readme, banners, SVN) | Canal abierto, no dependiente | 2 |

---

## 4. Seguridad: tareas transversales

Nuevas en esta revisión; ya reflejadas en `03` §8. Se implementan en la fase indicada y se verifican con `05` §4.6.

| ID | Tarea | Fase |
|---|---|---|
| S-01 | Token del agente protegido con DPAPI (`ProtectedData`, ámbito `LocalMachine`); en Linux/macOS futuros, permisos `600` + dueño del servicio. Nunca en JSON plano | F2-10 |
| S-02 | `POST /pair`: además del límite por código (5 intentos), throttling por IP (10 fallos/hora → 429 con `Retry-After`) | F1-07 |
| S-03 | HTTPS obligatorio: el CLI valida la URL antes de guardarla en SQLite; HTTP solo con modo desarrollo explícito | F2-09 |
| S-04 | Rate limit 120 peticiones/min por token en transients; 429 con `Retry-After` | F1-12 |
| S-05 | Auditoría de secretos: el token no aparece en logs, ni en `doctor --zip`, ni en los registros del plugin | F2-12 |

---

## 5. Paralelismo y dependencias

```
F0 (2 días) ──► F1 (1 sem) ──► F3 puede solaparse con F2 ──► F4 ──► F5
   │
   └─ contrato congelado ──► F2 puede arrancar en paralelo a F1
        (Core y Contracts no dependen del panel; solo de `04`)
```

- **F2 puede empezar cuando F0 cierre.** `Core`, `Contracts` y el renderizador solo necesitan el contrato, no el plugin terminado.
- **F3 (MSI, licencia) es independiente de F2**: solo requiere que el `Service` exista para empaquetarlo. Puede solaparse con las pruebas físicas de F2.
- **Bloqueo duro:** F4 no empieza sin H1 (los tres modelos de impresora imprimen bien). F5 no empieza con 30 días de piloto limpios (`05` §1).

---

## 6. Hitos de control

| Hito | Criterio | Cierre de |
|---|---|---|
| H0 | Checklist `06` §10 completa | F0 |
| H1 | Las 3 impresoras de la matriz imprimen «Pizza Margarita · jamón, piña, ñ, €, áéíóú» sin errores, en CP850/858/437 | F2 |
| H2 | 100 pedidos de prueba sin duplicados ni pérdidas | F4 |
| H3 | Corte de luz simulado sin pérdida de ticket (duplicado aceptable, marcado) | F4 |
| H4 | 30 días en producción sin incidencias de impresión | F4 |

---

## 7. Seguimiento

- Una fila por tarea en esta tabla es el tablero: `pendiente → en curso → hecha`. La fecha real junto a la estimación alimenta la revisión de `02`.
- Commits referenciando la tarea: `feat(orprint): F0-05 claim atómico` (convenciones de `06` §12).
- Regla de alcance: **lo que no esté en este documento va al backlog de `05` §8**, no a la fase en curso.

---

## 8. Fuera de V1 (recordatorio)

Relay y multiplataforma en producción, Android/iOS, GUI del agente (backlog, ADR-13), plantillas por rol, PrestaShop/Shopify, Verifactu/TicketBAI como SIF integrado, email/WhatsApp al cliente final. Detalle y prioridades: `05` §8.
