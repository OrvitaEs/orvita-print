# Modelo de datos y contrato del agente

> Orvita Print · v1.0 · Noviembre 2026
> Este documento es la referencia vinculante entre el plugin y el agente. Cualquier cambio incompatible exige subir la versión de API.

---

## 1. Identificadores

| Elemento | Valor |
|---|---|
| Prefijo de tablas | `{$wpdb->prefix}orp_` |
| Prefijo de opciones | `orp_` |
| Namespace REST | `orp-print/v1` |
| Versión de API | `1` |
| Versión del documento neutro | `1` |

---

## 2. Tablas

### 2.1 `orp_locations`

```sql
CREATE TABLE {prefix}orp_locations (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code          VARCHAR(32)     NOT NULL,
  name          VARCHAR(120)    NOT NULL,
  enabled       TINYINT(1)      NOT NULL DEFAULT 1,
  priority      SMALLINT        NOT NULL DEFAULT 100,
  rules         LONGTEXT        NULL,          -- JSON
  is_default    TINYINT(1)      NOT NULL DEFAULT 0,
  created_at    DATETIME        NOT NULL,
  updated_at    DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY code (code),
  KEY enabled_priority (enabled, priority)
) {charset_collate};
```

**Reglas (`rules`):** array JSON evaluado por prioridad ascendente. El primer local que coincide gana. Si ninguno coincide, se usa el local con `is_default = 1`.

```json
[
  { "type": "city",             "operator": "in",       "values": ["Alcalá", "alcala"] },
  { "type": "postcode",         "operator": "prefix",   "values": ["28"] },
  { "type": "shipping_method",  "operator": "in",       "values": ["local_pickup:3"] },
  { "type": "product_category", "operator": "in",       "values": ["pizzas"] }
]
```

Tipos admitidos: `city`, `postcode`, `shipping_method`, `product_category`, `order_meta`.
Operadores: `in`, `equals`, `contains`, `prefix`, `exists`.

**Semántica ratificada:** un local coincide cuando **todas** sus reglas se cumplen (AND); el primer local que coincide por prioridad gana. Para `order_meta`, `values[0]` es la clave del meta (o un campo opcional `key`): `{"type": "order_meta", "operator": "in", "values": ["_shipping_zona", "Zona Norte"]}`.

**Orden recomendado de reglas:**

| Prioridad | Regla | Cuándo |
|---|---|---|
| 1 | `order_meta` con **código canónico** de local | El checkout tiene un selector propio |
| 2 | `city` de **lista cerrada** (radio o select) | El checkout limita las opciones; el valor es controlado |
| 3 | `postcode` (prefijo) | Desempatar locales de la misma ciudad |
| 4 | `city` de **texto libre**, normalizada | Respaldo cuando no hay lista cerrada |
| 5 | Local por defecto + aviso | Red de seguridad |

La ciudad no es el problema: **el texto libre sí**. Un radio con tres ciudades produce valores exactos y es una fuente de local perfectamente válida, aunque el valor sea el nombre. Un campo de texto abierto, en cambio, falla con acentos, variantes («Alcalá» / «Alcalá de Henares») y erratas. Si se puede elegir, mejor un código (`local-a`) que un nombre; si no, una lista cerrada es suficiente.

### 2.2 `orp_agents`

```sql
CREATE TABLE {prefix}orp_agents (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  location_id    BIGINT UNSIGNED NOT NULL,
  name           VARCHAR(120)    NOT NULL,
  token_hash     CHAR(64)        NOT NULL,
  token_prefix   CHAR(8)         NOT NULL,
  status         VARCHAR(16)     NOT NULL DEFAULT 'active',
  agent_version  VARCHAR(32)     NULL,
  platform       VARCHAR(64)     NULL,
  last_seen_at   DATETIME        NULL,
  created_at     DATETIME        NOT NULL,
  revoked_at     DATETIME        NULL,
  PRIMARY KEY (id),
  UNIQUE KEY token_hash (token_hash),
  KEY location_status (location_id, status),
  KEY last_seen (last_seen_at)
) {charset_collate};
```

`status`: `active` | `revoked`.

**Enlace local ↔ agente.** El local existe antes que el agente y no depende de él:

1. El plugin crea el local y genera un **código de emparejamiento asociado a ese `location_id`**.
2. El agente introduce el código; el plugin crea la fila en `orp_agents` con ese `location_id` y devuelve el token.
3. Todas las peticiones de ese agente quedan limitadas a su `location_id`: no ve trabajos de otros locales.

El agente **no elige local**: hereda el del código con el que se emparejó. Y la asignación del pedido al local es anterior e independiente: la hacen las reglas de `orp_locations` (lista cerrada, ciudad o código postal), no el agente.

Con varios locales, el flujo es el mismo repetido N veces: un local, un código, un agente. Si el WordPress ya organiza los locales con otro plugin, se crean los `orp_locations` con el mismo nombre y su regla de ciudad o código postal; **no se lee ni se sincroniza** el otro plugin.

### 2.3 `orp_pairing_codes`

```sql
CREATE TABLE {prefix}orp_pairing_codes (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code         CHAR(6)         NOT NULL,
  location_id  BIGINT UNSIGNED NOT NULL,
  attempts     TINYINT         NOT NULL DEFAULT 0,
  expires_at   DATETIME        NOT NULL,
  used_at      DATETIME        NULL,
  created_by   BIGINT UNSIGNED NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY code (code),
  KEY expires (expires_at)
) {charset_collate};
```

### 2.4 `orp_jobs`

```sql
CREATE TABLE {prefix}orp_jobs (
  id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  location_id      BIGINT UNSIGNED NOT NULL,
  order_id         BIGINT UNSIGNED NOT NULL,
  printer_role     VARCHAR(32)     NOT NULL DEFAULT 'default',
  copies           TINYINT UNSIGNED NOT NULL DEFAULT 1,
  status           VARCHAR(16)     NOT NULL DEFAULT 'pending',
  payload          LONGTEXT        NOT NULL,
  idempotency_key  CHAR(64)        NOT NULL,
  attempts         TINYINT         NOT NULL DEFAULT 0,
  max_attempts     TINYINT         NOT NULL DEFAULT 5,
  claimed_by       BIGINT UNSIGNED NULL,
  claimed_at       DATETIME        NULL,
  delivered_at     DATETIME        NULL,
  last_error       TEXT            NULL,
  created_at       DATETIME        NOT NULL,
  expires_at       DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY idempotency_key (idempotency_key),
  KEY location_status_id (location_id, status, id),
  KEY status_expires (status, expires_at),
  KEY order_id (order_id)
) {charset_collate};
```

`status`: `pending` | `claimed` | `delivered` | `failed` | `expired` | `cancelled`.

- `printer_role` identifica la **salida**: hay un trabajo por salida activa del local.
- `copies` es una **copia congelada** del valor de la salida al crear el trabajo. Cambiar las copias después no altera los trabajos ya encolados.
- El `payload` guarda el documento neutro renderizado; dos salidas del mismo pedido comparten documento, pero no trabajo ni `ack`.

### 2.5 `orp_destinations`

Cada **salida** es un destino físico: una impresora con su número de copias. El agente las registra; el plugin las usa para crear un trabajo por salida.

```sql
CREATE TABLE {prefix}orp_destinations (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  location_id   BIGINT UNSIGNED NOT NULL,
  role          VARCHAR(32)     NOT NULL,
  label         VARCHAR(120)    NOT NULL,
  printer_name  VARCHAR(255)    NOT NULL,
  copies        TINYINT UNSIGNED NOT NULL DEFAULT 1,
  enabled       TINYINT(1)      NOT NULL DEFAULT 1,
  updated_at    DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY location_role (location_id, role),
  KEY location_enabled (location_id, enabled)
) {charset_collate};
```

| Campo | Origen | Nota |
|---|---|---|
| `role` | Agente | Identificador estable (`default`, `cocina`, `cliente`…) |
| `label` | Agente | Nombre visible: «Mostrador», «Cocina», «Repartidor» |
| `printer_name` | Agente | Nombre exacto de Windows |
| `copies` | Agente | Veces que se imprime el documento en esa salida |
| `enabled` | Agente | Si está desactivada, el plugin no crea trabajos |

**V1 asume un agente por local.** Si en el futuro hay varios, la clave única pasa a incluir `agent_id`.

### 2.6 `orp_logs`

```sql
CREATE TABLE {prefix}orp_logs (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  created_at  DATETIME        NOT NULL,
  level       VARCHAR(16)     NOT NULL,
  context     VARCHAR(32)     NOT NULL,
  job_id      BIGINT UNSIGNED NULL,
  agent_id    BIGINT UNSIGNED NULL,
  message     VARCHAR(255)    NOT NULL,
  data        LONGTEXT        NULL,
  PRIMARY KEY (id),
  KEY created (created_at),
  KEY job (job_id)
) {charset_collate};
```

Retención: 30 días. Sin datos personales.

### 2.7 Opciones

| Opción | Contenido |
|---|---|
| `orp_db_version` | Versión del esquema para migraciones |
| `orp_settings` | Array con los ajustes de usuario (desglose abajo) |
| `orp_template_version` | Entero que se incrementa al guardar la plantilla |
| `orp_config_version` | Entero que se incrementa al cambiar cualquier ajuste que afecte al agente |

**Principio:** en el servidor nada va hardcoded. Todo lo configurable por el usuario vive en la base de datos (`wp_options` y tablas propias) y se edita desde el panel; el código solo define valores por defecto.

**Claves de `orp_settings`** (el panel es el único escritor; los cambios que afectan al agente incrementan `orp_config_version`):

| Clave | Valores | Por defecto |
|---|---|---|
| `preset` | `aviso` \| `completo` \| `cocina_cliente` \| `personalizado` | `aviso` |
| `trigger_states` | `["created"]` o estados concretos (`processing`, `on-hold`…) | `["created"]` |
| `dedupe_scope` | `order` \| `trigger` | `order` |
| `paper_width_mm` | 58 \| 80 | 80 |
| `encoding` | CP850 \| CP858 \| CP437 | CP850 |
| `cut_mode` | `partial` \| `full` | `partial` |
| `open_drawer` | bool | `false` |
| `poll_interval` | segundos | 10 |
| `sound_alert` | bool | `false` |
| `default_location_id` | id de `orp_locations` | — |
| `job_expiry_minutes` | minutos | 30 |
| `retention_days` | días | 90 |

Fuera de esta tabla quedan los datos dinámicos que no edita el usuario: reglas y locales (`orp_locations`), salidas reportadas por el agente (`orp_destinations`), tokens y trabajos. La plantilla del ticket es una sección propia del panel; al guardarla sube `orp_template_version`.

### 2.8 Roles de impresión y copias

> **Alcance V1:** un documento por pedido y de **1 a n salidas**. Todas las salidas imprimen el mismo documento. Las plantillas distintas por rol (cocina sin precios, resguardo de cliente) quedan para después.

Un **trabajo** representa un documento para **una salida**. Si el local tiene dos salidas, el pedido genera dos trabajos. Así cada salida tiene su propio estado, su propio `ack` y su propio reintento: si una impresora falla, la otra no se ve afectada.

| Rol | Documento | Precios | Datos del cliente | Uso |
|---|---|---|---|---|
| `default` | **Aviso de pedido** | Opcional | Sí | V1 |
| `cocina` | Comanda | No | No | Futuro |
| `barra` | Comanda filtrada por categoría | No | No | Futuro |
| `cliente` | Resguardo | Sí | Sí | Futuro |
| `etiqueta` | Etiqueta de pedido | No | Parcial | Futuro |

Reglas:

- **Misma impresora, varias copias** → una salida con `copies = 3`. Es un trabajo con `copies = 3`, no tres trabajos.
- **Varias impresoras** → varias salidas, una por impresora. Un trabajo por salida.
- **No crear dos salidas que apunten a la misma impresora.** El panel lo advierte: para eso están las copias.
- Si una salida está desactivada, no se crean trabajos para ella. Los trabajos ya pendientes se cancelan.
- Cambiar las copias afecta solo a trabajos nuevos. Los pendientes conservan las copias con las que se crearon.

---

## 3. Clave de idempotencia

```
idempotency_key = sha256( order_id | trigger | location_id | printer_role | template_version )
```

- `trigger`: `created` o el slug del estado (`processing`, `on-hold`…).
- `printer_role`: permite que cocina y cliente sean trabajos distintos del mismo pedido.
- Reimpresión manual: `sha256( order_id | manual | printer_role | unix_timestamp | user_id )`, para que siempre genere un trabajo nuevo.
- Inserción con `INSERT IGNORE`: si la clave ya existe, no se duplica y se registra en `orp_logs`.

### Ámbito de deduplicación

El ajuste `dedupe_scope` decide si el disparador forma parte de la clave:

| Valor | Clave | Comportamiento | Uso |
|---|---|---|---|
| `order` | `sha256(order_id \| location_id \| role \| template_version)` | **Un solo aviso por pedido**, aunque cambie de estado | Preset Aviso |
| `trigger` | incluye `trigger` | Un trabajo por cada disparo | Comandas que deben reimprimirse al cambiar de estado |

Por defecto: **`order`**. Es la opción segura: un pedido nunca genera dos papeles por un cambio de estado.

---

## 4. Reclamación atómica

```sql
UPDATE {prefix}orp_jobs
   SET status     = 'claimed',
       claimed_by = %d,
       claimed_at = NOW(),
       attempts   = attempts + 1
 WHERE id = %d
   AND status = 'pending'
   AND expires_at > NOW()
   AND attempts < max_attempts;
```

Si `affected_rows = 0`, el controlador responde **409** con el estado actual del trabajo. El agente lo interpreta como «otro lo cogió» y continúa.

> **Nota de implementación:** el servidor puede sustituir `NOW()` por el reloj PHP en UTC (parámetros vinculados) siempre que todas las escrituras y lecturas usen el mismo reloj. Es la forma recomendada: evita divergencias de zona horaria entre PHP y MySQL. Los cuerpos de error, en cambio, son literales y no pueden desviarse.

### Arrendamiento (lease)

- Un trabajo `claimed` sin `ack` en **120 s** vuelve a `pending` (lo hace el *reaper* en la siguiente petición o en el cron de limpieza).
- Al superar `max_attempts`, pasa a `failed` con el último error.

### Caducidad

- `expires_at = created_at + 30 min` (configurable).
- Un trabajo caducado nunca se imprime. Se marca `expired` y se avisa en el panel.

Racional: si la impresora estuvo apagada tres horas, no pueden salir cuarenta tickets de golpe.

---

## 5. Endpoints

Base: `https://<tienda>/wp-json/orp-print/v1`

Autenticación: `Authorization: Bearer <token>` en todos salvo `pair` y `health`.

### 5.1 `GET /health`

Sin autenticación. Sirve para diagnóstico y para comprobar que el plugin responde.

```json
{
  "ok": true,
  "plugin_version": "1.0.0",
  "api_version": 1,
  "woocommerce": "11.2.0",
  "hpos": true,
  "server_time": "2026-11-19T20:15:00+01:00"
}
```

### 5.2 `POST /pair`

```json
{
  "code": "482913",
  "agent_name": "TPV Mostrador",
  "platform": "windows",
  "agent_version": "1.0.0",
  "destinations": [
    { "role": "default", "label": "Mostrador", "printer_name": "EPSON TM-T20III", "copies": 2, "enabled": true }
  ]
}
```

```json
{
  "token": "orp_7f3a…",
  "location": { "id": 2, "code": "pueblo-b", "name": "Pizzería Pueblo B" },
  "poll_interval": 10
}
```

Errores: `orp_pairing_invalid` (400), `orp_pairing_expired` (410), `orp_pairing_locked` (429).

El token se muestra **una sola vez**. El servidor solo guarda su SHA-256.

### 5.3 `POST /heartbeat`

```json
{
  "agent_version": "1.0.0",
  "pending_local": 0,
  "last_error": null,
  "destinations": [
    { "role": "default", "label": "Mostrador", "printer_name": "EPSON TM-T20III", "copies": 2, "enabled": true }
  ]
}
```

El agente envía sus salidas en cada heartbeat. Son pocos bytes y evita tener que versionar el cambio. El plugin hace *upsert* por `(location_id, role)`.

```json
{
  "ok": true,
  "server_time": "2026-11-19T20:15:10+01:00",
  "pending_count": 2,
  "config_version": 14
}
```

Frecuencia: cada 60 s. Si `config_version` cambia, el agente vuelve a pedir la configuración del documento. Las salidas y las copias no dependen de este valor: el agente las reporta en cada heartbeat.

### 5.4 `GET /jobs?after=<id>&limit=10`

```json
{
  "jobs": [
    {
      "id": 1844,
      "order_id": 5678,
      "printer_role": "default",
      "created_at": "2026-11-19T20:14:58+01:00",
      "expires_at": "2026-11-19T20:44:58+01:00",
      "attempt": 0,
      "document": { "version": 1, "blocks": [ … ] }
    }
  ],
  "cursor": 1844
}
```

- `after` es un **id autoincremental**, nunca una fecha.
- `limit` máximo: 20.
- Si no hay trabajos, devuelve `{ "jobs": [], "cursor": <after> }` y **no** genera error.

### 5.5 `POST /jobs/{id}/claim`

Cuerpo vacío. El agente se identifica por el token.

- **200** → devuelve el trabajo completo.
- **409** → `{ "error": "orp_already_claimed", "status": "claimed" }`
- **410** → `{ "error": "orp_job_expired" }`
- **404** → el trabajo no pertenece a este local.

### 5.6 `POST /jobs/{id}/ack`

```json
{
  "status": "delivered",
  "attempt": 1,
  "printer": "EPSON TM-T20III",
  "elapsed_ms": 412
}
```

```json
{
  "status": "failed",
  "attempt": 1,
  "error_code": "orp_printer_not_found",
  "error": "No existe la impresora «EPSON TM-T20III» en este equipo"
}
```

Respuesta: `{ "ok": true, "job_status": "delivered" }`.

### 5.7 `POST /jobs/{id}/release`

Devuelve un trabajo reclamado a `pending` sin consumir intento. Se usa ante fallos transitorios (red, spooler ocupado).

```json
{ "reason": "spooler_timeout" }
```

### 5.8 `GET /config`

Configuración que necesita el agente, servida por el plugin para poder cambiarla sin reinstalar.

```json
{
  "config_version": 14,
  "paper_width_mm": 80,
  "encoding": "CP850",
  "cut_mode": "partial",
  "open_drawer": false,
  "poll_interval": 10,
  "sound_alert": false
}
```

Las impresoras y las copias **no viajan en esta respuesta**: las define el agente y las reporta en el heartbeat. El plugin solo configura lo que es común al documento.

El agente solo reclama trabajos cuyo `printer_role` corresponda a una salida **configurada y activa en ese equipo**. Si la salida no existe localmente, el trabajo permanece `pending` en el servidor: configurar la impresora más tarde no pierde pedidos.

---

## 6. Códigos de error

| Código | HTTP | Significado | Acción del agente |
|---|---|---|---|
| `orp_invalid_token` | 401 | Token desconocido | Detener y avisar |
| `orp_token_revoked` | 401 | Token revocado | Detener y avisar |
| `orp_not_found` | 404 | Trabajo o recurso inexistente | Descartar |
| `orp_already_claimed` | 409 | Otro agente lo cogió | Continuar |
| `orp_job_expired` | 410 | Caducado | Descartar |
| `orp_rate_limited` | 429 | Demasiadas peticiones | Backoff |
| `orp_invalid_payload` | 400 | Documento malformado | Marcar `failed` |
| `orp_pairing_invalid` | 400 | Código incorrecto | Pedir de nuevo |
| `orp_pairing_expired` | 410 | Código caducado | Pedir de nuevo |
| `orp_pairing_locked` | 429 | Demasiados intentos | Esperar 15 min |

El agente reporta además códigos propios en `ack`, como `orp_printer_not_found` o `orp_unsupported_document`. No son errores HTTP y no cambian el estado de la petición.

---

## 7. Documento neutro

### 7.1 Envoltorio

```json
{
  "version": 1,
  "meta": {
    "job_id": 1844,
    "order_id": 5678,
    "location_code": "pueblo-b",
    "created_at": "2026-11-19T20:14:58+01:00",
    "copies": 1,
    "printer_role": "default"
  },
  "paper": { "width_mm": 80 },
  "blocks": []
}
```

### 7.2 Bloques

| Tipo | Campos | Notas |
|---|---|---|
| `text` | `value`, `align`, `bold`, `size`, `invert` | `size`: `normal`, `double`, `triple` |
| `kv` | `items[] {label, value}`, `separator` | Se alinea a ancho de papel |
| `items` | `columns[]`, `rows[][]`, `align[]` | Tabla de productos |
| `totals` | `items[] {label, value, bold}` | Subtotal, envío, total |
| `divider` | `char` | Por defecto `-` |
| `image` | `data` (base64), `width_dots` | Logo; **PNG base64**, decodificado en el agente con .NET puro (sin System.Drawing); máximo 384 px de ancho |
| `qr` | `value`, `size` | |
| `barcode` | `value`, `symbology` | `CODE128`, `EAN13` |
| `feed` | `lines` | Avance de papel |
| `cut` | `mode` | `partial`, `full` |
| `drawer` | `action` | `open` |
| `raw` | `data` (base64) | Válvula de escape ESC/POS; solo uso avanzado |

### 7.3 Ejemplo completo

```json
{
  "version": 1,
  "meta": { "job_id": 1844, "order_id": 5678, "location_code": "pueblo-b",
            "created_at": "2026-11-19T20:14:58+01:00", "copies": 1,
            "printer_role": "default" },
  "paper": { "width_mm": 80 },
  "blocks": [
    { "type": "text", "value": "PIZZERIA RIO ROSSO", "align": "center",
      "bold": true, "size": "double" },
    { "type": "text", "value": "Pueblo B · 20:14", "align": "center" },
    { "type": "divider" },
    { "type": "kv", "items": [
        { "label": "Pedido", "value": "#5678" },
        { "label": "Cliente", "value": "María López" },
        { "label": "Teléfono", "value": "600 000 000" },
        { "label": "Entrega", "value": "Calle Mayor 3" }
    ]},
    { "type": "items",
      "columns": ["Cant", "Producto", "Importe"],
      "align": ["left", "left", "right"],
      "rows": [
        ["1", "Margarita grande", "12,00"],
        ["2", "Barbacoa", "22,00"]
      ]},
    { "type": "totals", "items": [
        { "label": "Subtotal", "value": "34,00" },
        { "label": "Envío", "value": "2,00" },
        { "label": "TOTAL", "value": "36,00", "bold": true }
    ]},
    { "type": "text", "value": "Gracias por su pedido", "align": "center" },
    { "type": "feed", "lines": 3 },
    { "type": "cut", "mode": "partial" }
  ]
}
```

---

## 8. Compatibilidad y versionado

- El agente rechaza documentos con `version` superior a la que conoce y marca el trabajo como `failed` con `orp_unsupported_document`.
- Añadir un tipo de bloque es compatible hacia atrás: el agente ignora los desconocidos y registra un aviso.
- Cambiar la semántica de un bloque existente exige nueva versión de documento.
- El plugin anuncia `api_version` en `/health`; el agente comprueba compatibilidad al arrancar.

---

## 9. Semántica de estados (resumen)

| Estado | Significado | ¿Se puede reintentar? |
|---|---|---|
| `pending` | En cola | — |
| `claimed` | Un agente lo tiene | Sí, si expira el lease |
| `delivered` | Entregado a Windows o al puerto 9100 | No |
| `failed` | Error definitivo o intentos agotados | Manual |
| `expired` | Superó `expires_at` | Manual |
| `cancelled` | Anulado desde el panel | No |

> `delivered` significa **entregado al sistema de impresión**, no «papel confirmado». Es la única garantía que se puede dar con hardware térmico estándar.
