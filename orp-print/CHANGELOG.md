# Changelog — Orvita Print (plugin)

Formato: [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) adaptado;
versiones semánticas. El plugin es GPL v2+; el changelog del **agente de
Windows** vive en su propio repositorio (`agent/`).

## [1.0.0] — 2026-11-23

### Añadido

- **Panel propio** («Orvita Print», capacidad `manage_orp_print`): Ajustes en
  3 capas (producto / preset / instancia), Locales y reglas, Plantilla del
  ticket, Vista previa, Cola y Estado.
- **Locales y reglas**: 5 tipos (city, postcode, shipping_method,
  product_category, order_meta) × 5 operadores (in, equals, contains, prefix,
  exists), prioridad ascendente, local por defecto único con aviso en el
  panel, JSON de reglas validado.
- **Plantilla del ticket**: 12 campos activables, logo PNG generado con GD
  (máx. 384 px, fondo blanco), pie personalizado; guardarla sube
  `orp_template_version` solo si cambia y lo registra en `orp_logs`.
- **Vista previa imprimible** 80/58 mm con `@page`, sin recursos externos y
  sin crear trabajo; botón «Ver ticket» en el listado y ficha del pedido.
- **Reimpresión manual** desde la ficha del pedido: trabajo nuevo con clave
  `sha256(order_id | manual | role | timestamp | user_id)`.
- **Disparo automático**: `woocommerce_new_order`, checkout clásico, checkout
  de bloques (`woocommerce_store_api_checkout_order_processed`) y cambio a los
  estados configurados; deduplicación por `idempotency_key` con
  `INSERT IGNORE` (ámbito `order` o `trigger`). Los borradores de la caja de
  bloques no generan trabajo.
- **Fan-out**: un trabajo por salida activa del local con copias congeladas;
  desactivar una salida cancela sus pendientes.
- **`POST /pair`**: código de 6 dígitos (10 min, 5 intentos), throttling por
  IP (10 fallos/hora → 429 con `Retry-After`), código consumido de forma
  atómica; el token se muestra una vez y solo guarda SHA-256 + prefijo.
- **Cola (JobsPage)**: filtros por estado/local/fecha, ver documento,
  cancelar (pending/claimed) y reencolar (failed/expired sobre la MISMA fila).
- **Reaper**: arrendamiento de 120 s, caducidad → `expired` con aviso
  acumulable, retención de trabajos (`retention_days`; 0 = no borrar nunca) y
  de registros (30 días); cron horario + ejecución perezosa como respaldo.
- **Página de Estado** (diagnóstico completo): comprobaciones automáticas
  (HPOS, cron del reaper, GD, permalinks, capacidad), versiones (plugin, API,
  WP, Woo, PHP, config_version, template_version), locales con reglas,
  agentes con último latido/versión/salidas, resumen de cola por estado y
  últimos 20 eventos. Sin datos personales.
- **API REST `orp-print/v1`** (contrato): `GET /health` (público),
  `POST /pair` (público), `GET /jobs`, `POST /jobs/{id}/claim|ack|release`,
  `POST /heartbeat`, `GET /config`. Autenticación Bearer con respaldo
  `X-Orp-Token`, límite 120 peticiones/min por token con `Retry-After`.
- **CLI**: `wp orp seed` para sembrar locales, agente y trabajo de prueba.
- **i18n**: text domain `orp-print`, todo el texto visible en español;
  `readme.txt` de WordPress.org que declara el servicio de pago con precios y
  términos enlazados.

### Seguridad

- Sin comprobación de licencia y sin llamadas a servidores de Orvita
  (criterio de aceptación; auditoría en `SECURITY-AUDIT.md`).
- Token del agente solo como hash SHA-256 + prefijo; nunca en registros.
- Permisos por capacidad en todas las páginas y acciones, nonces en todos los
  formularios y enlaces de acción, saneado de entrada y escapado de salida.

[1.0.0]: https://orvitaprint.example/
