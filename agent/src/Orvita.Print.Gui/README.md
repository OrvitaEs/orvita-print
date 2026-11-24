# Orvita Print · GUI de bandeja (`Orvita.Print.Gui`)

Interfaz gráfica del agente (Doc/08): icono de bandeja + ventana única con la barra lateral
de pestañas **Cola**, **Salidas**, **Emparejamiento**, **Diagnóstico**, **Configuración** y
**Acerca de**. Cumple ADR-13: la GUI es **otro cliente de la pipe de gestión** — toda
operación (`pair`, `destination`, `test`, `doctor`, `queue.list`, `queue.item`,
`pair.remove`) viaja por la pipe `orvitaprint-management`; la GUI NUNCA abre la SQLite, ni
lee `appsettings`, ni escribe ficheros del servicio. El token no existe para la GUI
(S-05: el servicio jamás lo devuelve).

## Diseño (GUI-09)

- **Barra lateral azul** (#1B4F8A→#15406E) con pill de selección por pestaña: icono 22 px
  (geometrías `StreamGeometry` embebidas en `Views/Icons.axaml` — sin ficheros externos) +
  etiqueta 12 px. Se implementa re-tematizando `TabControl`/`TabItem` en `App.axaml`
  (`TabStripPlacement=Left`): la navegación sigue siendo el mismo TabControl.
- **Tokens de diseño solo en `App.axaml`**: azul primario #1976D2, azul marino #1A3C6E,
  escenario #F2F7FC, borde de tarjeta #DCE8F5, verde #2E7D32, rojo #C62828, ámbar #B26A00.
  Tarjetas blancas con esquinas 12 px (`Classes="card"`), banners de incidencias
  (`banner-danger/warn/info`) y estados (verde/ámbar/naranja) para Emparejamiento.
- **Cabecera común**: control `PageHeader` (tile azul con icono + título 20 px + subtítulo
  12 px + acciones a la derecha).
- **Barra de estado inferior**: punto verde/rojo (`IsConnected` en `MainViewModel`) +
  texto del `status` (con «última respuesta HH:mm» cuando hay conexión buena). Sin clic.
- Botones: `primary` (azul con texto blanco), `secondary` (blanco con borde) y `danger`
  (borde rojo para acciones destructivas).

## Reglas que respeta (Doc/08 §1)

- **Visibilidad, no configuración paralela**: mismos comandos que el CLI, misma fuente de verdad.
- **El servicio es el único dueño del estado.** Cerrar la GUI no toca el servicio ni el poller.
- **Sin ventana abierta, CERO polling de cola** (solo `status` cada 5 s para el icono de bandeja).
- Español primero, sin recursos externos: los iconos del tray y de la ventana se dibujan en
  código/XAML.

## Arquitectura (3 capas)

| Capa | Ficheros | Qué hace |
|---|---|---|
| Transporte | `Management/IManagementChannel.cs`, `NamedPipeManagementChannel.cs`, `ManagementGuiClient.cs` | Named pipe de Windows con el MISMO protocolo JSON que el CLI (una conexión por operación: el servicio atiende UNA a la vez y una conexión viva de la GUI bloquearía a `orvitaprint`). El día de Linux/macOS: un UDS con el mismo protocolo (~1 día, Doc/08 §2). |
| Lógica (MVVM) | `Models/` (pure: `TrayStateMapper`, `QueueStatusMapper`, `QueueFilter`, `NotificationDetector`), `ViewModels/` (`Main`, `Queue`, `Destinations`, `Pairing`, `Diagnostics`, `Settings`, `About`) | Todo testeable con dobles de la pipe; la UI solo dibuja. |
| UI | `Views/` (Avalonia 11 + Fluent), `Tray/` (`TrayController`, `TrayIcons`, `WindowsBalloonNotifier`) | Ventana única con barra lateral, icono de bandeja (5 estados), globos, instancia única (mutex global `Global\Orvita.Print.Gui.SingleInstance`). |

### Ampliaciones de protocolo (ADITIVAS, Doc/08 §3)

- **`queue.list {limit}`** y **`queue.item {job_id}`** — lectura pura del outbox (sin
  `document_json`; el detalle viaja como TEXTO legible generado por `DocumentText` en Core).
- **`pair.remove`** — el «Desemparejar» de §4.4 (para el poller, quita el transporte y borra
  URL+token+local del almacén; conserva las salidas). La GUI pide **doble confirmación**
  (vista). La revocación EN EL PANEL sigue siendo paso del administrador.
- El selector de impresoras **NO añade op `printers`**: `destination.list` ya devuelve las
  impresoras instaladas (decisión documentada en `DestinationsViewModel`).

### Pestañas nuevas (GUI-09)

- **Configuración**: «Iniciar la GUI con Windows» (HKCU Run, misma lógica de `WindowsAutoStart`
  y del primer arranque), «Mostrar aviso al imprimir cada trabajo» (modo discreto) y una
  tarjeta de solo lectura con versión/rutas del servicio, pedidas por la pipe (`doctor`) en
  **carga perezosa** (solo al abrir la pestaña).
- **Acerca de**: solo lectura — versión de la GUI + versión del servicio (del `status`),
  texto del producto y línea de soporte.
- **PLACEHOLDER documentado**: `SupportLine` («soporte@orvitaprint.es») en
  `AboutViewModel` — el dominio definitivo se fija con el socio antes de distribuir.

### Dónde guarda sus preferencias la GUI

| Preferencia | Almacén | Clave |
|---|---|---|
| Arrancar con Windows | Registro **HKCU** `…\Run` | `WindowsAutoStart.ValueName` |
| Aviso al imprimir cada trabajo | **`%LocalAppData%\OrvitaPrint\gui.json`** (JSON de la GUI, best-effort) | `NotifySuccess` |
| Marcas de primer arranque | Registro HKCU `Software\Orvita\OrvitaPrintGui` | `FirstRunDone` |

### Desviación documentada: auto-arranque (GUI-07)

Doc/08 §5 pedía «HKCU Run activado por defecto al instalar». El MSI es **per-máquina** y
Windows Installer no puede escribir la clave `Run` de cada usuario que entre: la GUI la aplica
en su **primer arranque** (marca `FirstRunDone`); la casilla «Arrancar la GUI con Windows»
(pestaña Configuración) la quita y no se reactiva sola nunca.

## Pruebas (95 xUnit + headless)

```powershell
cd agent
dotnet test tests/Orvita.Print.Gui.Tests
```

- **Lógica pura**: mapeo bandeja (5 estados + tooltips exactos de §4.1), etiquetas/colores de
  los 5 estados de cola (§4.2), filtros/búsqueda, detección de balones (error siempre, éxito
  solo sin modo discreto, el arranque no notifica).
- **Viewmodels con doble de pipe**: auto-refresco SOLO con ventana abierta (`ShouldAutoRefresh`
  + guard), banner de errores no vistos persistente hasta revisarse, CRUD de salidas (contrato
  del CLI + avisos de impresora repetida + pdf demo), pair (validación S-03 en cliente
  comprobada contra `PairingService.ValidateServerUrl` real) y unpair.
- **GUI-09**: set de iconos embebido completo, punto de la barra de estado (verde solo con
  conexión buena), `SettingsViewModel` (doctor con carga perezosa), `AboutViewModel` (versión
  del servicio conservada si la pipe cae), `GuiJsonPreferenceStore` (persistencia, JSON
  corrupto tolerado y reparado).
- **Roundtrip real**: el canal de la GUI contra un `ManagementPipeServer` EN PROCESO
  (`queue.list`, `queue.item`, `pair.remove`, S-05 sin eco de token).
- **Headless (Avalonia)**: la ventana se construye con sus 6 pestañas, el punto de estado y
  contenido en pantalla; abrir Configuración pide `doctor` una sola vez.

## Guion de verificación MANUAL (lo que no se puede automatizar aquí)

Con el servicio arrancado (`dotnet run --project src/Orvita.Print.Service -- --data-dir .\smoke --console`)
y la GUI lanzada (`dotnet run --project src/Orvita.Print.Gui`):

1. **Bandeja — 5 estados (§4.1)**: sin servicio → icono hueco gris + tooltip «El servicio
   Orvita Print no está en ejecución»; servicio sin emparejar → gris; emparejado y poller
   corriendo → verde «imprimiendo normalmente»; tumba la red/tienda >3 min → ámbar; fuerza un
   error de impresión → rojo «Incidencia de impresión; abre para ver detalles».
2. **Menú contextual**: clic derecho en el icono → Abrir / Imprimir prueba / Diagnóstico (ZIP) /
   Salir. «Imprimir prueba» dispara un ticket por la salida; «Salir» cierra la GUI y el
   servicio SIGUE vivo (`orvitaprint status` sigue respondiendo).
3. **Cola (§4.2)**: entra un pedido → aparece en ≤3 s; badge «Pendiente» gris → «Imprimiéndose»
   azul → «Impreso» verde; filtra por estado, busca `#778899`; clic en un trabajo con error →
   banner rojo desaparece, el detalle muestra el documento legible con acentos/ñ/€.
4. **Balloon (§4.6)**: con la ventana oculta (cerrar = ocultar al tray), fuerza un fallo →
   globo de error SIEMPRE; un éxito → globo solo con «Mostrar aviso al imprimir cada trabajo»
   activo (Configuración).
5. **Instancia única**: segundo doble clic en el exe → no hay segunda ventana.
6. **Auto-arranque**: cierra sesión y vuelve a entrar → la GUI arranca sola (HKCU Run);
   desactiva la casilla en Configuración, reinicia → ya no arranca sola.
7. **DPI**: Windows al 150 % → la ventana y el tray se ven nítidos.

## Empaquetado

`installer/build.ps1` publica la GUI self-contained win-x64 (~103 MB sin comprimir; **MSI
total 63 MB** — la GUI añade ~23 MB comprimidos, dentro de lo «aceptable» de Doc/08 §5 sin
necesidad de trim) y la valida con `msiexec /a` (comprueba `Orvita.Print.Gui.exe` presente).

