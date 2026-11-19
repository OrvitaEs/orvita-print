# Arquitectura técnica

> Orvita Print · v1.0 · Noviembre 2026
> Documento de partida para desarrollo. No es una especificación cerrada: cada decisión marcada como *ADR* debe ratificarse antes de implementarse.

---

## 1. Principios

1. **El plugin es la fuente de verdad.** La cola vive en WordPress, no en un servicio nuestro.
2. **El agente es tonto.** No sabe qué es un pedido; solo ejecuta trabajos de impresión.
3. **El contrato es el producto.** Cuatro endpoints y un formato de documento neutro.
4. **Nada en la ruta crítica depende de nosotros.** Si desaparecemos, el sistema sigue imprimiendo.
5. **Fallo visible, nunca silencioso.** Todo trabajo acaba en `delivered`, `failed` o `expired`, y el administrador lo ve.
6. **Sin nube en V1.** El relay es una pieza futura, no una dependencia presente.

---

## 2. Vista de componentes

```
┌─────────────────────────────────────────────┐
│ SERVIDOR DEL CLIENTE (WordPress)            │
│                                             │
│  WooCommerce                                │
│      │ hook                                 │
│      ▼                                      │
│  Plugin orp-print                           │
│   ├── Locales y reglas                      │
│   ├── Constructor de documento              │
│   ├── Cola (tabla orp_jobs)                 │
│   ├── API REST /wp-json/orp-print/v1        │
│   └── Panel de administración               │
└──────────────────┬──────────────────────────┘
                   │ HTTPS · el agente inicia siempre
                   │ GET jobs · POST claim · POST ack
┌──────────────────▼──────────────────────────┐
│ PC DEL TPV (Windows 10/11 x64)              │
│                                             │
│  Servicio Windows (Orvita.Print.Service)    │
│   ├── Poller (10 s ± jitter)                │
│   ├── Cola local SQLite                     │
│   ├── Renderizador ESC/POS                  │
│   └── Transporte de impresión               │
│         ├── Spooler Windows (RAW)           │
│         ├── Spooler Windows (GDI)           │
│         └── TCP 9100                        │
│                                             │
│  Cli (Orvita.Print.Cli)                     │
│   └── pair · destinations · test · doctor   │
│                 ▲ named pipe (gestión)      │
└─────────────────┼───────────────────────────┘
                  │ USB / Ethernet
           ┌──────▼──────┐
           │  Impresora  │
           │  térmica    │
           └─────────────┘
```

---

## 3. Flujo de un pedido

```
1. Cliente compra           → WooCommerce crea el pedido
2. Hook                     → el plugin determina el local
3. Si hay local asignado    → construye el documento neutro
4. Inserta en orp_jobs      → estado pending, clave de idempotencia
5. Agente consulta          → GET /jobs?after=<cursor>
6. Agente reclama           → POST /jobs/{id}/claim  (atómico)
7. Agente imprime           → spooler / 9100
8. Agente confirma          → POST /jobs/{id}/ack  { delivered | failed }
9. Plugin actualiza         → estado final visible en el panel
```

Si en el paso 2 **no hay local coincidente**, el trabajo no se descarta: se asigna al local por defecto y se genera un aviso en el panel. Un pedido nunca desaparece en silencio.

### Varios locales, varios agentes

El local se decide **por regla** (selector con código canónico, código postal, ciudad o método de envío), nunca por el agente. Cada local tiene su propio código de emparejamiento y su propio agente; un agente no puede reclamar trabajos de otro local.

```
pedido → regla → local_id → agente emparejado con ese local_id → trabajos
```

El texto libre es el último recurso: acentos y variantes lo rompen. Lo recomendable es que el checkout guarde un **código de local**; si no, una **lista cerrada** de ciudades (radio o select) también es una fuente fiable.

Tres pizzerías en tres ciudades = tres locales, tres códigos, tres agentes. No hace falta que el WordPress sea multisite ni que use un plugin de multitienda.

---

## 4. Decisiones de arquitectura

| ID | Decisión | Alternativas descartadas | Motivo |
|---|---|---|---|
| ADR-01 | Cola en la base de datos del cliente | Cola en nube propia | Sin coste, sin RGPD, sin punto único de fallo |
| ADR-02 | El agente consulta (pull) | Webhook del plugin al agente | El TPV está detrás de NAT; no requiere IP pública ni abrir puertos |
| ADR-03 | Intervalo de 10 s con jitter | 1 s / long-polling | 10 s es imperceptible en cocina; long-polling no funciona bien en hosting compartido |
| ADR-04 | Documento neutro en JSON | Enviar ESC/POS ya generado | Permite 58/80 mm, distintas marcas y futuros transportes |
| ADR-05 | Spooler de Windows como vía principal | ESC/POS crudo por red como principal | El cliente ya tiene la impresora instalada y funcionando |
| ADR-06 | `delivered` = entregado a Windows | `printed` = confirmado por hardware | La mayoría de térmicas no devuelven estado fiable |
| ADR-07 | Emparejamiento por código de 6 dígitos | Usuario y contraseña | Un solo paso, sin cuentas, revocable |
| ADR-08 | Licencia local firmada | Validación online | El sistema no depende de nuestra disponibilidad |
| ADR-09 | Servicio autónomo, gestionado por CLI | GUI obligatoria / solo bandeja | Debe imprimir tras reiniciar sin sesión iniciada; en V1 no hay interfaz (ver ADR-13) |
| ADR-10 | .NET 10 LTS self-contained | .NET Framework / runtime compartido | Sin instalación de runtime, soporte hasta 2028 |
| ADR-11 | El plugin gratuito imprime solo desde el navegador | Reservar la impresión para el agente de pago | Sin agente el plugin sigue siendo útil; cumple la Guideline 5 de WordPress.org y evita el crippleware |
| ADR-12 | Núcleo portable, empaquetado por SO; V1 solo Windows | Multiplataforma desde el día 1 / Agente Windows en el código | El 90 % del núcleo (poller, SQLite, ESC/POS, licencia) es .NET puro; el spooler y la enumeración de impresoras viven en proyectos separados detrás de `IPrintTransport`. Multiplataforma se decide al codificar, no al publicar; en V1 se prueba y soporta solo Windows |
| ADR-13 | Servicio headless gestionado por CLI sobre named pipe | Pantalla de configuración / solo config manual | El servicio es funcional y configurable por sí solo (`appsettings.json` + pipe de gestión). El CLI es su primera cliente; una GUI futura será otra cliente de la misma pipe y no añadirá capacidades al servicio |

---

## 5. Plugin

### 5.1 Requisitos

| Elemento | Versión |
|---|---|
| PHP | 8.1 – 8.3 |
| WordPress | 6.8+ (probado hasta 7.1) |
| WooCommerce | 9.5+ (probado hasta 11.2) |
| HPOS | Compatible y declarado |

### 5.2 Estructura

```
orp-print/
├── orp-print.php
├── uninstall.php
├── composer.json
├── src/
│   ├── Plugin.php
│   ├── Admin/
│   │   ├── SettingsPage.php
│   │   ├── JobsPage.php
│   │   └── Diagnostics.php
│   ├── Domain/
│   │   ├── Location.php
│   │   ├── Agent.php
│   │   └── Job.php
│   ├── Queue/
│   │   ├── JobRepository.php
│   │   ├── Dispatcher.php
│   │   └── Reaper.php
│   ├── Routing/
│   │   ├── RuleMatcher.php
│   │   └── Rules/
│   ├── Template/
│   │   ├── TemplateSettings.php
│   │   └── DocumentBuilder.php
│   ├── Rest/
│   │   ├── Routes.php
│   │   ├── Auth.php
│   │   └── Controllers/
│   └── Support/
│       ├── Migrations.php
│       └── Logger.php
├── assets/
├── languages/
└── tests/
```

Prefijo de tablas, opciones y hooks: `orp_`.
Namespace PHP: `Orvita\PrintBridge`.

### 5.3 Puntos de enganche

| Hook | Uso |
|---|---|
| `before_woocommerce_init` | Declarar compatibilidad HPOS |
| `woocommerce_new_order` | Disparo principal en el caso del piloto |
| `woocommerce_order_status_changed` | Disparo configurable por estado |
| `woocommerce_order_actions` | Añadir «Reimprimir ticket» |
| `woocommerce_order_action_orp_reprint` | Ejecutar la reimpresión manual |
| `admin_notices` | Avisos de agente desconectado o trabajos caducados |

**Regla:** todo acceso al pedido se hace por la API CRUD (`wc_get_order`, `$order->get_*()`). Nunca `get_post_meta` para datos de pedido.

### 5.4 Construcción del documento

```
TemplateSettings  →  DocumentBuilder  →  array JSON  →  tabla orp_jobs
```

La versión de plantilla forma parte de la clave de idempotencia. Si el administrador cambia la plantilla, los pedidos nuevos generan un trabajo distinto, pero los ya impresos no se duplican.

### 5.5 Vista previa e impresión manual

El mismo `DocumentBuilder` alimenta dos salidas:

| Salida | Uso | Requiere agente |
|---|---|---|
| Documento neutro → `orp_jobs` | Impresión automática | Sí |
| HTML imprimible | Vista previa y botón «Imprimir» | No |

- La vista previa se sirve desde una página de administración y desde la ficha del pedido.
- El HTML usa CSS `@page` y ancho de 80 mm (58 mm como variante), sin recursos externos ni fuentes remotas.
- El botón «Imprimir» llama a `window.print()`: no hay PDF ni servicio externo.
- La impresión manual **no crea un trabajo** en la cola. Es una acción del navegador, no del agente.
- Si el local no tiene destinos activos (ningún agente emparejado), el pedido **no genera trabajos**: queda disponible para vista previa e impresión manual. La cola no crece sin agente.
- Esto es lo que hace que el plugin gratuito sea funcional por sí solo y lo que un revisor de WordPress.org verá como producto, no como embudo.

No se promete fidelidad ESC/POS exacta en el navegador: se promete el mismo contenido y el mismo ancho. La fidelidad byte a byte es responsabilidad del agente.

---

## 6. Agente

### 6.1 Proyectos

| Proyecto | Tipo | Responsabilidad |
|---|---|---|
| `Orvita.Print.Core` | Biblioteca | Poller, cola local, renderizado, transportes |
| `Orvita.Print.Service` | Servicio Windows | Ejecuta el núcleo, sin interfaz |
| `Orvita.Print.Cli` | Comando `orvitaprint` | Gestión: `pair`, `destination`, `test`, `doctor`, `status` |

> **Gestión sin UI (ADR-13).** El servicio funciona y se configura por sí solo: `appsettings.json` para la URL y el comportamiento, pipe de gestión para las operaciones. `Orvita.Print.Cli` es la primera cliente de esa pipe. Una GUI futura (estado visible tipo bandeja, botones) será otra cliente de la misma pipe y no añadirá ni cambiará nada del servicio.
| `Orvita.Print.Contracts` | Biblioteca | Modelos compartidos del contrato |

> **Portabilidad (ADR-12).** `Core` y `Contracts` no referencian nada específico de Windows. El spooler (`winspool.drv`), el fallback GDI y la enumeración de impresoras viven en `Orvita.Print.Printing.Windows`, detrás de la interfaz `IPrintTransport`. El mismo núcleo compila para Linux (systemd + transporte CUPS / TCP 9100) y macOS; en V1 solo se empaqueta, prueba y soporta Windows.

### 6.2 Rutas

| Dato | Ruta |
|---|---|
| Configuración | `%ProgramData%\OrvitaPrint\config.json` |
| Cola local | `%ProgramData%\OrvitaPrint\outbox.db` |
| Registros | `%ProgramData%\OrvitaPrint\logs\` |
| Licencia | `%ProgramData%\OrvitaPrint\license.dat` |

La configuración va en `ProgramData` y no en el perfil del usuario para que el servicio pueda leerla sin sesión iniciada.

### 6.2.1 Reparto de la configuración (tres capas)

| Capa | Dónde vive | Contenido | Quién la cambia |
|---|---|---|---|
| **Bootstrap (estática)** | `appsettings.json` | Rutas de BD y logs, nivel de log, intervalo de respaldo | El instalador, una vez |
| **Dinámica remota** | WordPress (`orp_settings`) → `GET /config` | Ancho de papel, codificación, corte, cajón, intervalo de poll, aviso sonoro | El panel del plugin; el agente re-sincroniza cuando cambia `config_version` |
| **Dinámica local** | SQLite del agente (`outbox.db`) | `ServerUrl`, destinos (impresora, rol, copias, enabled) y token (protegido con DPAPI, nunca en JSON plano) | El CLI vía pipe de gestión |

Regla: `appsettings.json` solo contiene lo que el servicio necesita antes de abrir la BD (rutas, registro); todo lo gestionable es dato. `ServerUrl` se establece al emparejar (`orvitaprint pair --server …`) y vive junto al token y los destinos: el agente se gestiona íntegramente por CLI sin editar ficheros, y un futuro cambio de transporte (directo → relay, §7) es un cambio de dato, no de instalación. `PollIntervalSeconds` es valor de respaldo hasta el primer `GET /config`; después el servidor es autoridad. Los destinos configurados localmente se reportan al servidor en cada heartbeat (`POST /heartbeat`), de modo que el panel siempre conoce las salidas activas de cada local.

### 6.3 Ciclo del poller

```
bucle:
  intentar:
    GET /jobs?after=cursor
    para cada trabajo:
      insertar en outbox (si no existe)
      claim
      imprimir
      ack
      cursor = max(cursor, id)
    dormir 10 s ± 20 %
  si error de red:
    backoff 10 → 20 → 40 → 60 s
  si 401:
    detener el poller y marcar «token inválido» (visible en `orvitaprint status` y en el panel)
```

### 6.4 Cola local y recuperación

Estados locales: `received` → `printing` → `done` / `error`.

Si el servicio arranca y encuentra un trabajo en `printing` sin resultado (caída o corte de luz), **se reimprime y se marca como `duplicate_suspected`**. Criterio de producto: en un ticket de cocina es preferible un duplicado a un pedido perdido. El incidente queda registrado y visible.

### 6.5 Renderizado

El renderizador traduce el documento neutro a:

| Transporte | Cuándo | Notas |
|---|---|---|
| Spooler RAW | Por defecto | `winspool.drv` · `StartDocPrinter` con tipo `RAW` |
| Spooler GDI | Si el driver rechaza RAW | `System.Drawing.Printing` |
| TCP 9100 | Impresora de red | Bytes ESC/POS directos |

Comandos soportados: inicializar, alinear, negrita, doble ancho/alto, subrayado, salto de línea, corte parcial/total, apertura de cajón, logo raster, código de barras y QR.

Codificación: **CP850 por defecto**, CP858 y CP437 configurables. Toda compilación debe superar la cadena de prueba:

```
Pizza Margarita · jamón, piña, ñ, €, áéíóú
```

### 6.6 Impresoras

- Se enumeran las impresoras instaladas en Windows (`Win32_Printer`).
- Se preselecciona la predeterminada.
- Se guarda el **nombre exacto**; si no aparece al imprimir, el trabajo falla con mensaje explícito.
- Si no hay ninguna impresora instalada, `orvitaprint status` y `orvitaprint doctor` lo indican con ese texto.
- El estado de la impresora se muestra como pista, nunca se usa para bloquear.

---

### 6.7 Salidas: impresoras y copias

> **Alcance V1:** de **1 a n salidas** por local, todas con el mismo documento. Cubre «imprimir en varias impresoras» y «varias copias en la misma».

El agente es el dueño de la configuración física:

- Añadir una salida: etiqueta, impresora y copias.
- Cada salida tiene un `role` estable.
- El agente reporta sus salidas al plugin en cada heartbeat.
- El plugin crea un trabajo por cada salida activa.

| Lo que quiere el cliente | Configuración |
|---|---|
| Un ticket | 1 salida, `copies = 1` |
| Dos copias en la misma impresora | 1 salida, `copies = 2` |
| Mostrador y cocina | 2 salidas, una por impresora |

Reglas:

- Si varias salidas apuntan a la misma impresora, el panel avisa: para eso están las copias.
- Si una salida se desactiva, sus trabajos pendientes se cancelan.
- Cambiar las copias afecta solo a trabajos nuevos.
- Si una salida no está configurada en el agente, sus trabajos quedan `pending`, no `failed`.

El documento entregable incluye la leyenda **«No válido como factura»**. La factura la emite el TPV; nuestro documento es operativo.

---

## 7. Modo directo y modo relay

El agente implementa la interfaz `ITransport`:

```csharp
interface ITransport {
    Task<JobBatch> FetchAsync(Cursor cursor, CancellationToken ct);
    Task<Job?> ClaimAsync(long jobId, CancellationToken ct);
    Task AckAsync(long jobId, JobResult result, CancellationToken ct);
}
```

| Implementación | Destino | Disponibilidad |
|---|---|---|
| `DirectTransport` | WordPress del cliente | V1 |
| `RelayTransport` | Nuestro servicio | Futuro |

El relay no cambia el agente: cambia la URL base y el modelo de autenticación, y ambos son datos en el almacén local del agente (§6.2.1), no ficheros de instalación. Esa es la garantía de que V1 no es un callejón sin salida.

---

## 8. Seguridad

| Elemento | Medida |
|---|---|
| Transporte | HTTPS obligatorio; rechazo de HTTP salvo modo desarrollo explícito |
| Token de agente | 32 bytes aleatorios, mostrado una vez, almacenado como SHA-256 |
| Alcance | Cada token solo accede a los trabajos de su local |
| Emparejamiento | Código de 6 dígitos, 10 minutos, un solo uso, máximo 5 intentos |
| Revocación | Inmediata desde el panel; el agente recibe 401 |
| Permisos | `manage_woocommerce` + capacidad propia `manage_orp_print` |
| Rate limit | 120 peticiones/minuto por token |
| Registros | Nunca datos personales; solo `order_id`, `job_id` y código de error |
| Licencia | Fichero firmado con ECDSA P-256; clave pública embebida en el agente |

El agente valida el certificado TLS del servidor con la cadena del sistema. No se admite ignorar errores de certificado.

---

## 9. Despliegue

### Plugin

1. Instalación desde WordPress.org o subida del ZIP.
2. Asistente: crear local → definir regla → crear plantilla → generar código de emparejamiento.
3. Descarga del agente desde la misma pantalla, con el código ya visible.

### Agente

1. MSI, instalación por máquina (`msiexec /i /qn`).
2. Registro del servicio con arranque automático.
3. `orvitaprint pair --server https://tienda.es <código>` → guarda URL y token (protegido) y arranca el poller.
4. `orvitaprint destination add --printer … --copies … --role …` por cada salida.
5. `orvitaprint test` en cada salida y `orvitaprint doctor` para verificar.

Requisitos: Windows 10 22H2 o Windows 11, x64, .NET 10 self-contained (no requiere instalación aparte).

Sin interfaz gráfica en V1 (ADR-13): el servicio y el CLI bastan para instalar, configurar y diagnosticar. Una GUI futura será otra cliente de la pipe de gestión.

### Firma de código

**Decisión V1: no se firma.** El agente se descarga solo desde nuestra web tras el pago y lo instala un profesional, que puede pasar el aviso de SmartScreen con instrucciones. No es el producto principal y no justifica 120–400 €/año mientras el volumen sea bajo.

Consecuencias asumidas:

- El instalador mostrará «Editor desconocido» y SmartScreen puede avisar. Se documenta con capturas; no se promete una instalación sin avisos.
- Se publica el **SHA-256 del MSI** junto a la descarga para que el instalador pueda verificarlo.
- La actualización automática no depende de Authenticode: firma **Ed25519 del manifiesto** con la clave privada fuera del servidor, hash del MSI y HTTPS. Basta para integridad y origen.
- No se acepta una actualización sin firma válida del manifiesto, aunque el MSI no esté firmado por Windows.

Se revisa si aparece volumen, instalaciones sin técnico o bloqueos de antivirus. Opciones cuando toque: Azure Artifact Signing (~10 USD/mes, organizaciones de la UE; personas físicas solo EE. UU. y Canadá), OV (200–400 €/año) o EV (sin ventaja frente a OV desde 2024).

---

## 10. Actualizaciones y licencia

### Actualización

```
GET https://updates.<dominio>/agent/latest.json
{
  "version": "1.2.0",
  "url": "https://…/OrvitaPrint-1.2.0.msi",
  "sha256": "…",
  "min_supported": "1.0.0",
  "signature": "…"
}
```

Se verifica firma del manifiesto y hash del MSI antes de ejecutar `msiexec /i /qn`. Authenticode no se exige en V1; cuando se firme, se fijará el thumbprint del certificado de editor y se rechazará cualquier otro. Si algo falla, se conserva la versión actual.

### Licencia

```json
{
  "license_id": "…",
  "sku": "studio",
  "max_locations": 10,
  "updates_until": "2027-11-19",
  "customer": "…",
  "signature": "…"
}
```

Validación local con clave pública embebida. **Sin conexión.** Si `updates_until` ha pasado, el agente sigue funcionando y solo deja de aceptar actualizaciones.

---

## 11. Observabilidad

| Dónde | Qué se ve |
|---|---|
| Panel del plugin | Cola, estados, últimos trabajos, agente conectado/desconectado, versión del agente |
| CLI del agente | `orvitaprint status`: último trabajo, último error, contador; `orvitaprint test`: impresión de prueba |
| Registros | Rotación diaria, 7 días, exportación a ZIP con `orvitaprint doctor --zip` |
| Avisos | Agente sin heartbeat > 3 min · trabajos caducados · local sin regla |

Sin telemetría remota en V1. Es coherente con el argumento de privacidad y elimina una obligación RGPD.

---

## 12. Rendimiento y límites

| Métrica | Valor |
|---|---|
| Pedidos | 200–300/mes (≈ 10/día) |
| Peticiones del agente | 6/min por TPV |
| Tamaño de trabajo | 4–8 KB |
| Retención de trabajos | 90 días (configurable) |
| Trabajos simultáneos | Irrelevante a este volumen |

El diseño está sobrado para el piloto y soporta sin cambios decenas de locales.

---

## 13. Compatibilidad

| Elemento | Soportado |
|---|---|
| Windows | 10 22H2, 11 23H2+ |
| Arquitectura | x64 |
| Impresoras | Epson TM-T20III/TM-m30, Xprinter XP-58/80, ESC/POS genérico |
| Ancho de papel | 58 mm y 80 mm |
| WordPress | 6.8 – 7.1 |
| WooCommerce | 9.5 – 11.2 |
| HPOS | Activado y desactivado |
| Checkout | Clásico y por bloques (solo lectura del pedido) |

---

## 14. Fuera de alcance en V1

- Relay y multi-plataforma.
- Android / iOS.
- Multi-sitio de WordPress.
- Edición visual libre de plantillas.
- Impresión de imágenes distintas al logo.
- Generación de facturas legales o certificación como sistema informático de facturación (Verifactu / TicketBAI). Se integra con un SIF de terceros; no se certifica el propio.
- Envío de ticket al cliente por email o WhatsApp.

> **Documentos internos frente a documentos fiscales.** La comanda de cocina no tiene valor fiscal y no está regulada. El ticket que se entrega al cliente sí puede ser una factura simplificada, y en ese caso el software que lo genera debe cumplir Verifactu o TicketBAI. Decisión de producto: el sistema imprime comandas y documentos que le entregue un SIF homologado; **no emite facturas por sí mismo**.

> Consecuencia técnica: para documentos fiscales, el trabajo se encola **cuando el documento existe**, no cuando se crea el pedido. El disparador es el SIF, no el hook de WooCommerce.

---

## 15. Configuración: presets y ajustes

### Principio

El piloto define **una instancia**, no el producto. La configuración del cliente inicial —un ticket, un destino, disparo al crear el pedido, plantilla completa— se documenta como **preset**, no como comportamiento fijo en el código.

### Tres capas

| Capa | Qué define | Quién la toca |
|---|---|---|
| **Producto** | Capacidades disponibles | Nosotros |
| **Preset** | Combinación típica de ajustes | El cliente elige una |
| **Instancia** | Valores concretos de un local | El cliente ajusta |

### Presets

| Preset | Destinos | Disparador | Dedupe | Plantilla |
|---|---|---|---|---|
| **Aviso** | 1 | Creación | `order` | Mínima |
| **Completo** | 1 | Creación | `order` | Todos los campos |
| **Cocina + cliente** | 2 | Creación o estado | `trigger` | Dos plantillas |
| **Personalizado** | 1–n | Libre | Libre | Libre |

El piloto usa **Completo**. El preset por defecto de una instalación nueva debe ser el más simple (**Aviso**), no el del piloto.

### Ajustes ortogonales

- Disparador: creación del pedido o estados concretos.
- Destinos: de 1 a n roles.
- Plantilla: preset + selección de campos.
- Copias: por destino.
- Aviso de incidencia: sonido y notificación en el agente. Email al local con enlace a la vista imprimible, solo si falla un trabajo (V1.x, no en V1).

### Límite

Cada opción nueva es soporte futuro. Regla: **preset primero, ajuste avanzado después**. Si una opción no la pide ningún cliente real, no se expone en la interfaz.
