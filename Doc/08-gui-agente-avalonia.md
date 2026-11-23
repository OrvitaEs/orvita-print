# GUI del agente · Avalonia

> Orvita Print · v1.0 · Noviembre 2026
> Especificación de la interfaz gráfica del agente, **post-V1**. Se activa cuando el piloto (F4) la pida: soporte, visibilidad o el canal de venta. Cumple ADR-13: la GUI es **otra cliente de la pipe de gestión** y no añade ni cambia nada del servicio.

---

## 1. Objetivo y principios

1. **Visibilidad**, no configuración paralela: ver qué está pasando en el TPV (trabajos, errores, conexión) sin consola.
2. **El servicio es el único dueño del estado.** La GUI solo lee por la pipe y delega operaciones (`pair`, `destination`, `test`, `doctor`). Nunca abre la SQLite ni toca `appsettings`.
3. **Opcional y ligera**: sin ella el agente funciona igual (consola + panel web). Arranca con la sesión de usuario, no exige admin.
4. **Español primero**, sin recursos externos (coherente con el resto del producto).

## 2. Arquitectura

| Pieza | Decisión |
|---|---|
| Proyecto | `Orvita.Print.Gui` (Avalonia 11, MVVM con `CommunityToolkit.Mvvm`), en la solución `agent/` |
| Proceso | Aplicación de bandeja propia: icono en el tray + ventana única. Una instancia (mutex global) |
| Canal | Pipe de gestión existente (`orvitaprint-management`). Abstracción de transporte `IManagementChannel` para que el día de Linux/macOS sea un Unix domain socket con el **mismo protocolo JSON** (~1 día de adaptación) |
| Datos | **Polling**: `status` cada 5 s; cola de trabajos cada 3 s mientras la ventana está abierta (y solo entonces). Sin push en la primera versión: el protocolo es request/response y el volumen es ínfimo (10 pedidos/día) |
| Sesión | La GUI vive en la sesión del usuario (sesión 1), el servicio en la 0. La GUI muestra el servicio; si la GUI no arranca, nada del producto se pierde |
| Autostart | Clave `Run` de HKCU (opcional, activada por defecto al instalar). El servicio Windows no depende de esto |

## 3. Ampliación del protocolo de pipe (ADITIVA, permitida por Doc/06 §8)

Lo que ya existe cubre: `pair`, `destination add/list/remove`, `test`, `status`, `doctor`. Para la vista de cola se añade **una operación**:

- **`queue list [--limit N]`** → los trabajos del outbox local con: `job_id`, `order_id`, `printer_role`, `copies`, estado local (`received`/`printing`/`done`/`error`), marcador `duplicate_suspected`, timestamps y último error. Implementación: el servicio lee su propia SQLite (operación de lectura pura, sin efectos). Con `--limit` por defecto 50.

Nada más. Los estados del servidor (`pending`/`claimed`/`delivered`…) **no se muestran aquí**: eso es territorio del panel de WordPress. La GUI muestra lo que este equipo hace con lo que recibe.

## 4. Pantallas

### 4.1 Icono de bandeja (siempre visible)

| Estado del servicio | Icono | Tooltip |
|---|---|---|
| Emparejado y poller corriendo | Verde | «Orvita Print · imprimiendo normalmente» |
| Emparejado pero servidor sin latido reciente | Ámbar | «Sin conexión con la tienda; reintentando» |
| Trabajo en `error` o `duplicate_suspected` reciente | Rojo | «Incidencia de impresión; abre para ver detalles» |
| Sin emparejar | Gris | «Sin emparejar: ejecuta el emparejamiento» |
| Servicio no responde (pipe cerrada) | Rojo parpadeante/apagado | «El servicio Orvita Print no está en ejecución» |

Clic → abre la ventana. Menú contextual: Abrir, Imprimir prueba, Diagnóstico (ZIP), Salir (solo cierra la GUI, nunca el servicio).

### 4.2 Ventana principal — pestaña «Cola» (la pieza que pide el negocio)

Lista de trabajos del outbox local con **badges de estado en español**:

| Estado local | Etiqueta en GUI | Color |
|---|---|---|
| `received` | Pendiente | Gris |
| `printing` | Imprimiéndose | Azul (spinner) |
| `done` | Impreso | Verde |
| `error` | Error | Rojo |
| `duplicate_suspected` | Reimpreso tras corte | Ámbar (badge sobre el estado) |

Columnas: pedido (enlace al panel si hay ServerUrl), salida/role, copias, hora de llegada, hora de impresión, intentos, último error. Filtro por estado y búsqueda por nº de pedido. Auto-refresh con la ventana abierta. Detalle al hacer clic: documento en texto legible (el `.txt` que ya produce el flujo) y las marcas de tiempo completas.

Aviso destacado arriba de la lista cuando hay errores no vistos (persistente hasta que el usuario los abra).

### 4.3 Pestaña «Salidas»

Lista de salidas configuradas (role, impresora, copias, activa) con añadir/editar/quitar — **mismo contrato que el CLI** (`destination add/list/remove`), con selector de impresoras del enumerador (F2-05), aviso si dos salidas apuntan a la misma impresora, y soporte del destino `pdf` (solo para demos: marcado como tal).

### 4.4 Pestaña «Emparejamiento»

Estado actual (ServerUrl con token oculto, local, última conexión). Flujo: pegar el código de 6 dígitos → `pair` → confirmación con el nombre del local. Botón «Desemparejar» (pide confirmación; equivale a revocar desde el panel, que queda como paso del administrador).

### 4.5 Pestaña «Diagnóstico»

Resumen del `doctor` (versiones, licencia, poller, cola), botón «Generar ZIP de diagnóstico» (`doctor --zip` → muestra la ruta con botón «Abrir carpeta»), y visor de las últimas líneas del log (fichero, solo lectura).

### 4.6 Notificaciones

- Balloon del tray al **fallar** un trabajo (siempre) y al **imprimir** un trabajo (configurable; respeta `sound_alert` del `GET /config`).
- Nada de notificaciones por cada éxito si se activa el modo discreto (una sola barra de estado en la ventana).

## 5. Empaquetado

- El **mismo MSI** (F3-01) instala el ejecutable de la GUI junto al servicio y al CLI. La GUI no se registra como servicio: es un exe de sesión de usuario.
- Auto-arranque por usuario (HKCU Run) activado por defecto; casilla en la propia GUI.
- Sin dependencias nuevas de runtime (Avalonia self-contained en la misma publicación; el MSI crece ~50–80 MB — aceptable, o trimmed si el tamaño importa).

## 6. Fuera de alcance

- Editar plantillas o ajustes del documento (territorio del panel web).
- Arrancar/parar/instalar el servicio Windows (eso es del MSI y de `sc`).
- Ver la cola del servidor o de otros locales (el panel web ya lo hace).
- Push/WebSocket en tiempo real (si el polling de 3 s se queda corto, entonces se añade `events` por pipe — aditivo).
- Multiplataforma real (requiere `Printing.Cups` + servicio Unix: proyecto aparte, ADR-12 ya lo habilita).

## 7. Tareas y estimación

| ID | Tarea | Est. |
|---|---|---|
| GUI-01 | Esqueleto Avalonia + `IManagementChannel` (pipe Windows) + cliente de protocolo compartido con el CLI | 0,5 d |
| GUI-02 | Pipe op `queue list` (servicio) + lectura del outbox + pruebas | 0,5 d |
| GUI-03 | Bandeja: icono por estado, tooltip, menú contextual, notificaciones | 1 d |
| GUI-04 | Pestaña «Cola» (estados, filtros, auto-refresh, detalle) | 1 d |
| GUI-05 | Pestañas «Salidas» y «Emparejamiento» (flujos existentes de la pipe) | 1 d |
| GUI-06 | Pestaña «Diagnóstico» + doctor ZIP + visor de log | 0,5 d |
| GUI-07 | MSI (integración en F3-01), auto-arranque, ajuste de publicación | 0,5 d |
| GUI-08 | Pruebas: MVVM testable, protocolo, humo con servicio real | 0,5 d |

**Total: ~5,5 días de desarrollo asistido.** Precondición: F4 en marcha (el feedback del piloto define prioridades de pantallas).

## 8. Criterios de aceptación

- [ ] Sin la GUI, el agente funciona exactamente igual (no hay dependencia de arranque).
- [ ] La cola muestra los 5 estados con las etiquetas y colores de §4.2; un trabajo con error es visible en <3 s tras el fallo.
- [ ] El icono de bandeja refleja el estado del servicio (desemparejado, sin conexión, incidencia) sin abrir la ventana.
- [ ] Todas las operaciones pasan por la pipe: la GUI no lee ficheros ni BD.
- [ ] El token jamás aparece en pantalla (ServerUrl oculto, como `status`).
- [ ] Cerrar la GUI no detiene el servicio ni el poller.
- [ ] Con la ventana cerrada, el consumo de CPU de la GUI es ~0 (sin polling).

## 9. Riesgos

| Riesgo | Mitigación |
|---|---|
| La GUI se convierte en vía de configuración paralela y diverge del CLI/pipe | Regla de hierro: toda mutación pasa por la pipe; `Doc/06` §12 lo recuerda |
| Bandeja invisible en Windows (oculta por defecto) | Guía con captura + el panel web sigue siendo la referencia del dueño |
| Avalonia + tray: diferencias entre versiones de Windows | Probar en 10 22H2 y 11 (matriz F4); fallback: ventana abierta al inicio |
| Tamaño del MSI | Self-contained con trim solo si supera lo razonable; medir antes de decidir |
