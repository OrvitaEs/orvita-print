# Manual de uso de Orvita Print (plugin `orp-print`)

Manual completo de **instalación, configuración y uso** del plugin de WordPress/WooCommerce `orp-print` (namespace PHP `Orvita\PrintBridge`, prefijo `orp_`).

- **Versión documentada:** 1.0.0 (F1 completo)
- **Licencia:** GPL v2 o posterior
- **Requisitos:** WordPress ≥ 6.8 · PHP ≥ 8.1 · WooCommerce (para pedidos)
- **Contrato REST congelado:** `wp-json/orp-print/v1` (ver `Doc/04-modelo-de-datos-y-contrato.md`)

---

## Índice

1. [¿Qué es Orvita Print?](#1-qué-es-orvita-print)
2. [Instalación](#2-instalación)
3. [Primeros pasos (guía rápida)](#3-primeros-pasos-guía-rápida)
4. [Configuración: Ajustes](#4-configuración-ajustes)
5. [Locales y reglas](#5-locales-y-reglas)
6. [Plantilla del ticket](#6-plantilla-del-ticket)
7. [Vista previa e impresión manual](#7-vista-previa-e-impresión-manual)
8. [Cola de impresión](#8-cola-de-impresión)
9. [Disparo automático de pedidos](#9-disparo-automático-de-pedidos)
10. [Reimpresión desde el pedido](#10-reimpresión-desde-el-pedido)
11. [Emparejamiento del agente de Windows](#11-emparejamiento-del-agente-de-windows)
12. [Página Estado (diagnóstico)](#12-página-estado-diagnóstico)
13. [Referencia de la API REST](#13-referencia-de-la-api-rest)
14. [Herramientas: WP-CLI y simulador](#14-herramientas-wp-cli-y-simulador)
15. [Mantenimiento, desinstalación y datos](#15-mantenimiento-desinstalación-y-datos)
16. [Resolución de problemas](#16-resolución-de-problemas)

---

## 1. ¿Qué es Orvita Print?

Orvita Print convierte cada pedido de WooCommerce en un **documento neutro** (JSON, versión 1) que se encola para el **local físico** que corresponda (tienda, barra, cocina…). Un **agente opcional para Windows** instalado en el TPV reclama esos trabajos vía HTTPS y los imprime en la impresora térmica.

**Sin el agente el plugin también es útil:** puedes configurar locales, diseñar la plantilla, previsualizar el ticket e imprimirlo a mano desde el navegador. No hay comprobación de licencia ni llamadas a servidores de Orvita.

**Arquitectura en una frase:** pedido → documento neutro → cola por local → agente (poll/claim/ack) → impresora térmica.

**Capacidades de la versión actual (capa «Producto»):**

- Un documento por pedido, de 1 a *n* salidas por local.
- Disparo al crear el pedido y/o al pasar a estados de pedido elegibles.
- Deduplicación por pedido o por disparo.
- Plantilla por campos seleccionables (logo, cliente, líneas, totales, QR, pie…).
- Cola con estados, reclamo atómico (dos agentes nunca imprimen el mismo trabajo), reconocimiento idempotente y caducidad.
- Emparejamiento de agentes con código de 6 dígitos.
- Registro técnico sin datos personales y página de diagnóstico.

---

## 2. Instalación

### 2.1 Instalación en producción

1. Sube la carpeta `orp-print` a `/wp-content/plugins/` (o instálala desde **Plugins → Añadir nuevo → Subir plugin** con el ZIP).
2. Activa el plugin en **Plugins**. Al activar:
   - Se crean las **6 tablas propias** con el prefijo de tu BD (`wp_orp_locations`, `wp_orp_agents`, `wp_orp_destinations`, `wp_orp_jobs`, `wp_orp_pairing_codes`, `wp_orp_logs`).
   - Se concede la capacidad **`manage_orp_print`** al rol *administrator* (única capacidad que protege todo el panel).
3. Verifica el menú **Orvita Print** en el lateral del administrador (icono de impresora).

### 2.2 Entorno de desarrollo (Docker, 5 comandos)

Desde la carpeta `orp-print/`:

```bash
# 1. Arrancar WordPress con el plugin activado (puerto 8888)
npx @wordpress/env start

# 2. Sembrar el entorno: 3 locales, 1 salida, 1 agente y 1 trabajo de prueba.
#    El TOKEN del agente se imprime en consola y solo se muestra una vez: cópialo.
npx @wordpress/env run cli wp orp seed

# 3. Comprobar la salud de la API (público, sin token)
curl -s http://localhost:8888/wp-json/orp-print/v1/health

# 4. Ejecutar el ciclo completo con el simulador:
#    heartbeat → poll → claim → documento en var/jobs/ → ack
php tools/simulator.php --url=http://localhost:8888 --token=TOKEN --once

# 5. Ver la configuración del documento que consume el agente
#    (sin token debe responder 401)
curl -s -H "Authorization: Bearer TOKEN" http://localhost:8888/wp-json/orp-print/v1/config
```

> Si no tienes PHP local, los comandos 4 y 5 también funcionan dentro del contenedor:
> `npx @wordpress/env run cli php /var/www/html/wp-content/plugins/orp-print/tools/simulator.php --url=http://localhost:8888 --token=TOKEN --once`

### 2.3 Compatibilidad HPOS

El plugin declara compatibilidad con **HPOS** (tablas de pedido propias de WooCommerce). Todos los accesos al pedido usan la API CRUD de WooCommerce, así que funciona igual con HPOS activado o desactivado. Puedes comprobarlo en **Orvita Print → Estado**.

---

## 3. Primeros pasos (guía rápida)

Tras instalar, este es el recorrido mínimo recomendado:

1. **Orvita Print → Ajustes** — elige un preset («Aviso» es el de una instalación nueva) y revisa los valores de instancia.
2. **Orvita Print → Locales y reglas** — crea al menos un local con sus reglas de asignación y márcalo como por defecto si quieres red de seguridad.
3. **Orvita Print → Plantilla del ticket** — activa/desactiva campos, escribe el pie y elige el logo.
4. **Orvita Print → Vista previa** — comprueba el ticket con datos de ejemplo y pruébalo con «Imprimir».
5. *(Opcional)* Instala el agente de Windows en el TPV y genera un **código de emparejamiento** en «Locales y reglas».
6. Haz un pedido de prueba y sigue su trabajo en **Orvita Print → Cola**.

---

## 4. Configuración: Ajustes

Página: **Orvita Print → Ajustes**. Los ajustes viven en la opción `orp_settings` de la BD; el panel es su único escritor.

### 4.1 Las tres capas

| Capa | Qué es | ¿Editable? |
|---|---|---|
| **1 · Producto** | Qué puede hacer esta versión del plugin (lista fija de capacidades) | No |
| **2 · Preset** | Combinación típica de ajustes; al aplicarla se escriben sus valores en la instancia | Sí (desplegable + «Aplicar preset») |
| **3 · Instancia** | Los valores concretos de tu instalación | Sí (formulario + «Guardar ajustes») |

Un preset **es configuración, no un modo del código**: después de aplicarlo puedes afinar la instancia a mano.

### 4.2 Presets disponibles

| Preset | Qué hace |
|---|---|
| **Aviso** *(por defecto)* | Lo más simple: un aviso por pedido en una salida, al crear el pedido. Plantilla mínima (título, fecha/hora, nº pedido, líneas, totales). |
| **Completo** | Ticket con todos los campos (logo, cliente, teléfono, dirección, QR, pie, nota «no factura») en una sola salida, al crear el pedido. |
| **Cocina + cliente** | Dos disparadores (creación + estado «En proceso»), un trabajo por cada disparo (`dedupe_scope: trigger`), corte completo y aviso sonoro. Las 2 salidas reales las aplica el agente reportándolas (en V1 se imprime la plantilla vigente única en todas las salidas). |
| **Personalizado** | Sin cambios automáticos: mandan los valores de instancia. |

### 4.3 Capa de instancia — todos los campos

**Disparo y deduplicación**

| Campo | Valores | Descripción |
|---|---|---|
| Disparadores | `created` (fijo) + estados de WooCommerce | El disparo por **creación del pedido** está siempre activo. Puedes añadir «Pasar al estado…» para cualquiera de los estados de pedido de WooCommerce (p. ej. `processing`, `on-hold`). |
| Deduplicación | `order` / `trigger` | **Un solo aviso por pedido** (aunque cambie de estado dos veces) o **un trabajo por cada disparo** (reimprime al pasar de estado). |
| Local por defecto | un local o «Ninguno» | Red de seguridad: si ninguna regla coincide, el pedido va aquí y se muestra un aviso en el panel. Con «Ninguno», los pedidos sin regla no se encolan (solo se registran en el log). Se sincroniza con la marca «Por defecto» de la página Locales. |

**Documento y agente** (su cambio sube `orp_config_version`; el agente re-sincroniza solo)

| Campo | Valores | Por defecto |
|---|---|---|
| Ancho de papel | `80 mm` / `58 mm` | 80 |
| Codificación | `CP850` / `CP858` / `CP437` | CP850 |
| Modo de corte | Corte parcial / completo | Parcial |
| Abrir cajón | Sí/No | No |
| Intervalo de poll | 1–600 segundos | 10 s (el agente aplica ±20 % de jitter) |
| Aviso sonoro | Sí/No — suena al haber trabajo pendiente | No |

**Cola y retención**

| Campo | Rango | Por defecto | Efecto |
|---|---|---|---|
| Caducidad de trabajos | 1–1440 min | 30 | Un trabajo `pending` caducado ya no se sirve al agente (HTTP 410); el Reaper lo marca `expired`. |
| Retención | 1–3650 días | 90 | Días que se conservan los trabajos entregados antes de la limpieza. |

> **Saneado automático:** los valores fuera de rango vuelven al límite o al defecto (p. ej. poll 0 → 10, ancho 999 → 80). La validación es server-side, con nonce y capacidad `manage_orp_print`.

---

## 5. Locales y reglas

Página: **Orvita Print → Locales y reglas**. Cada local es un destino físico (tienda, barra, cocina…) al que se asignan pedidos.

### 5.1 Crear o editar un local

| Campo | Reglas |
|---|---|
| **Código (canónico)** | Minúsculas, números, guiones y guiones bajos. Máx. 32 caracteres. **Único**. Es el código que puede usar una regla `order_meta` del checkout. |
| **Nombre** | Nombre visible (máx. 120 caracteres). |
| **Prioridad** | 0–65535. **Menor número = se evalúa antes**. |
| **Estado** | Local activo/inactivo. Un local inactivo no recibe pedidos. |
| **Local por defecto** | Solo puede haber uno. Destino de los pedidos que no coinciden con ninguna regla (con aviso en el panel). |
| **Reglas (JSON)** | Array JSON (ver 5.2). Se valida al guardar: JSON inválido, regla con tipo/operador/valores no admitidos o más de 20 reglas → error y no se guarda. |

**Acciones por fila:** Editar · Activar/Desactivar · Por defecto · Generar código de emparejamiento · Borrar (borra el local **junto con sus salidas y agentes**; pide confirmación).

> **Importante:** las reglas se leen **al crear el trabajo**. Cambiarlas después no altera los trabajos ya encolados.

### 5.2 Reglas de asignación (JSON)

Todas las reglas del local deben cumplirse (AND). Tipos y operadores admitidos:

| Tipo | Filtra por | Operadores válidos |
|---|---|---|
| `city` | Ciudad de envío | `in`, `equals`, `contains`, `prefix`, `exists` |
| `postcode` | Código postal | `in`, `equals`, `contains`, `prefix`, `exists` |
| `shipping_method` | Método de envío | `in`, `equals`, `contains`, `prefix`, `exists` |
| `product_category` | Categoría de producto | `in`, `equals`, `contains`, `prefix`, `exists` |
| `order_meta` | Metadato del pedido | `in`, `equals`, `contains`, `prefix`, `exists` |

- En `order_meta`, `values[0]` es la **clave del metadato**.
- El matching es tolerante con mayúsculas/acentos en la comparación (p. ej. «Alcalá» y «alcala» se listan como valores).

Ejemplo (local de Alcalá, envío local):

```json
[
  { "type": "city", "operator": "in", "values": ["Alcalá", "alcala"] },
  { "type": "postcode", "operator": "prefix", "values": ["28"] }
]
```

**Resolución del local de un pedido:** se evalúan los locales **activos por prioridad ascendente** y gana el primero cuyas reglas se cumplen. Si ninguno coincide → local por defecto (con aviso). Si no hay local por defecto → el pedido no se encola y el aviso lo indica.

### 5.3 Salidas (destinos) por local

La tabla inferior **es de solo lectura**: cada salida la reporta el agente del TPV en su heartbeat (impresora, rol, etiqueta, copias y estado). Cada pedido genera **un trabajo por salida activa** del local, con las copias congeladas al crearlo.

Avisos automáticos del panel:

- ⚠️ Dos salidas activas del mismo local apuntan a la **misma impresora** → usa el número de copias de la salida, no salidas repetidas.
- ℹ️ Un local **sin salidas activas** no generará trabajos hasta que el agente reporte una.

Una salida que el agente dejó de reportar se muestra como **«sin confirmar»** y se conserva (no se borra): la baja real la da el agente reportándola con `enabled = 0`.

---

## 6. Plantilla del ticket

Página: **Orvita Print → Plantilla del ticket**. La plantilla vive en la opción `orp_template` y alimenta **las dos salidas**: el documento que imprime el agente y la vista previa imprimible del navegador.

### 6.1 Campos activables (en este orden en el ticket)

| Campo | Contenido |
|---|---|
| Logo | PNG generado desde un adjunto de la biblioteca de medios (máx. 384 px de ancho, fondo blanco). Sin logo configurado, el bloque no se emite. |
| Título del negocio | Nombre comercial. |
| Fecha y hora | Del pedido. |
| Nº de pedido | Número de pedido de WooCommerce. |
| Cliente | Nombre del cliente. |
| Teléfono | Teléfono de facturación/envío. |
| Dirección de entrega | Dirección de envío. |
| Líneas de producto | Artículos, cantidades y precios. |
| Totales | Subtotales, envío, impuestos y total. |
| Código QR | QR del pedido. |
| Pie personalizado | Texto libre (por defecto «Gracias por su pedido»). |
| Nota «No válido como factura» | Aviso legal impreso al pie. |

El preset elegido en Ajustes marca los valores por defecto («Aviso» = 5 campos mínimos; «Completo» = todos).

### 6.2 Guardar y versionado

- Al guardar con cambios se incrementa **`orp_template_version`**, que entra en la **clave de idempotencia**: los pedidos ya impresos **no se duplican** al cambiar la plantilla.
- El cambio queda registrado en la tabla de logs y visible en la propia página (**Registro de cambios**: versión vigente, último cambio y los 5 últimos eventos).
- Los trabajos ya encolados conservan el documento con el que se crearon.

> La misma plantilla alimenta la vista previa: usa **«Ver vista previa con datos de ejemplo»** para validar el texto antes de imprimir en producción.

---

## 7. Vista previa e impresión manual

Página: **Orvita Print → Vista previa** (submenú; también botón «Ver ticket» en los pedidos).

- **Con datos de ejemplo:** desde la página de plantilla o el submenú. No requiere WooCommerce activo ni pedidos.
- **Con un pedido real:** botón **«Ver ticket»** en la fila del pedido (lista de pedidos de WooCommerce) o acción «Ver ticket (Orvita Print)» en la ficha del pedido → abre `admin.php?page=orp-preview&order_id=…`.

Características:

- HTML generado por el **mismo DocumentBuilder** que alimenta la cola: lo que ves es lo que se imprime.
- Se sirve **limpia** (sin chrome de wp-admin y sin recursos externos) para que **Imprimir → window.print()** salga a 80/58 mm sin romper el diseño.
- **NO crea trabajo en la cola**: la impresión desde el navegador es acción manual.

---

## 8. Cola de impresión

Página: **Orvita Print → Cola**. Cada fila es **un documento para una impresora**: un trabajo por pedido y salida.

### 8.1 Estados de un trabajo

| Estado | Significado |
|---|---|
| `pending` | En espera de ser reclamado por el agente. |
| `claimed` | Reclamado por un agente (lease de 120 s). |
| `delivered` | El agente confirmó la impresión (ack). |
| `failed` | El agente reportó un error de impresión. |
| `expired` | Caducó sin entregarse (el Reaper lo marca; ya no se sirve). |
| `cancelled` | Anulado desde el panel; no se imprimirá. |

### 8.2 Cabecera de agentes

Tabla «Agentes por local»: agente **conectado** (último latido < 3 min) o **desconectado**, estado revocado, versión y salidas reportadas. Las salidas que el último latido ya no incluye se marcan **«sin confirmar»**.

### 8.3 Filtros y vista de documento

- Filtros por **estado**, **local**, **fecha de creación** (en tu zona horaria; la BD va en UTC) y **trabajos por página** (5–100, por defecto 20).
- Al hacer clic en un trabajo se abre su **documento neutro completo** (JSON bonito, solo lectura) con estado, pedido, salida, copias congeladas, intentos, último error, fechas de creación/caducidad/entrega.

### 8.4 Acciones operativas

| Acción | Desde → hasta | Detalle |
|---|---|---|
| **Cancelar** | `pending`/`claimed` → `cancelled` | Lo entregado o fallado no se toca («el papel no se desimprime»). |
| **Reencolar** | `failed`/`expired` → `pending` | Restaura **la misma fila**: intentos a 0, caducidad nueva, mismo `idempotency_key`. No duplica nada y conserva el documento original. |

> Para **reimprimir con el documento regenerado** (plantilla actual) usa «Reimprimir ticket» en la ficha del pedido (sección 10).

---

## 9. Disparo automático de pedidos

Cuando se dispara un pedido (creación o estado elegido en Ajustes), el plugin:

1. **Resuelve el local** con las reglas (sección 5.2).
2. Aplica la **deduplicación**: `order` = un solo trabajo por pedido; `trigger` = uno por cada disparo.
3. Crea **un trabajo por cada salida activa** del local (fan-out), con copias congeladas.
4. Registra el resultado en `orp_logs` (sin datos personales).

Detalles a saber:

- Funciona con **checkout clásico y de bloques** y con HPOS.
- El disparo por creación siempre está activo (el piloto nace en `on-hold` con COD: el disparador correcto es la creación).
- Si el pedido no coincide con ninguna regla: local por defecto + aviso; sin local por defecto, no se encola (queda en el log y aviso en el panel).
- Sin salidas activas en el local, no se crean trabajos (la cola no crece sin agente); se sugiere imprimir desde el navegador.

---

## 10. Reimpresión desde el pedido

En la ficha de cada pedido de WooCommerce (desplegable «Acciones del pedido»):

| Acción | Efecto |
|---|---|
| **Ver ticket (Orvita Print)** | Abre la vista previa imprimible del pedido. **No crea trabajo.** |
| **Reimprimir ticket** | **Crea trabajo nuevo** para cada salida activa del local del pedido, con clave de idempotencia `sha256(order_id \| manual \| role \| timestamp \| user_id)`: las reimpresiones manuales nunca se deduplican entre sí. El resultado se muestra en un aviso del panel. |

Si el local no tiene salidas activas, no se crea ningún trabajo y se sugiere imprimir desde el navegador («Ver ticket»).

---

## 11. Emparejamiento del agente de Windows

La impresión automática requiere el **agente de Orvita Print para Windows** (software propietario, pago único, se instala en el TPV). El flujo de emparejamiento no usa tokens a mano: usa un **código de 6 dígitos**.

### 11.1 Generar el código (lado WordPress)

1. Ve a **Orvita Print → Locales y reglas**.
2. En la fila del local, pulsa **«Generar código»** (columna «Emparejamiento»).
3. El panel muestra el código y su hora de caducidad. Mientras esté vigente, también aparece como «código vigente» en la tabla.

Propiedades de seguridad del código: vida de **10 minutos**, **un solo uso**, máximo **5 intentos**, válido solo para su local y un agente. Throttling por IP: 10 fallos/hora → HTTP 429 con `Retry-After`.

### 11.2 Emparejar el agente (lado TPV)

En el PC del TPV, con el agente instalado:

```
orvitaprint pair --server <URL-del-tienda> <código-de-6-dígitos>
```

El agente llama a `POST /pair` (público, sin token), recibe su **token Bearer** —que se muestra **una sola vez**— y queda vinculado a ese local. En la BD solo queda el hash SHA-256 del token más un prefijo de 8 caracteres para identificarlo; el token nunca aparece en los logs.

### 11.3 Tras el emparejamiento

- El agente reporta sus **salidas** (impresoras, roles, etiquetas, copias) en cada **heartbeat**; aparecen en «Locales y reglas» y en la cabecera de la Cola.
- El ciclo operativo es: **heartbeat** (estado + `pending_count` + `config_version`) → **poll** de trabajos pendientes → **claim** atómico de uno → impresión → **ack** (o **release** si no puede imprimirlo).
- Un agente puede ser **revocado** desde la BD (`status='revoked'`); dejará de autenticarse (HTTP 401 `orp_token_revoked`).
- Puedes probar todo el ciclo sin agente real con `tools/simulator.php` (sección 14.2).

---

## 12. Página Estado (diagnóstico)

Página: **Orvita Print → Estado**. Solo lectura, sin formularios y sin datos personales: es lo primero que se pide a un administrador cuando «algo no imprime».

Incluye:

- **Comprobaciones automáticas (✅/⚠️):** WooCommerce activo (y versión), HPOS activado/desactivado, cron del Reaper programado (próxima ejecución), extensión GD de PHP (necesaria para el logo), y otras comprobaciones del entorno.
- **Versiones:** plugin, API, WordPress, WooCommerce, PHP, HPOS, `config_version` y `template_version`.
- **Locales** con sus reglas resumidas («ciudad in [Alcalá, alcala] · código postal prefix [28]»).
- **Agentes** con último latido, versión y salidas.
- **Resumen de la cola** por estado.
- **Últimos 20 eventos** de `orp_logs` con nivel y contexto (retención: 30 días).

---

## 13. Referencia de la API REST

Namespace: **`orp-print/v1`** (contrato congelado; detalles completos en `Doc/04` §5).

### 13.1 Autenticación

- `Authorization: Bearer <token>` con respaldo en la cabecera **`X-Orp-Token`**.
- Todos los endpoints la exigen **excepto** `GET /health` y `POST /pair`.
- El token se guarda solo como SHA-256 + prefijo de 8 caracteres.
- **Rate limit por token: 120 peticiones/minuto.** Al superarlas: HTTP 429 con cabecera `Retry-After`.

### 13.2 Endpoints

| Método y ruta | Auth | Descripción |
|---|---|---|
| `GET /health` | No | Salud y versiones: `ok`, `plugin_version`, `api_version`, `woocommerce`, `hpos`, `server_time`. |
| `POST /pair` | No | Emparejamiento con código de 6 dígitos. Devuelve el token **una sola vez**. Errores: 400 `orp_pairing_invalid`, 410 `orp_pairing_expired`, 429 `orp_pairing_locked`. |
| `GET /jobs?after=<id>&limit=<n>` | Sí | **Poll** con cursor: trabajos `pending` del local del token. `limit` máx. 20 (por defecto 10). Devuelve `jobs` (con el documento neutro) y `cursor`. |
| `POST /jobs/{id}/claim` | Sí | **Reclamo atómico**: 200 con el trabajo · 409 `orp_already_claimed` (otro agente lo cogió) · 410 `orp_job_expired` · 404 `orp_not_found` (no es del local). Lease de 120 s. |
| `POST /jobs/{id}/ack` | Sí | **Reconocimiento idempotente** de impresión: repite el POST y la respuesta mantiene `job_status`. |
| `POST /jobs/{id}/release` | Sí | El agente devuelve el trabajo que no puede imprimir. |
| `POST /heartbeat` | Sí | El agente reporta `agent_version`, `platform`, `destinations` (upsert de salidas) y `last_error`. Responde `ok`, `server_time`, `pending_count`, `config_version`. |
| `GET /config` | Sí | Configuración del documento: `config_version`, `paper_width_mm`, `encoding`, `cut_mode`, `open_drawer`, `poll_interval`. Las impresoras/copias no viajan aquí: las define el agente. |

### 13.3 Códigos de error HTTP

| HTTP | Código | Cuándo |
|---|---|---|
| 401 | `orp_invalid_token` / `orp_token_revoked` | Token ausente, inválido o revocado. |
| 400 | `orp_invalid_payload` / `orp_pairing_invalid` | Cuerpo inválido o código de emparejamiento malformado/desconocido/usado. |
| 404 | `orp_not_found` | Trabajo inexistente o de otro local. |
| 409 | `orp_already_claimed` | Otro agente ganó el reclamo. |
| 410 | `orp_job_expired` / `orp_pairing_expired` | Trabajo o código caducado. |
| 429 | `orp_rate_limited` / `orp_pairing_locked` | Límite de 120/min (con `Retry-After`) o 5 intentos del código. |

### 13.4 Convenciones de datos

- La **base de datos va en UTC**; las fechas del contrato salen en **ISO-8601** con la zona horaria del sitio (`wp_date()`).
- Los cuerpos de error 409/410 de claim son el JSON literal `{"error": …}` del contrato (parseables por el agente sin adaptaciones).
- Un cambio de esquema sube `orp_db_version` y añade su migración; nunca se edita una ya publicada.

---

## 14. Herramientas: WP-CLI y simulador

### 14.1 `wp orp seed`

Deja un entorno de pruebas listo en un comando: **3 locales** (`local-centro`, `local-norte`, `local-sur`), **1 agente** con salida y **1 trabajo de prueba**.

```bash
wp orp seed           # siembra (el token se imprime UNA vez en consola)
wp orp seed --reset   # borra la siembra anterior y la regenera
```

Notas: si la siembra ya existe, avisa y no hace nada (usa `--reset`). Al regenerar, el token anterior queda revocado al borrar el agente.

### 14.2 Simulador de agente

`tools/simulator.php` ejecuta el ciclo completo de un agente sin dependencias:

```bash
php tools/simulator.php --url=http://localhost:8888 --token=TOKEN --once
# con fallo simulado:
php tools/simulator.php --url=... --token=TOKEN --once --fail=orp_printer_not_found
```

Útil para verificar: heartbeat → poll → claim → documento en `var/jobs/` → ack; el 409 con dos agentes a la vez; el 410 con trabajos caducados; el 401 con token revocado; y el respaldo de cabecera `X-Orp-Token`.

### 14.3 Pruebas PHPUnit

109 tests / 509 aserciones (esquema, reclamo atómico, idempotencia, cursor, matching de reglas, disparo con dedupe, plantilla, pair, reaper, acciones de cola y saneado de ajustes):

```bash
composer install
WP_TESTS_DIR=/ruta/a/wordpress-tests-lib vendor/bin/phpunit
# o con wp-env:
npx @wordpress/env run tests-cli bash tools/_t_phpunit.sh
```

---

## 15. Mantenimiento, desinstalación y datos

### 15.1 Reaper (limpieza automática)

Cron propio `orp_reaper_hourly` (más una ejecución perezosa cada 5 min en cargas admin) que:

- Marca `expired` los `pending` con caducidad pasada y avisa en el panel con el recuento (el aviso se limpia al entrar en la Cola, donde puedes reencolar).
- Limpia los trabajos entregados superada la **retención** configurada.
- Purga los **logs** con más de **30 días**.
- Detecta **agentes desconectados** (último latido > 3 min).

El cron se reprograma al reactivar el plugin o con `Reaper::ensure_schedule()`; se elimina al desactivar.

### 15.2 Desactivación vs. desinstalación

- **Desactivar** el plugin elimina el cron programado pero **conserva todos los datos**.
- **Desinstalar** (borrar) el plugin, por defecto **no borra nada**: trabajos, locales, agentes y logs se conservan por si se reinstala.
- Borrado total: activa antes la clave `uninstall_purge` (en `orp_settings`), que elimina las 6 tablas `orp_*`, las opciones (`orp_db_version`, `orp_settings`, `orp_template`, `orp_template_version`, `orp_config_version`, `orp_expired_unread`), la capacidad `manage_orp_print` y los transients del rate limit.

### 15.3 Privacidad

El registro técnico (`orp_logs`) **no contiene datos personales** (el token nunca aparece; los heartbeats no se registran). La página Estado no muestra datos personales.

---

## 16. Resolución de problemas

| Síntoma | Dónde mirar / qué hacer |
|---|---|
| **No imprime nada** | Abre **Orvita Print → Estado** y revisa las comprobaciones ✅/⚠️. Después, la **Cola**: ¿hay trabajos `pending`? ¿El agente aparece «conectado» con salidas activas? |
| El pedido no se encoló | ¿Coincidía alguna regla? Sin regla → local por defecto (con aviso) o nada si no hay defecto. Aviso en el panel y registro en el log. Cambiar reglas no afecta a trabajos ya encolados. |
| Trabajo `expired` | La caducidad (`job_expiry_minutes`) pasó sin entrega. El agente no reclama caducados (410). Usa **Reencolar** en la Cola para darle caducidad nueva. |
| Trabajo `failed` | Revisa el «Último error» en la vista del documento y el log del agente. Reencola tras corregir. |
| Duplicados temidos | No deberían ocurrir: claim atómico + ack idempotente + idempotency_key. Comprueba `dedupe_scope` en Ajustes si ves un trabajo por cada cambio de estado y solo quieres uno por pedido. |
| El agente no aparece | Genera un código de emparejamiento vigente y repite `orvitaprint pair --server <URL> <código>`. El token solo se muestra una vez. |
| Salida «sin confirmar» | El último heartbeat del agente ya no incluye esa salida. Revisa la impresora en el TPV; la baja la da el agente reportándola `enabled = 0`. |
| Error 401 del agente | Token revocado o borrado (`orp_token_revoked`). Vuelve a emparejar el local. |
| Error 429 | Rate limit (120 req/min por token) o 5 intentos del código de emparejamiento; espera el `Retry-After` (15 min en el código bloqueado). |
| Aviso «dos salidas, misma impresora» | Usa el campo **copias** de una sola salida en vez de dos salidas repetidas. |
| Logo no sale en el ticket | Configura el adjunto en **Plantilla del ticket**; requiere la **extensión GD** de PHP (comprobación en Estado). |
| Dos locales con reglas solapadas | Gana el de **menor prioridad numérica**. Revisa prioridades y valores de las reglas. |
| Reset del entorno de pruebas | `wp orp seed --reset` (regenera locales, agente y trabajo; el token anterior queda revocado). |

---

## Anexo A. Tablas de base de datos creadas

| Tabla | Contenido |
|---|---|
| `orp_locations` | Locales: código, nombre, activado, prioridad, reglas JSON, marca de defecto. |
| `orp_agents` | Agentes emparejados: nombre, estado (`active`/`revoked`), token (SHA-256 + prefijo), versión, plataforma, `last_seen_at`, local. |
| `orp_destinations` | Salidas reportadas por el agente: local, rol, etiqueta, impresora, copias, activada. |
| `orp_jobs` | Cola: pedido, local, salida (`printer_role`), copias, estado, intentos, payload (documento neutro), `idempotency_key`, fechas. |
| `orp_pairing_codes` | Códigos de emparejamiento: 6 dígitos, caducidad, intentos. |
| `orp_logs` | Registro técnico sin datos personales (retención 30 días). |

Opciones en `wp_options`: `orp_settings` (ajustes), `orp_template` (plantilla), `orp_template_version`, `orp_config_version`, `orp_db_version`, `orp_expired_unread`.

## Anexo B. Referencia rápida de presets

| Clave | Disparadores | Dedupe | Destacados |
|---|---|---|---|
| `aviso` | `created` | `order` | Plantilla mínima; corte parcial; sin cajón ni sonido. |
| `completo` | `created` | `order` | Todos los campos de plantilla activados. |
| `cocina_cliente` | `created`, `processing` | `trigger` | Corte completo, aviso sonoro; las 2 salidas las reporta el agente. |
| `personalizado` | — | — | No toca nada: los valores de instancia mandan. |
