# Orvita Print — Agente (.NET 10)

Solución del agente (Doc/03 §6, Doc/07 §2.2). Estado actual: **F2 completa y F3 completa**
(gestión por CLI sobre pipe de gestión, almacén local con token DPAPI, canal de actualización
firmado Ed25519 con rollback, doctor ZIP, instalador MSI por máquina con servicio auto-start,
licencia ECDSA P-256 validada offline y herramientas de release).

## Proyectos

| Proyecto | Tipo | Contenido |
|---|---|---|
| `src/Orvita.Print.Contracts` | lib | Modelos del contrato congelado (Doc/04): documento neutro v1 (§7), `Job`/`JobBatch`/`JobResult`, códigos de error (§6) con sus dos formas de cuerpo, y modelos REST (health, heartbeat, config, pair). |
| `src/Orvita.Print.Core` | lib portable | `ITransport` (Doc/03 §7), `DirectTransport` (HttpClient, Bearer, HTTPS obligatorio — S-03, `release` §5.7), `Poller` (10 s ± 20 % jitter, backoff 10→20→40→60 s, parada en 401), cola SQLite `OutboxRepository` (recuperación Doc/03 §6.4), renderizador ESC/POS (`EscPosRenderer`/`EscPosBuilder`) y el contrato portátil de impresión: `IPrintTransport`, `IPrinterEnumerator`, `IPngDecoder`, `PrintTransportException`. Además F2-08/F2-10: `LocalConfigStore` (almacén SQLite: ServerUrl + token protegido + salidas, MISMA BD que la cola), `PairingClient` (POST /pair), `ISecretProtector` (portable) y el protocolo JSON de la pipe `ManagementProtocol` (`orvitaprint-management`). **Sin nada de Windows** (ADR-12). |
| `src/Orvita.Print.Printing.Windows` | lib (net10.0-windows) | La ÚNICA pieza que toca APIs de Windows de impresión (ADR-12): spooler RAW (`winspool.drv` P/Invoke, datatype RAW), respaldo GDI (`System.Drawing.Printing`), TCP 9100, selector Auto RAW→GDI, enumeración de impresoras (`EnumPrinters` nivel 2 + `GetDefaultPrinter`), decodificador PNG puro (DeflateStream, sin System.Drawing) y el ticket de prueba fijo. |
| `src/Orvita.Print.Service` | exe (net10.0-windows) | Servicio headless (ADR-09/13): worker + `UseWindowsService`, pipeline claim→render→imprimir→ack, heartbeat 60 s con re-sincronización por `config_version`, logs Serilog con rotación diaria (7 días). F2-08: servidor de la pipe de gestión (`ManagementPipeServer`, ACL restrictiva, una conexión cada vez) con operaciones `pair`, `destination add/list/remove`, `status`, `test`, `doctor`, `restart-poller`; `PairingService` (pair → almacén con token DPAPI → transporte en caliente) y `PollerController` (re-pair/reinicio sin tirar el proceso). DPAPI (`ProtectedData`, ámbito `LocalMachine` + entropía fija) vive aquí (S-01). |
| `src/Orvita.Print.Cli` | exe (net10.0-windows) | `orvitaprint` (F2-09, ADR-13): cliente de la pipe de gestión — `pair`, `destination add/list/remove`, `status`, `test`, `doctor`, `restart-poller`. Salidas en español, `--help` por comando y códigos de salida para scripts (0 ok · 1 fallo · 2 uso · 3 servicio no en ejecución). NUNCA lee el almacén: todo por la pipe (S-05). |
| `src/Orvita.Print.Gui` | exe (net10.0) | **GUI de bandeja (Doc/08, post-V1)**: Avalonia 11 + MVVM, ventana única con pestañas (Cola, Salidas, Emparejamiento, Diagnóstico), icono de bandeja con los 5 estados, globos, instancia única. OTRO cliente de la pipe (ADR-13): ops `pair`, `destination.*`, `status`, `test`, `doctor` + las aditivas `queue.list`/`queue.item` (lectura pura del outbox) y `pair.remove` (desemparejar). Sin ventana abierta, cero polling de cola; el token no existe para la GUI (S-05). Ver su `README.md` (guion de verificación manual incluido). |
| `tests/Orvita.Print.Core.Tests` | xUnit | 235 pruebas sin servidor real ni impresora (incluye `DocumentText`, la vista legible del documento neutro para la GUI). |
| `tests/Orvita.Print.Printing.Windows.Tests` | xUnit | 44 pruebas: PNG (fixtures generados en la prueba), TCP 9100 con `TcpListener` capturando bytes, selección RAW/GDI/transitorios, layout GDI, spooler/enumerador reales sin hardware. |
| `tests/Orvita.Print.Service.Tests` | xUnit | 111 pruebas: pipeline completo con servidor y transporte de impresión falsos, outbox (claim por id, reset, `queue.list`/`queue.item` de la GUI), release HTTP, heartbeat y re-sincronización, humo del host en consola, DPAPI real (Windows), flujo de pair con HTTP falso (200/400/410/429 + S-03), handler de la pipe (status oculta el token, avisos de destination, unpair), servidor de pipe REAL in-proceso con cliente real y licencia (estados missing/valid/expired/invalid, cable en status/doctor, canal de updates bloqueado). |
| `tests/Orvita.Print.Cli.Tests` | xUnit | 24 pruebas: parser de subcomandos y opciones, validación S-03 en el CLI, ayuda por comando, códigos de salida y clasificación de errores de pipe. |
| `tests/Orvita.Print.Gui.Tests` | xUnit + headless | 85 pruebas: lógica pura de la GUI (mapeo de bandeja, estados de cola, filtros, balones), viewmodels con doble de pipe (auto-refresco solo con ventana, banner de errores no vistos, CRUD de salidas, pair/unpair con S-03), roundtrip del canal de la GUI contra un servidor de pipe REAL en proceso, y humo headless de la ventana. |
| `tools/Orvita.Print.ReleaseTools` | exe (net10.0) | **Herramienta de release NUESTRA, NO se distribuye en el MSI** (F3-02/F3-03): `orvita-release genkey` (ECDSA P-256 licencias + Ed25519 manifiestos), `genlicense` y `genmanifest`. Claves privadas FUERA del repositorio y del servidor (ver su README). |
| `installer/` | WiX | **MSI por máquina (F3-01)**: registro del servicio auto-start, ACL restrictiva de `%ProgramData%\OrvitaPrint`, property pública `ORVDATA`, major-upgrade con UpgradeCode estable, SHA-256 del MSI en `artifacts/checksum.txt`. Compilar con `build.ps1` (publica self-contained win-x64, cosecha y llama a `wix build`); validar con `build.ps1 -Validate` (`msiexec /a`) y `audit-msi.ps1`. |
| `docs/guia-instalacion.md` | doc | **Guía de instalación para el profesional (F3-04)**: prerrequisitos, verificación SHA-256, aviso SmartScreen, pair, salidas, test, diagnóstico, desinstalación y updates. |

`tools/Smoke/`: consola de humo del contrato contra el wp-env real (no está en la solución).

## Compilar y probar

```powershell
cd agent
dotnet build
dotnet test   # 509 pruebas: 235 + 54 + 111 + 24 + 85 (GUI)
```

El SDK está fijado en `global.json` (10.0.200 estable, `rollForward latestFeature`,
`allowPrerelease false` — excluye la preview de .NET 11 que haya instalada).

Objetivo `net10.0` (net10.0-windows donde toca), `x64` documentado en `Directory.Build.props`
para publicación self-contained posterior (ADR-10).

## Ejecutar el servicio en modo consola (desarrollo/humo, sin instalar nada)

El MISMO ejecutable es servicio Windows y consola (`AddWindowsService` solo aplica su ciclo
de vida cuando el proceso es un servicio real). Para fumar-test sin instalar:

```powershell
dotnet run --project src/Orvita.Print.Service -- --data-dir .\smoke --console
```

- `%ProgramData%\OrvitaPrint\` es la ruta por defecto de datos (Doc/03 §6.2); `--data-dir` la
  cambia sin tocar nada más.
- El token de los argumentos de desarrollo no se registra nunca (S-05). En producción llega
  del emparejamiento y vive cifrado en SQLite.

## Gestionar el agente: `orvitaprint` (F2-09)

```powershell
# Emparejar (la URL y el token quedan en SQLite, token cifrado con DPAPI — S-01):
orvitaprint pair --server https://mi-tienda.es/wp-json/orp-print/v1 482913
# (HTTP sin TLS solo en desarrollo, con la bandera explícita — S-03:)

# Salidas (Doc/03 §6.7): nombre EXACTO de la lista, copias y rol único:
orvitaprint destination list                      # salidas + impresoras instaladas (nombres exactos)
orvitaprint destination add --printer "EPSON TM-T20III" --copies 2 --label "Mostrador"
orvitaprint destination remove --role default

# Estado, prueba y diagnóstico:
orvitaprint status                                # emparejamiento (token OCULTO, S-05), poller, cola, último error
orvitaprint test --role default                   # ticket de prueba por el mismo transporte que un trabajo real
orvitaprint doctor                                # resumen (versiones, rutas, impresoras, cola)
orvitaprint restart-poller                        # avanzado: reinicia el bucle sin tirar el servicio
```

Códigos de salida: `0` ok, `1` operación fallida (p. ej. código de pair caducado), `2` uso
incorrecto, `3` servicio no en ejecución. Si el servicio no corre, el CLI lo dice con ese
texto exacto y sale con 3.

El emparejamiento se aplica EN CALIENTE: el worker despierta, envía el heartbeat con las
salidas del almacén y arranca el poller sin reiniciar el proceso. Al reiniciar el servicio,
el emparejamiento se recarga del almacén (token DPAPI → memoria, nunca a disco plano).

## Instalación por máquina: el MSI (F3-01, requiere admin)

```powershell
# Compilar (publica self-contained win-x64, cosecha los ficheros, valida con ICE y deja el SHA-256):
powershell installer\build.ps1          # → installer\artifacts\OrvitaPrint-<versión>.msi + checksum.txt

# Validar SIN instalar en esta máquina (extracción administrativa, no requiere admin):
powershell installer\build.ps1 -SkipPublish -Validate
powershell installer\audit-msi.ps1 -MsiPath installer\artifacts\OrvitaPrint-1.0.0.msi
```

El MSI instala los binarios en `C:\Program Files\Orvita Print\` (service\, cli\ + PATH y gui\ —
la **GUI de bandeja**, GUI-07), registra el **servicio Windows OrvitaPrint con arranque
automático**, crea `%ProgramData%\OrvitaPrint` con ACL restrictiva (SYSTEM/Administradores
control total, Usuarios solo lectura — el token no lo toca nadie más, S-01) y acepta la
property pública `ORVDATA` para reubicar los datos. Major-upgrade: instalar encima reemplaza
la versión anterior; desinstalar **conserva** `%ProgramData%\OrvitaPrint` (emparejamiento y
licencia intactos). Sin firma de código en V1 (Doc/03 §9): el SHA-256 se publica junto a la
descarga y la guía documenta el aviso de SmartScreen.

Nota GUI (GUI-07): la GUI no es un servicio — es un exe de sesión de usuario; el auto-arranque
con la sesión (HKCU Run) lo aplica la GUI en su primer arranque, porque un MSI per-máquina no
puede escribir la clave `Run` de cada usuario (desviación documentada en Doc/08 §5 y en el
comentario de `Product.wxs`).

Para probar en sesión 0 SIN MSI, los comandos manuales siguen sirviendo:

```powershell
sc.exe create OrvitaPrint binPath= "C:\Program Files\Orvita Print\service\Orvita.Print.Service.exe" start= auto DisplayName= "Orvita Print Agent"
sc.exe description OrvitaPrint "Agente de impresión térmica de Orvita Print (WooCommerce)"
sc.exe failure OrvitaPrint reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc.exe start OrvitaPrint
```

## Licencia del agente (F3-02) y canal de actualizaciones (F3-03)

- La licencia (`license.dat`, JSON firmado con **ECDSA P-256**) se copia a
  `%ProgramData%\OrvitaPrint\license.dat` y el servicio la valida al arrancar **offline** (clave
  pública EMBEBIDA en `LicenseTrust`). Estado visible en `status`/`doctor`:
  **sin licencia el agente funciona con 1 local** (política V1 del piloto); con
  `updates_until` pasado sigue funcionando y solo deja de aceptar actualizaciones.
- El canal de actualización (F2-11) comprueba `latest.json` firmado con **Ed25519** una vez al
  día y aplica el MSI solo si firma y SHA-256 verifican; cualquier fallo conserva la versión.
- Las claves de firma y el flujo completo (genkey/genlicense/genmanifest → subir MSI + latest.json
  + checksum) están en `tools/Orvita.Print.ReleaseTools/README.md`. Las privadas viven FUERA del
  repo; las públicas de release están embebidas (`UpdateTrust`/`LicenseTrust`).

## Prueba end-to-end contra wp-env

```powershell
# token nuevo (se muestra una sola vez):
cd ..\orp-print
npx --yes @wordpress/env@latest run cli wp orp seed --reset

cd ..\agent
dotnet run --project tools/Smoke -- http://localhost:8888/wp-json/orp-print/v1 orp_<token>
```

Ejercita `GET /health`, `GET /config`, `GET /jobs`, `POST /jobs/{id}/claim`,
`POST /jobs/{id}/ack` (más un segundo ack idempotente y un claim 404). El token falso
produce `TransportUnauthorizedException` (401 → el poller se detiene).

## Lote físico H1 — verificación manual pendiente de hardware (Doc/05 §4.4)

Automatizable NO es: la impresión real en térmicas se verifica a mano en las 3 impresoras
de la matriz (hitos H1/F2 y F4-02). Checklist por cada impresora (Epson TM-T20III, Epson
TM-m30, Xprinter XP-58/80):

1. **Instalación**: impresora instalada en Windows con su driver; `EnumPrinters` la lista con
   su nombre EXACTO (anotar el nombre para la salida).
2. **Test de salida**: `orvitaprint test` (con F2-09) o el documento de prueba del servicio →
   debe salir el ticket fijo: cabecera doble y negrita, kv alineado, tabla de productos,
   totales, la cadena «Pizza Margarita · jamón, piña, ñ, €, áéíóú» y corte parcial.
3. **Codificación**: repetir con `encoding` CP858 (el € sale como €) y CP437 (sustituciones
   seguras €→E y aviso en log, sin caracteres rotos) — Doc/05 §4.3.
4. **Copias**: salida con `copies = 3` → TRES copias en un solo documento del spooler
   (una sola fila en la cola de impresión).
5. **Fallback GDI**: en una impresora con driver que rechace RAW (forzar datatype no RAW en
   el driver si se puede) → el selector Auto imprime por GDI y lo registra.
6. **TCP 9100**: salida con `Kind=Tcp9100` y `Address=ip:9100` → imprime directo sin driver.
7. **Estado como pista**: apagar la impresora → el estado aparece en la enumeración como
   pista («Desconectada») pero la impresión NO se bloquea por ello; falla con
   `orp_print_transient` → release y reintento (Doc/04 §5.7).
8. **Cajón**: documento con bloque `drawer` → pulso de apertura (pin 2, 50 ms).
9. **Logo PNG**: plantilla con logo PNG base64 (≤ 384 px) → raster GS v 0 legible, sin ruido.
10. **Sesión 0**: servicio instalado y arrancado sin sesión iniciada (F4-02 lo repite con
    corte de luz y TPV real).
