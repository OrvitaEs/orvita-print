# Guía de trabajo · F0

> Para quien empieza a desarrollar. No hace falta leerlo todo antes de escribir código: esta guía dice qué leer, qué está decidido, qué no y qué hay que entregar al final de F0.

---

## 1. Qué leer y en qué orden

| Orden | Documento | Para qué | Tiempo |
|---|---|---|---|
| 1 | `00-resumen-ejecutivo.md` | Entender el producto y el negocio | 10 min |
| 2 | `03-arquitectura-tecnica.md` §2–§7 | Componentes, flujo, agente, salidas | 15 min |
| 3 | `04-modelo-de-datos-y-contrato.md` §2–§6 | Tablas, endpoints, errores | 20 min |
| 4 | `05-plan-de-ejecucion-y-pruebas.md` §1–§4 | Fases y criterios de aceptación | 10 min |
| 5 | Esta guía, §6–§10 | Qué construir exactamente en F0 | 15 min |

`01` y `02` son negocio y costes: no son necesarios para programar. Se leen si hay que tomar una decisión comercial.

---

## 2. Qué está decidido (no reabrir en F0)

| Tema | Decisión |
|---|---|
| Conexión | Directa: el agente consulta el WordPress del cliente. Sin nube |
| Cola | En MySQL del cliente, tabla `orp_jobs` |
| Transporte | Spooler de Windows primero; GDI de respaldo; TCP 9100 opcional |
| Confirmación | `delivered` = entregado al spooler, no papel confirmado |
| Salidas | 1..n por local; un trabajo por salida; copias dentro del trabajo |
| Disparador | Creación del pedido o estados concretos, configurable |
| Deduplicación | `dedupe_scope = order` por defecto |
| Plantillas | Un documento en V1; roles distintos quedan para después |
| Licencia | Plugin GPL gratuito y **útil sin agente** (vista previa e impresión manual); agente propietario con licencia perpetual + 12 meses |
| Licencia en el plugin | Ninguna: el plugin no valida claves ni llama a servidores de Orvita |
| Distribución | WordPress.org es canal, no dependencia de lanzamiento |
| Stack | PHP 8.1+, WordPress 6.8+, WooCommerce 9.5+, .NET 10 LTS |
| Firma de código | **No en V1**: descarga tras el pago e instalación por un profesional |

Si algo de esto parece mal, se plantea antes de escribir código, no durante.

---

## 3. Qué falta decidir (y qué bloquea)

| Decisión | ¿Bloquea F0/F1? | Workaround mientras tanto |
|---|---|---|
| Nombre comercial | No | Usar `Orvita Print`, prefijo `orp_` |
| Quién desarrolla | **Sí, organizativo** | — |
| Precio de lanzamiento | No | No afecta al código |
| WordPress.org desde el día 1 | No | Cabecera GPL v2+ y `readme.txt` desde el principio |
| Facturar el piloto | No | No afecta al código |

Solo la segunda es organizativa. Si quien lee esta guía es quien va a desarrollar, no hay bloqueo: se puede empezar hoy.

---

## 4. Alcance de V1

### Dentro

- Locales con reglas (lista cerrada de ciudades, código postal, método de envío, meta).
- Disparador por creación o por estado.
- Plantilla por campos seleccionables, con **vista previa imprimible e impresión manual desde el navegador**.
- El plugin es funcional sin agente: configurar, previsualizar e imprimir a mano.
- 1..n salidas por local; copias por salida.
- Reimpresión manual desde el pedido.
- Panel de trabajos y errores.
- HPOS y checkout clásico + bloques.
- Reintentos, caducidad y `ack` idempotente.
- Agente como servicio headless, gestionado por CLI (`orvitaprint`): emparejamiento, salidas, impresión de prueba y diagnóstico.

### Fuera

- Relay o nube propia.
- Shopify y PrestaShop.
- Facturación / Verifactu / TicketBAI. Solo se integra con un SIF externo.
- Plantillas distintas por rol.
- Selector de local propio en el checkout.
- Email o WhatsApp al cliente final.
- Android, macOS, Linux.
- Varios agentes por local.
- White-label.

---

## 5. Entorno

| Elemento | Versión |
|---|---|
| PHP | 8.2 (mínimo 8.1) |
| WordPress | 7.x (mínimo 6.8) |
| WooCommerce | 11.x (mínimo 9.5) |
| Base de datos | MySQL 8.0 o MariaDB 10.6 |
| HPOS | Activo en desarrollo y en pruebas |
| Node | No hace falta en V1: JS de administración en vanilla |

Recomendado `wp-env` (Docker):

```json
{
  "phpVersion": "8.2",
  "plugins": [ "." ],
  "config": {
    "WP_DEBUG": true,
    "WP_DEBUG_LOG": true,
    "WP_DEBUG_DISPLAY": false
  }
}
```

Alternativa: Local by Flywheel o cualquier WordPress con WooCommerce y WP-CLI.

Pruebas: PHPUnit con la suite de WordPress. En F0 basta una prueba de humo del esquema y otra del reclamo atómico; la matriz completa es de F2.

---

## 6. Estructura del repositorio

```
orp-print/
├── orp-print.php              # cabecera, constantes, arranque
├── uninstall.php
├── composer.json              # solo desarrollo: phpcs, phpunit
├── src/
│   ├── Plugin.php
│   ├── Install/
│   │   ├── Activator.php
│   │   └── Schema.php
│   ├── Rest/
│   │   ├── Router.php
│   │   ├── Auth.php
│   │   ├── JobsController.php
│   │   ├── HeartbeatController.php
│   │   └── ConfigController.php
│   ├── Domain/
│   │   ├── LocationResolver.php
│   │   ├── JobFactory.php
│   │   ├── DocumentBuilder.php
│   │   └── DestinationRepository.php
│   ├── Admin/
│   │   ├── Menu.php
│   │   └── SettingsPage.php
│   ├── Cli/
│   │   └── SeedCommand.php
│   └── Support/
│       ├── Clock.php
│       └── Logger.php
├── tools/
│   └── simulator.php          # agente de referencia, sin dependencias
├── tests/
└── languages/
```

Reglas:

- Namespace `Orvita\PrintBridge`, PSR-4 sobre `src/` con autoloader propio (sin `vendor/` en el ZIP).
- Prefijo `orp_` para tablas, opciones, hooks y capacidades.
- Sin dependencias de Composer en producción. Composer solo para desarrollo.
- `declare(strict_types=1);` en todos los PHP nuevos.
- Nada de `?>` al final de los archivos.

---

## 7. F0 en dos días

### Día 1 · Esqueleto y datos

| Tarea | Entregable |
|---|---|
| Plugin activable, constantes, autoload | Aparece en Plugins y no lanza avisos |
| Capacidad `manage_orp_print` | Concedida a `administrator` al activar |
| Esquema con `dbDelta` | 6 tablas: local, agente, códigos, trabajos, salidas y logs |
| Opciones por defecto | `orp_settings`, `orp_db_version`, versiones |
| `uninstall.php` | No borra datos salvo opción explícita |
| `GET /health` público y autenticación | `/health` responde 200 sin token; `/config` responde 401 sin token |
| `wp orp seed` | Crea 3 locales, 1 agente y 1 trabajo de prueba |

### Día 2 · Contrato y simulador

| Tarea | Entregable |
|---|---|
| `GET /jobs?after&limit` | Devuelve trabajos pendientes del local del token |
| `POST /jobs/{id}/claim` | Atómico; segundo agente recibe 409 |
| `POST /jobs/{id}/ack` | `delivered` / `failed`, idempotente |
| `POST /jobs/{id}/release` | Vuelve a `pending` sin consumir intento |
| `POST /heartbeat` | Guarda `last_seen_at` y salidas |
| `GET /config` | Devuelve configuración del documento |
| `tools/simulator.php` | Poll→claim→ack en bucle, con `--once` y `--fail`; el token se pasa por parámetro |
| Documento neutro v1 | Generado por `DocumentBuilder`, guardado en `payload` |

`POST /pair` queda para F1: en F0 el token se siembra con `wp orp seed`.

---

## 8. Contrato congelado

Namespace: `orp-print/v1`. Autenticación: `Authorization: Bearer <token>` y, como respaldo, `X-Orp-Token`. Todos los endpoints la exigen salvo `/health` y `/pair`.

| Método | Ruta | Uso |
|---|---|---|
| GET | `/health` | Salud y versión |
| GET | `/jobs?after=<id>&limit=10` | Poll |
| POST | `/jobs/{id}/claim` | Reclamo atómico |
| POST | `/jobs/{id}/ack` | Resultado |
| POST | `/jobs/{id}/release` | Fallo transitorio |
| POST | `/heartbeat` | Estado y salidas |
| GET | `/config` | Configuración del documento |
| POST | `/pair` | Emparejamiento (F1) |

Reglas de cambio:

- **Aditivo**: añadir campos con valor por defecto no rompe.
- **Rompedor**: cambiar nombres, tipos o semántica exige `/v2`.
- El documento lleva `version: 1`. Una versión desconocida se rechaza sin bloquear la cola.
- Los errores usan los códigos de `04` §6. No inventar códigos nuevos sin documentarlos.

---

## 9. Simulador de agente

Es la herramienta de desarrollo, no el agente real. Debe:

1. Hacer heartbeat.
2. Pedir trabajos con cursor.
3. Reclamar uno.
4. Escribir el documento en `var/jobs/<id>.json` y una versión de texto legible.
5. Confirmar `delivered` o `failed` según `--fail`.
6. Manejar 409, 410 y 429 sin romperse.

```bash
php tools/simulator.php --url=http://localhost:8888 --token=TOKEN --once
php tools/simulator.php --url=http://localhost:8888 --token=TOKEN --fail=orp_printer_not_found
```

Sin impresora no se prueba la impresión: se prueba el contrato. La impresión se valida en F2.

---

## 10. Definición de hecho de F0

- [ ] El plugin se activa en un WordPress limpio sin avisos.
- [ ] Las tablas se crean y no se duplican al desactivar y reactivar.
- [ ] `/health` responde 200 sin token y `/config` responde 401 sin token.
- [ ] Un trabajo sembrado se reclama, se confirma y queda `delivered`.
- [ ] Dos simuladores a la vez: solo uno obtiene el trabajo.
- [ ] Un `ack` repetido no altera el estado.
- [ ] Un trabajo caducado devuelve 410 y no se imprime.
- [ ] Un token revocado devuelve 401.
- [ ] El cursor avanza aunque un trabajo falle.
- [ ] `wp orp seed` deja el entorno listo en un comando.
- [ ] El README del repositorio explica cómo arrancar y probar en cinco comandos.

Si algo de esto falla, F0 no está cerrado y F1 no empieza.

---

## 11. Trampas conocidas

- **Cabecera `Authorization`**: algunos hostings la eliminan. Aceptar también `X-Orp-Token` y comprobarlo en el servidor real del piloto.
- **`dbDelta`**: formato estricto (dos espacios en `PRIMARY KEY`, sin claves foráneas). Una tabla por llamada si hay dudas.
- **Zona horaria**: guardar UTC en base de datos y mostrar con `wp_date()` y `wp_timezone()`.
- **HPOS**: usar `$order->get_meta()` y `$order->update_meta_data()`. Nunca `get_post_meta()` sobre pedidos.
- **Checkout de bloques**: enganchar `woocommerce_store_api_checkout_order_processed` además de `woocommerce_checkout_order_processed`. La deduplicación protege del doble disparo.
- **Duplicados**: `INSERT IGNORE` sobre `idempotency_key`; si `affected_rows = 0`, ya existía.
- **JSON**: `LONGTEXT`, no tipo `JSON`, para compatibilidad con MariaDB y con `dbDelta`.
- **Límite de peticiones**: contador por token en transients; devolver 429 con `Retry-After`.
- **Secretos**: guardar solo el hash del token y un prefijo de 8 caracteres para localizarlo.

---

## 12. Convenciones

- Ramas: `f0/esquema`, `f0/endpoints`, `f1/panel`.
- Commits: `feat:`, `fix:`, `docs:`, `test:`, `chore:`.
- Text domain: `orp-print`.
- Todo texto visible en español, envuelto en funciones de traducción.
- Comentarios en español; nombres de código en inglés.
- Un cambio de esquema sube `orp_db_version` y añade su migración; nunca se edita una migración ya publicada.
- No se mezcla negocio y código: los presets son configuración, no condicionales.

---

## 13. Después de F0

F1 construye el plugin real: alta de local, regla, plantilla, panel y creación de trabajos desde un pedido. El contrato no se toca salvo error demostrado.

La primera pregunta de F1 no es técnica: **¿quién valida la plantilla del ticket?** Ese texto lo tiene que aprobar el cliente antes de imprimir en producción.
