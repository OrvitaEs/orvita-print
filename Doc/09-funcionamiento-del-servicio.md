# Funcionamiento del servicio (Orvita.Print.Service)

> Orvita Print — v1.0 — Noviembre 2026
> Documentación completa del servicio Windows del agente: qué es, cómo arranca, cómo imprime, cómo se gestiona y cómo se actualiza. Complementa a Doc/03 (arquitectura), Doc/04 (contrato) y Doc/07 (plan técnico). Toda la descripción deriva del código real de `agent/src/Orvita.Print.Service`.

---

## Índice

1. [Qué es el servicio](#1-qué-es-el-servicio)
2. [Mapa de componentes y dependencias](#2-mapa-de-componentes-y-dependencias)
3. [Arranque: de `Program.cs` al bucle principal](#3-arranque-de-programcs-al-bucle-principal)
4. [Configuración en tres capas](#4-configuración-en-tres-capas)
5. [Emparejamiento (pair)](#5-emparejamiento-pair)
6. [El poller: cómo llegan los trabajos](#6-el-poller-cómo-llegan-los-trabajos)
7. [Pipeline de un trabajo (JobProcessor)](#7-pipeline-de-un-trabajo-jobprocessor)
8. [La cola local (outbox)](#8-la-cola-local-outbox)
9. [Heartbeat y configuración remota](#9-heartbeat-y-configuración-remota)
10. [Pipe de gestión y CLI](#10-pipe-de-gestión-y-cli)
11. [Impresión de prueba (test)](#11-impresión-de-prueba-test)
12. [Diagnóstico (doctor --zip)](#12-diagnóstico-doctor---zip)
13. [Licencia](#13-licencia)
14. [Actualizaciones automáticas](#14-actualizaciones-automáticas)
15. [Modelo de seguridad](#15-modelo-de-seguridad)
16. [Logging y observabilidad](#16-logging-y-observabilidad)
17. [Resumen de tiempos y valores por defecto](#17-resumen-de-tiempos-y-valores-por-defecto)
18. [Inventario de ficheros del proyecto](#18-inventario-de-ficheros-del-proyecto)

---

## 1. Qué es el servicio

`Orvita.Print.Service` es un **servicio Windows headless** (ADR-09/13) que vive en el PC del TPV. Su único trabajo es: **pedir trabajos al plugin de WordPress, imprimirlos en la impresora térmica y reportar el resultado**. No sabe qué es un pedido: solo entiende del documento neutro y del contrato REST (`/jobs`, `/claim`, `/ack`, `/heartbeat`, `/config`, `/pair`).

Características clave:

- **Un solo ejecutable** (`Orvita.Print.Service.exe`, `net10.0-windows`) que corre como servicio de Windows en producción y como consola normal en desarrollo — `AddWindowsService` solo aplica su ciclo de vida cuando el proceso es un servicio real (padre `services.exe`); desde un terminal es consola sin necesidad de detección propia.
- **Todo el cableado vive en `AgentHostBuilder`**, que es lo que prueba el humo y lo que reutiliza la pipe de gestión.
- **Siempre vivo**: aunque no esté emparejado, el servicio arranca y queda a la espera de que la pipe de gestión le entregue un `pair` — sin reiniciar el proceso.
- **Gestionado por CLI**: `Orvita.Print.Cli` (`orvitaprint …`) habla con él por una named pipe; el token del servidor nunca sale del proceso.

### Cómo se ejecuta

```
# Producción (MSI lo instala como servicio Windows "OrvitaPrint")
Orvita.Print.Service.exe            # lo arranca el SCM (services.exe)

# Desarrollo (consola, contra wp-env)
Orvita.Print.Service.exe --server https://tulegal/wp-json/orp-print/v1 --token <token> --allow-insecure-http --data-dir ./datos
```

Argumentos de desarrollo (no persistentes; en producción la URL/token viven cifrados en SQLite):

| Argumento | Efecto |
|---|---|
| `--server <url>` | URL base del servidor (desarrollo) |
| `--token <token>` | Token del agente (solo en memoria; **nunca** se registra en el log, S-05) |
| `--allow-insecure-http` | Permite HTTP sin TLS — solo desarrollo (S-03) |
| `--data-dir <ruta>` | Directorio de datos; ruta relativa se resuelve contra el ejecutable |
| `--console` | Marcador inofensivo, para lanzar en consola de forma explícita |

---

## 2. Mapa de componentes y dependencias

```
                        ┌──────────────────────────────────────────────┐
                        │            Orvita.Print.Service.exe          │
                        │                                              │
  SCM (services.exe) ───►  AgentHostBuilder.BuildHost()                │
                        │      │                                       │
                        │      ├── Serilog (consola + fichero diario)  │
                        │      │                                       │
                        │  Hosted Services (en orden):                 │
                        │      ├── ManagementPipeServer  ◄─────────────┼──── orvitaprint (CLI)
                        │      ├── UpdateService                       │
                        │      └── AgentWorker  ◄──────────────────────┼──── el orquestador principal
                        │                                              │
                        │  Singletons compartidos:                     │
                        │   ServerConnection ─── DirectTransport ──────┼──── HTTPS ──► Plugin WP
                        │   PollerController ─── Poller (Core)         │      /jobs /claim /ack
                        │   JobProcessor ─────── EscPosRenderer        │      /heartbeat /config
                        │   HeartbeatSender ──── DirectTransport       │      /pair
                        │   OutboxRepository ─── SQLite outbox.db      │
                        │   LocalConfigStore ─── SQLite outbox.db      │
                        │   StoreDestinationProvider (SQLite)          │
                        │   PairingService / PairingWaiter             │
                        │   DpapiSecretProtector (S-01)                │
                        │   LicenseState / UpdateOrchestrator / …      │
                        │                                              │
                        │  Printing.Windows:                           │
                        │   WindowsPrintTransportFactory ──────────────┼──── Spooler RAW / GDI,
                        │   WindowsPrinterEnumerator                   │      TCP 9100 o PDF
                        └──────────────────────────────────────────────┘
```

Referencias del proyecto (`Orvita.Print.Service.csproj`):

- `Orvita.Print.Contracts` — DTOs del contrato (Doc/04).
- `Orvita.Print.Core` — portable (ADR-12): `Poller`, `OutboxRepository`, `LocalConfigStore`, `DirectTransport`, `PairingClient`, `EscPosRenderer`, `UpdateChecker`, protocolo de gestión.
- `Orvita.Print.Printing.Windows` — transportes de impresión reales.
- Paquetes: `Microsoft.Extensions.Hosting.WindowsServices`, `System.Security.Cryptography.ProtectedData` (DPAPI, S-01), Serilog (host, consola y fichero).

---

## 3. Arranque: de `Program.cs` al bucle principal

### 3.1 `Program.cs`

Mínimo: construye el host con `AgentHostBuilder.BuildHost(args)` y hace `await host.RunAsync()`. Todo el cableado (logging, DI, hosted services) está en `AgentHostBuilder` para que sea reutilizable y comprobable por humo.

### 3.2 `AgentHostBuilder.BuildHost` — cableado del host (F2-07)

1. Lee `AgentOptions` de la sección `Orvita` de `appsettings.json` y aplica los argumentos de desarrollo (`ApplyDevelopmentArgs`).
2. Configura **Serilog**: nivel desde `Serilog:MinimumLevel`, sink de consola y sink de fichero diario (`<data>/logs/orvitaprint-.log`, rotación por día, 7 ficheros retenidos).
3. Registra en DI todos los singletons (tabla en §2) y tres *hosted services*:
   - `ManagementPipeServer` — pipe de gestión (singleton explícito porque el `UpdateApplier` debe poder pararla).
   - `UpdateService` — canal de actualización (1×/día).
   - `AgentWorker` — el orquestador principal.

El host se construye con `AddWindowsService(ServiceName: "OrvitaPrint")`.

### 3.3 `AgentWorker.ExecuteAsync` — secuencia de arranque

Es el corazón del servicio (`BackgroundService`). Pasos en orden:

1. **Recuperación de la cola** (Doc/03 §6.4): `OutboxRepository.RecoverInterrupted()` reencola como `received` los trabajos que quedaron en `printing` por una caída, marcados `duplicate_suspected` (mejor un duplicado que un pedido perdido).
2. **Drenaje inicial**: `JobProcessor.ProcessStartupQueueAsync()` imprime todo lo que ya estaba `received` con documento de un ciclo anterior.
3. **Resolver el emparejamiento**: mientras no haya transporte:
   - intenta `PairingService.TryLoadPairingFromStore()` (F2-10: descifra el token con DPAPI y crea el transporte — reinicios y cortes de luz **no obligan a reemparejar**);
   - si no hay emparejamiento guardado (o el token no descifra), registra un aviso visible y **queda vivo** en `PairingWaiter.WaitForPairAsync()`: cuando `orvitaprint pair` llegue por la pipe, arranca heartbeat + poller **sin reiniciar** (ADR-13).
4. **Heartbeat inicial** (`HeartbeatSender.SendOnceAsync`): reporta destinos y sincroniza la configuración remota.
5. **Arranca el poller** (`PollerController.Start`).
6. **Se suscribe a `PairingWaiter.PairApplied`**: un re-pair con el worker en marcha reinicia el poller con el transporte nuevo y lanza un latido inmediato.
7. **Bucle de heartbeat** con `PeriodicTimer` (60 s por defecto): en cada tick envía el latido; si el `poll_interval` devuelto por el servidor cambió, reinicia el poller. **Ninguna excepción mata el bucle**: los fallos del tick se registran con ERROR y se reintenta en el siguiente intervalo (incidente 2026-11-23: el latido nunca debe morir en silencio).
8. **Parada**: ante `OperationCanceledException` del `stoppingToken` (SCM stop o Ctrl+C), en `finally` desuscribe el evento, para el poller limpiamente y registra «Agente Orvita Print detenido limpiamente».

### 3.4 Componentes de soporte del arranque

- **`ServerConnection`** — el transporte con el servidor, **mutable y thread-safe** (candado). Nace de la configuración bootstrap (desarrollo) o vacío; `SetTransport(url, token, …)` crea un `DirectTransport` y libera el anterior. El token vive en memoria clara solo dentro del `DirectTransport`. Una URL inválida se registra como error sin derribar el servicio.
- **`PairingWaiter`** — `SemaphoreSlim` de una señal: el worker espera en `WaitForPairAsync`; `PairingService.NotifyPaired()` lo despierta y dispara el evento `PairApplied` (una vez por pair aplicado; dos pairs seguidos son absorbidos con `SemaphoreFullException` controlada).
- **`AgentRuntimeStatus`** — estado observable para `status`/`doctor`: arranque del proceso, «última vez visto por el servidor» (último 200 de heartbeat), último intento y último error de latido, y el `poll_interval` devuelto por `/pair`.

---

## 4. Configuración en tres capas

Doc/03 §6.2.1 define tres capas; el servicio las implementa así:

### Capa estática (bootstrap) — `appsettings.json` + args

Solo infraestructura, sección `Orvita` → `AgentOptions`:

| Clave | Defecto | Significado |
|---|---|---|
| `DataDirectory` | `%ProgramData%\OrvitaPrint` | Raíz de datos (BD, logs, diagnostics, prints, license.dat) |
| `HeartbeatIntervalSeconds` | `60` | Cadencia del latido (Doc/04 §5.3) |
| `PollIntervalSeconds` | `10` | Intervalo de respaldo del poller hasta el primer `GET /config` |
| `TransientPrintRetries` | `3` | Reintentos locales ante fallo transitorio de impresión |
| `TransientRetryDelaySeconds` | `2` | Base de espera entre reintentos (2 s → 4 s → 6 s…) |
| `Update:Enabled` | `true` | Interruptor maestro del canal de actualización |
| `Update:ManifestUrl` | `""` | URL del `latest.json`; vacío → canal inactivo (sin red) |
| `Update:CheckIntervalHours` | `24` | Cadencia de comprobación |
| `Update:FirstCheckDelayMinutes` | `5` | Retardo del primer chequeo (no compite con el arranque) |
| `Server:Url` / `Server:Token` | `""` | Solo desarrollo; en producción llegan por la pipe |
| `Server:AllowInsecureHttp` | `false` | Solo desarrollo (S-03) |

Los **secretos nunca viven aquí** (S-01: el token va cifrado con DPAPI en SQLite).

### Capa local — almacén SQLite (F2-10)

`LocalConfigStore` sobre **el mismo fichero** que la cola: `outbox.db` (un fichero, un respaldo). Guarda el emparejamiento (URL + token cifrado DPAPI + metadatos del local) y las **salidas** (destinos) por rol. `StoreDestinationProvider` consulta el almacén **en cada llamada**, así que lo que cambia por la pipe (`destination add/remove`) es visible de inmediato para el procesador y el heartbeat, sin reiniciar nada.

### Capa remota — `GET /config` (Doc/04 §5.8)

`RemoteConfigState` mantiene la última configuración aplicada, con su `config_version`. Solo lo común al documento: `encoding` (por defecto CP850) y `paper.width_mm` (58/80 lo deduce el renderizador; anchos no estándar fijan columnas). El `poll_interval` remoto es **autoridad** sobre el de appsettings: lo aplica el servicio reiniciando el poller. El servidor avisa de cambios por el heartbeat (§9).

Resolución efectiva del intervalo de poll en cualquier momento:

```
poll_interval = RemoteConfigState.PollIntervalSeconds   (GET /config)
             ?? AgentRuntimeStatus.PairPollIntervalSeconds (POST /pair)
             ?? AgentOptions.PollIntervalSeconds        (appsettings: 10 s)
```

---

## 5. Emparejamiento (pair)

Flujo end-to-end (`PairingService`, F2-08 + F2-10): `orvitaprint pair --server <url> <código>` en un terminal → CLI → pipe de gestión → servicio.

1. **Validación de URL** (S-03): absoluta, http(s), **HTTPS obligatorio** salvo bandera de desarrollo explícita; normaliza la barra final del path.
2. **`POST /pair`** (Doc/04 §5.2) con: código de 6 dígitos, nombre del agente (defecto `TPV {MachineName}`), plataforma `windows`, versión (`AgentVersion.Current`) y las salidas ya configuradas (`DestinationWire` — garantiza `printer_name` no vacío: si una salida TCP solo tiene dirección, se reporta la dirección o el rol, porque el plugin descarta en silencio salidas sin impresora).
3. **Persistencia** (S-01): URL + token **CIFRADO con DPAPI** (`DpapiSecretProtector`: `ProtectedData`, ámbito `LocalMachine`, entropía fija `OrvitaPrint/v1/agent-token`) + local + fecha, en `LocalConfigStore`. El token claro no vuelve a usarse salvo para el transporte en memoria.
4. **Transporte en memoria**: `ServerConnection.SetTransport(...)` crea el `DirectTransport` — el poller arranca sin reiniciar el proceso.
5. **Señal al worker**: `PairingWaiter.NotifyPaired()` despierta al worker (primer pair) y dispara `PairApplied` (re-pair → reinicio de poller + latido inmediato).
6. **Respuesta al CLI sin eco del token** (S-05): solo `server_url`, local (id/código/nombre) y `poll_interval`.

Al **arrancar**, `TryLoadPairingFromStore()` reconstruye el transporte desde el almacén: descifra el token; si la URL guardada no vale o el token no descifra (cambió máquina/usuario/instalación), registra el error y el servicio sigue vivo esperando un nuevo pair.

El intervalo devuelto por `/pair` se guarda en `AgentRuntimeStatus.PairPollIntervalSeconds` hasta que el primer `GET /config` manda (§4).

---

## 6. El poller: cómo llegan los trabajos

### 6.1 `PollerController` (dueño del poller, F2-08)

- `Start(token)`: crea un `Poller` con el **transporte activo en ese instante** (accesor `Func<ITransport>` → re-pair ⇒ transporte nuevo) y lo ejecuta en una tarea enlazada al token de parada. Idempotente: si ya corre, no hace nada.
- `StopAsync()`: cancela y **espera** la finalización limpia. Un poller viejo que muera con error no propaga la excepción a quien pidió la parada (el bucle de heartbeat y el re-pair no pueden morir por un poller anterior accidentado).
- `RestartAsync(token)`: parar → arrancar. Lo usan el re-pair, `restart-poller` y el cambio de intervalo de poll.
- Si la tarea muere de forma inesperada → log ERROR. Si terminó por 401 → estado `StoppedUnauthorized` y aviso claro: «Reempareja el agente».

### 6.2 Bucle `Poller` (Core, Doc/03 §6.3, ADR-03)

```
loop:
  GET /jobs?cursor=…            (lote de trabajos con cursor)
  para cada trabajo: onJob(job) → JobProcessor.ProcessJobAsync
                                   (un fallo de un trabajo NO para la cola)
  backoff = inicial             (ronda limpia)
  espera = intervalo ± 20 % jitter
  ante error: espera = backoff; backoff *= 2 (tope 60 s)
```

Estados visibles (`PollerState`, para `orvitaprint status`):

| Estado | Significado |
|---|---|
| `Idle` | Creado pero no arrancado (o parado) |
| `Running` | Ciclo normal: fetch → procesar → esperar intervalo ± 20 % |
| `Backoff` | Error transitorio (red, 429, 5xx): esperando |
| `StoppedUnauthorized` | Detenido por 401: token inválido/revocado — requiere re-pair |
| `Stopped` | Detenido limpiamente por cancelación |

Gestión de errores de ronda:

- **401** (`orp_invalid_token`/`orp_token_revoked`) → poller **detenido** definitivamente.
- **429** (`orp_rate_limited`) → espera `Retry-After` si viene; si no, backoff (10 → 20 → 40 → 60 → 60…).
- **Error de red / 5xx / respuesta no contractual** (p. ej. un notice de PHP delante del JSON) → backoff; **el poller nunca muere en silencio** (incidente 2026-11-23).

---

## 7. Pipeline de un trabajo (JobProcessor)

Pipeline completo (Doc/03 §6.3, F2-03/F2-07). Entrada: un `Job` del lote del poller.

```
Job(job_id, printer_role)
   │
   ├─ 1. ResolveDestination(role)          ¿salida configurada y activa?
   │        NO → no se reclama: queda «pending» en el servidor (Doc/04 §5.8) y no se pierde
   │
   ├─ 2. POST /claim {job_id}              (transporte perezoso: agente sin emparejar = sin trabajos)
   │        409/410/404 → el trabajo ya no es nuestro (otro agente, caducado, ajeno):
   │                      se marca orp_job_not_claimed local y se descarta sin ack
   │
   ├─ 3. Enqueue idempotente en la cola local + guardado del documento neutro
   │        claim sin documento → orp_unsupported_document + ack failed
   │
   ├─ 4. TryClaim local (received → printing)
   │        ya en marcha/estado cambiado → no duplicar
   │        duplicate_suspected → log: «mejor un duplicado que un pedido perdido» (Doc/03 §6.4)
   │
   ├─ 5. Parseo del documento
   │        JSON malformado o version > 1 → orp_unsupported_document + ack failed
   │
   ├─ 6. Render ESC/POS
   │        normaliza logos (PNG base64 → raster 1bpp con .NET puro; un bloque
   │        indecodificable se avisa y se deja FUERA: nunca bytes sospechosos a la impresora)
   │        EscPosRenderer con opciones de RemoteConfigState (encoding CP850 por defecto, papel)
   │
   └─ 7. PrintAsync por IPrintTransportFactory (copias ≥ 1, Doc/04 §2.8)
            OK      → MarkDone local + POST /ack {delivered, attempt, printer, elapsed_ms}
            fallo TRANSIENTE (spooler ocupado, red)
                      → reintentos locales: espera 2 s·intento (2,4,6 s), hasta MaxTransientRetries
                      → agotados: POST /release {reason: "spooler_timeout"} SIN consumir intento
                                  (Doc/04 §5.7) + ResetToReceived local + no se reprocesa esta ronda
                                  (si el release falla: MarkError + ack failed)
            fallo DEFINITIVO (impresora inexistente, documento no soportado, otro)
                      → MarkError local + POST /ack {failed, error_code, error}
```

Semántica de fallos (Doc/04 §5.6/§5.7):

- **Transitorio** → reintentos locales; agotados → `POST /release` sin consumir intento; el trabajo vuelve a `received` local y se reintentará más tarde. Sin ack.
- **Definitivo** → ack `failed` con el código del agente: `orp_printer_not_found`, `orp_unsupported_document` o `orp_print_failed` (mapeo en `ErrorCodeWire`).
- **Recuperado tras caída** → se reimprime con marcador `duplicate_suspected` en la cola.
- **Rol sin salida configurada/desactivada** → no se reclama; permanece `pending` en el servidor.

`PendingLocalCount` (received + printing) y `LastError` alimentan el heartbeat.

---

## 8. La cola local (outbox)

`OutboxRepository` (Core) sobre SQLite en `<data>/outbox.db`, compartida con `LocalConfigStore` (un fichero, un respaldo). Estados locales:

```
received ── TryClaim ──► printing ── PrintAsync OK ──► done
    ▲                        │
    │                        ├─ PrintAsync fallo definitivo ──► error
    │                        └─ PrintAsync transitorio agotado ──► (release) ──► received
    └────────── RecoverInterrupted (arranque): printing → received, duplicate_suspected = 1
```

Operaciones usadas por el servicio: `RecoverInterrupted`, `Enqueue` (idempotente por job_id), `UpdateDocument`, `TryClaim`, `MarkDone`, `MarkError`, `ResetToReceived`, `GetByState`, `CountByState`, `Get`, `ListLatest` (GUI-02), `GetLatestJob`. El `DocumentJson` queda en la fila para reimpresiones y para la GUI (`queue.item` muestra texto legible, nunca el JSON crudo).

---

## 9. Heartbeat y configuración remota

`HeartbeatSender.SendOnceAsync` (Doc/04 §5.3), cada 60 s y uno inicial al emparejar:

1. Construye `HeartbeatRequest`:
   - `agent_version` (`AgentVersion.Current`, de `AssemblyInformationalVersion`; `TryParseCurrentVersion` recorta sufijos `–dev`/`+build` para comparaciones de update);
   - `pending_local` (trabajos `received` + `printing` de la cola);
   - `last_error` (mensaje del trabajo en `error` más reciente);
   - `destinations` (todas las salidas, upsert por role en el servidor, vía `DestinationWire`).
2. `POST /heartbeat` por el transporte concreto (`DirectTransport`).
3. Si la respuesta trae `config_version` distinta de la aplicada → `GET /config` y `RemoteConfigState.Update(config)` (encoding, papel, `poll_interval`).
4. Registra en `AgentRuntimeStatus` el «última vez visto por el servidor» o el error del latido.

Manejo en el worker (`SendHeartbeatSafelyAsync`): ante `TransportUnauthorizedException` → ERROR visible con instrucción de re-pair, **el poller queda detenido y el servicio sigue vivo** para poder emparejar de nuevo; ante cualquier otro fallo → WARNING y el bucle sigue.

---

## 10. Pipe de gestión y CLI

### 10.1 `ManagementPipeServer` (F2-08, ADR-13)

Hosted service que escucha la pipe con nombre fijo y documentado:

```
\\.\pipe\orvitaprint-management
```

- **Protocolo**: JSON de líneas — una solicitud (`{"id", "op", "args"}`), una respuesta (`{"id", "ok", ...}`), correlación por id; clientes secuenciales. Una conexión atiende tantas solicitudes como mande el cliente antes de cerrar.
- **Una conexión cada vez**: `maxNumberOfServerInstances = 1`; un segundo cliente simultáneo recibe `ERROR_PIPE_BUSY` (el CLI lo traduce). Un cliente colgado o una línea rota: se registra y se vuelve a aceptar — el servicio no cae.
- **Seguridad (ACL)**: solo el usuario que arranca el servicio, Administradores, LocalSystem y NetworkService tienen acceso (S-05/F3-01). **El token JAMÁS viaja por la pipe.**
- `ProcessLineAsync` nunca lanza: JSON inválido → respuesta de error `invalid_request` con id 0.

### 10.2 Operaciones (`ManagementRequestHandler`)

Despachador sin estado propio: cada operación lee almacén/cola/transporte al vuelo, de modo que responde siempre lo que el servicio vive en ese instante. Nunca lanza: los errores van como respuesta `ok:false` (`PairingException` → su código; `ArgumentException` → `invalid_argument`; `PrintTransportException` → `print_error`; resto → `internal_error`).

| Operación | Qué hace |
|---|---|
| `ping` | `pong: true` + versión del agente |
| `pair` | Validación de URL y código → `PairingService.ApplyPairingAsync`. Respuesta sin eco del token (S-05) |
| `destination.add` | Crea/actualiza una salida: `role` (defecto `default`), `label`, `printer` o `address`, `copies` (clamp 1–99), `enabled`, `kind` (`spooler`/`tcp9100`/`pdf`/`gdi`). Validaciones: salida `pdf` fuerza impresora «Microsoft Print to PDF» y directorio por defecto (`%ProgramData%\OrvitaPrint\prints\`); `tcp9100` exige `--address host[:puerto]`. Avisos (no bloquean, la salida se guarda igual): impresora no encontrada entre las instaladas, ninguna impresora instalada, y dos roles apuntando a la misma impresora (para dos copias está `--copies`) |
| `destination.list` | Salidas configuradas + impresoras instaladas (nombre, ¿por defecto?, hint de estado) + impresora por defecto |
| `destination.remove` | Quita una salida por rol |
| `status` | Informe completo: versión, plataforma, arranque, emparejamiento (URL sin token, S-05), local, `last_seen_by_server`, último intento/error de heartbeat, estado del poller, `config_version`, contadores de cola, último trabajo, último error de impresión, destinos, impresoras y licencia |
| `test` | Impresión de prueba (§11) |
| `doctor` | Resumen de diagnóstico (versión, runtime, OS, rutas de datos/BD/logs, emparejamiento, poller, cola, impresoras, licencia). Con `--zip` genera el ZIP de diagnóstico y devuelve **solo la ruta** (F2-12: los binarios nunca viajan por la pipe) |
| `restart-poller` | Reinicia el poller (requiere estar emparejado; usa el transporte activo) |
| `queue.list` | Cola local pura para la GUI de bandeja (GUI-02, Doc/08 §3): `limit` (defecto 50, tope 200) + `total_jobs`; **sin** `document_json` |
| `queue.item` | Detalle de un trabajo: campos + `document_parse_ok` + `document_text` (texto legible del documento neutro) |
| desconocida | `unknown_operation` |

El CLI (`Orvita.Print.Cli`, `orvitaprint …`) es un cliente de esta pipe: `pair`, `destination add/list/remove`, `status`, `test`, `doctor [--zip]`, `restart-poller`, `queue list/item`.

---

## 11. Impresión de prueba (test)

`TestPrintService.PrintAsync(role)` (F2-05, Doc/03 §9 y §6.5):

1. Resuelve la salida del rol (o `default`); inexistente o desactivada → `PrintTransportException` con mensaje de ayuda («configúrala con orvitaprint destination add»).
2. Genera el documento fijo `TestTicket` (con la etiqueta de la salida) y lo renderiza con `EscPosRenderer` usando las opciones de la configuración remota — el mismo renderizador que un trabajo real.
3. Lo envía por el **mismo transporte** que un trabajo real (`WindowsPrintTransportFactory`), con las copias configuradas de la salida.
4. Devuelve destino, copias, ms y bytes impresos (lo muestra `orvitaprint test`).

---

## 12. Diagnóstico (doctor --zip)

`DiagnosticsBundleBuilder` (F2-12, Doc/03 §11) genera:

```
%ProgramData%\OrvitaPrint\diagnostics\orvitaprint-<timestamp>.zip
   ├── logs\orvitaprint-YYYYMMDD.log    (los últimos 7 días; lectura con FileShare.ReadWrite
   │                                     porque Serilog mantiene abierto el fichero del día)
   └── diagnostics.json
```

`diagnostics.json` incluye: agente (versión, runtime .NET, OS, rutas de instalación y datos), emparejamiento (paired, `server_url`, local, nombre, fecha, conectado — **S-05: sin token ni blob DPAPI**), servidor (`last_seen_by_server`, intento y error de heartbeat), poller (estado y running), config (`config_version`, intervalo), updates (última comprobación/resultado/versión/error), licencia, cola (contadores por estado + últimas 50 filas **sin el documento**: solo job_id, rol, copias, estado, intentos, `duplicate_suspected`, errores y fechas — los datos del cliente del documento JAMÁS se exportan), últimos 10 errores con código, destinos e impresoras.

---

## 13. Licencia

`LicenseState` (F3-02, Doc/03 §10): se carga **UNA VEZ al arrancar** desde `<data>\license.dat` y se valida **localmente** con la clave pública ECDsa **embebida** (`LicenseTrust`) — sin conexión nunca. Snapshot inmutable, hilo-seguro por construcción; **nunca lanza**.

| Estado | Significado |
|---|---|
| `missing` | Sin fichero: el agente **funciona** (equivalente al SKU de 1 local, política piloto V1); el estado lo dice claro en `status`/`doctor` |
| `valid` | Informa sku, max_locations, updates_until y cliente |
| `expired_updates` | Licencia válida; SOLO deja de aceptar actualizaciones automáticas |
| `invalid` | Fichero corrupto o firma inválida: se registra con su código (`license_invalid_format`/`license_signature_invalid`/`license_read_failed`) y el agente sigue en modo «sin licencia» |

Cambiar de licencia = copiar la nueva y **reiniciar el servicio**. La aplicación comercial real de la licencia vive en el servidor; el fichero nunca bloquea emparejamiento ni impresión.

---

## 14. Actualizaciones automáticas

Cadena de responsabilidades (F2-11, Doc/03 §10):

```
UpdateService (BackgroundService, 1×/día)
   │  primer chequeo a los 5 min; luego cada CheckIntervalHours (24 h)
   │  licencia con updates_until pasado → NO comprueba (el agente sigue funcionando)
   ▼
UpdateOrchestrator.CheckAndApplyAsync        (aislado para pruebas; nunca lanza hacia fuera)
   │  1. versión actual parseable (si no: canal inactivo, visible en UpdateStatus)
   │  2. UpdateChecker.CheckAsync(version):
   │       GET latest.json (URL estática, bootstrap) → verifica firma Ed25519 con la clave
   │       pública EMBEBIDA (UpdateTrust, NO configuración) → compara versiones →
   │       descarga y verifica el MSI → PreparedUpdate
   │       null → RecordUpToDate
   │  3. fallo → RecordFailure con código + WARNING; versión actual intacta; reintento diario
   ▼
UpdateApplier.ApplyAsync (Windows-only, decisión documentada)
   │  1. PollerController.StopAsync()      — ninguna impresión en vuelo mientras el MSI sustituye binarios
   │  2. ManagementPipeServer.StopAsync()  — el CLI responde «servicio no disponible» desde entonces
   │  3. msiexec /i <msi> /qn /norestart   — /norestart: nunca un reinicio de máquina sorpresivo en un TPV
   │       exit 0    → el Windows Installer detiene este proceso, sustituye el binario y ARRANCA
   │                   el servicio: «se reinicia solo» con la versión nueva
   │       exit 3010 → éxito con reinicio requerido: la versión nueva llega al reiniciar la máquina
   │       otro      → UpdateException: se conserva la versión actual y el ciclo diario reintenta
   │  finally: borra el MSI temporal (best effort)
```

Todo queda registrado en `UpdateStatus` (última comprobación, resultado: `up_to_date`/`applied`/`reboot_required`/`failed`/`disabled`, versión vista, error) y es visible en el diagnóstico y en `doctor`.

Si el canal no está configurado (`ManifestUrl` vacío) o está desactivado (`Update:Enabled=false`), `UpdateService` queda inactivo con un aviso claro en el log.

---

## 15. Modelo de seguridad

| Regla | Implementación |
|---|---|
| **S-01** Token cifrado en reposo | `DpapiSecretProtector`: `ProtectedData.Protect` con `DataProtectionScope.LocalMachine` + entropía fija `OrvitaPrint/v1/agent-token`. Es la **única** pieza que conoce DPAPI; la interfaz `ISecretProtector` vive en Core (portable, ADR-12). El token solo se descifra en esta máquina, y solo el servicio tiene que hacerlo (el CLI nunca lo lee: todo pasa por la pipe) |
| **S-03** HTTPS obligatorio | `PairingService.ValidateServerUrl`: HTTP solo con la bandera explícita `--allow-insecure-http` (desarrollo, wp-env). El servicio no confía en el CLI: revalida siempre |
| **S-05** El token nunca se expone | No va al log (solo la URL, que no es secreta), no viaja por la pipe (pair responde sin eco; `status` lo oculta), y no está en el ZIP de diagnóstico (auditado campo a campo en `DiagnosticsBundleBuilder.BuildReport`) |
| **ACL de la pipe** | Solo usuario del servicio + Administradores + LocalSystem + NetworkService (F3-01) |
| **Permisos del directorio de datos** | F3-01 (MSI); la seguridad real del DPAPI descansa en LocalMachine + estos permisos |
| **Claves públicas embebidas** | La de actualizaciones (`UpdateTrust.ReleasePublicKeyBase64`) y la de licencias (`LicenseTrust`) NO son configuración: quien pudiera cambiarlas invalidaría la cadena |

---

## 16. Logging y observabilidad

- **Serilog**: consola (`HH:mm:ss.fff [LVL] mensaje`) + fichero diario `<data>\logs\orvitaprint-YYYYMMDD.log` con rotación diaria y **7 ficheros retenidos** (Doc/03 §11). Nivel desde `Serilog:MinimumLevel` (defecto `Information`).
- **Estado en vivo** (`orvitaprint status`): poller, emparejamiento, `last_seen_by_server`, contadores de cola, último trabajo y último error — servido por la pipe desde `AgentRuntimeStatus`, `PollerController`, `RemoteConfigState` y los repositorios.
- **`orvitaprint doctor`**: resumen técnico + `--zip` para el paquete de soporte (§12).
- Incidentes documentados en el código como recordatorio: el bucle de heartbeat y el poller **nunca mueren en silencio** (2026-11-23); el contrato exige `printer_name` no vacío o el plugin descarta salidas sin rastro (2026-11-23).

---

## 17. Resumen de tiempos y valores por defecto

| Parámetro | Valor | Dónde |
|---|---|---|
| Heartbeat | 60 s | `AgentOptions.HeartbeatIntervalSeconds` (Doc/04 §5.3) |
| Poll base | 10 s ± 20 % jitter (ADR-03) | `PollIntervalSeconds` → `PollerOptions` |
| Backoff del poller | 10 → 20 → 40 → 60 → 60… s (tope 60) | `PollerOptions` |
| Reintentos transitorios de impresión | 3, esperas 2/4/6 s | `TransientPrintRetries`/`TransientRetryDelaySeconds` |
| Actualizaciones | 1ª a los 5 min, luego cada 24 h | `Update:*` |
| Logs | rotación diaria, 7 días | Serilog file sink |
| ZIP de diagnóstico | logs de 7 días + 50 trabajos | `DiagnosticsBundleBuilder` |
| Directorio de datos | `%ProgramData%\OrvitaPrint` | `AgentOptions.ResolveDataDirectory` |
| Pipe de gestión | `\\.\pipe\orvitaprint-management`, 1 instancia | `ManagementProtocol.PipeName` |
| Nombre del servicio Windows | `OrvitaPrint` | `AgentHostBuilder.ServiceName` |

---

## 18. Inventario de ficheros del proyecto

| Fichero | Responsabilidad |
|---|---|
| `Program.cs` | Punto de entrada mínimo: `AgentHostBuilder.BuildHost(args)` + `RunAsync` |
| `AgentHostBuilder.cs` | Cableado completo del host: logging, DI, hosted services, `AddWindowsService`, argumentos de desarrollo |
| `AgentOptions.cs` | Opciones bootstrap (`ServerOptions`, `UpdateServiceOptions`, `AgentOptions`) y resolución del directorio de datos |
| `AgentWorker.cs` | Orquestador principal: recuperación de cola, drenaje, espera de pair, heartbeat inicial, poller, bucle de latidos, re-pair |
| `AgentRuntimeStatus.cs` | Estado observable: arranque, última vez visto por el servidor, errores de latido, intervalo de pair |
| `ServerConnection.cs` | Transporte mutable con el servidor (pair/re-pair sin reiniciar), thread-safe |
| `PairingService.cs` | Flujo de pair end-to-end + carga del pair guardado + validación S-03 |
| `PairingWaiter.cs` | Señal worker↔pairing y evento `PairApplied` |
| `PollerController.cs` | Dueño del poller: start/stop/restart, estados visibles |
| `JobProcessor.cs` | Pipeline completo de un trabajo (claim → render → print → ack/release) |
| `HeartbeatSender.cs` | `POST /heartbeat` + re-sincronización `GET /config`; `AgentVersion` |
| `RemoteConfigState.cs` | Configuración remota aplicada (`config_version`, encoding, papel, poll) |
| `ManagementPipeServer.cs` | Servidor de la pipe de gestión (protocolo JSON-líneas, ACL, 1 instancia) |
| `ManagementRequestHandler.cs` | Despachador de operaciones: ping/pair/destinations/status/test/doctor/queue/restart-poller |
| `TestPrintService.cs` | Impresión de prueba con el ticket fijo |
| `StoreDestinationProvider.cs` | `IDestinationProvider` sobre el almacén SQLite (lectura en cada llamada) |
| `IDestinationProvider.cs` | Interfaz de salidas + `DestinationWire` (regla `printer_name` no vacío) |
| `OutboxRepository` (Core) | Cola local SQLite y sus estados |
| `LicenseState.cs` | Carga y validación local de `license.dat`; política piloto V1 |
| `UpdateService.cs` | Temporizador diario del canal de actualización |
| `UpdateOrchestrator.cs` | Un ciclo de comprobación (aislable en pruebas) |
| `UpdateApplier.cs` | Parada de poller/pipe + `msiexec /qn /norestart` |
| `UpdateStatus.cs` | Estado del canal de updates para diagnóstico |
| `DpapiSecretProtector.cs` | DPAPI LocalMachine + entropía fija (S-01) |
| `DiagnosticsBundleBuilder.cs` | ZIP de diagnóstico (logs + diagnostics.json) |
| `ErrorCodeWire.cs` | `ErrorCode` del contrato → código de cable para el ack |

Pruebas: `agent/tests/Orvita.Print.Service.Tests` cubre el host (humo), el worker/heartbeat, el pipeline de trabajos, la pipe y el handler de gestión, el pair y su carga, el poller y su controlador, updates (orquestador/aplicador), licencia, DPAPI y el ZIP de diagnóstico.

---

*Fin del documento. Para el contrato REST y el formato del documento neutro, véase Doc/04; para la arquitectura general y los ADRs, Doc/03; para el plan de fases, Doc/07.*
