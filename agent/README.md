# Orvita Print — Agente (.NET 10)

Solución del agente (Doc/03 §6, Doc/07 §2.2). Estado actual: **F2-01…F2-05 y F2-07** (faltan pipe F2-08, CLI F2-09, almacén F2-10, update F2-11, doctor F2-12).

## Proyectos

| Proyecto | Tipo | Contenido |
|---|---|---|
| `src/Orvita.Print.Contracts` | lib | Modelos del contrato congelado (Doc/04): documento neutro v1 (§7), `Job`/`JobBatch`/`JobResult`, códigos de error (§6) con sus dos formas de cuerpo, y modelos REST (health, heartbeat, config, pair). |
| `src/Orvita.Print.Core` | lib portable | `ITransport` (Doc/03 §7), `DirectTransport` (HttpClient, Bearer, HTTPS obligatorio — S-03, `release` §5.7), `Poller` (10 s ± 20 % jitter, backoff 10→20→40→60 s, parada en 401), cola SQLite `OutboxRepository` (recuperación Doc/03 §6.4), renderizador ESC/POS (`EscPosRenderer`/`EscPosBuilder`) y el contrato portátil de impresión: `IPrintTransport`, `IPrinterEnumerator`, `IPngDecoder`, `PrintTransportException`. **Sin nada de Windows** (ADR-12). |
| `src/Orvita.Print.Printing.Windows` | lib (net10.0-windows) | La ÚNICA pieza que toca APIs de Windows (ADR-12): spooler RAW (`winspool.drv` P/Invoke, datatype RAW), respaldo GDI (`System.Drawing.Printing`), TCP 9100, selector Auto RAW→GDI, enumeración de impresoras (`EnumPrinters` nivel 2 + `GetDefaultPrinter`), decodificador PNG puro (DeflateStream, sin System.Drawing) y el ticket de prueba fijo. |
| `src/Orvita.Print.Service` | exe (net10.0-windows) | Servicio headless (ADR-09/13): worker + `UseWindowsService`, pipeline claim→render→imprimir→ack, heartbeat 60 s con re-sincronización por `config_version`, logs Serilog con rotación diaria (7 días). Sin UI; la pipe de gestión llega en F2-08. |
| `tests/Orvita.Print.Core.Tests` | xUnit | 141 pruebas sin servidor real ni impresora. |
| `tests/Orvita.Print.Printing.Windows.Tests` | xUnit | 44 pruebas: PNG (fixtures generados en la prueba), TCP 9100 con `TcpListener` capturando bytes, selección RAW/GDI/transitorios, layout GDI, spooler/enumerador reales sin hardware. |
| `tests/Orvita.Print.Service.Tests` | xUnit | 27 pruebas: pipeline completo con servidor y transporte de impresión falsos, outbox (claim por id, reset), release HTTP, heartbeat y re-sincronización, y humo del host en consola. |

`tools/Smoke/`: consola de humo del contrato contra el wp-env real (no está en la solución).

## Compilar y probar

```powershell
cd agent
dotnet build
dotnet test   # 212 pruebas: 141 + 44 + 27
```

Objetivo `net10.0` (net10.0-windows donde toca), `x64` documentado en `Directory.Build.props`
para publicación self-contained posterior (ADR-10).

## Ejecutar el servicio en modo consola (desarrollo/humo, sin instalar nada)

El MISMO ejecutable es servicio Windows y consola (`AddWindowsService` solo aplica su ciclo
de vida cuando el proceso es un servicio real). Para fumar-test sin instalar:

```powershell
dotnet run --project src/Orvita.Print.Service -- --data-dir .\smoke --console
# Emparejado contra wp-env (HTTP solo en desarrollo, S-03):
dotnet run --project src/Orvita.Print.Service -- `
  --data-dir .\smoke --server http://localhost:8888/wp-json/orp-print/v1 --token orp_XXX --allow-insecure-http
```

- `%ProgramData%\OrvitaPrint\` es la ruta por defecto de datos (Doc/03 §6.2); `--data-dir` la
  cambia sin tocar nada más.
- Salidas de desarrollo en `appsettings.json` (`Orvita:Destinations`): rol, impresora EXACTA,
  copias, modo (`Auto`/`RawSpooler`/`Gdi`/`Tcp9100` + `Address` host:puerto). F2-10 las
  moverá a SQLite gestionada por la pipe.
- El token de los argumentos no se registra nunca (S-05).

## Instalación como servicio de Windows (F3, requiere admin — NO automatizada aún)

```powershell
sc.exe create OrvitaPrint binPath= "C:\Program Files\OrvitaPrint\Orvita.Print.Service.exe" start= auto DisplayName= "Orvita Print Agent"
sc.exe description OrvitaPrint "Agente de impresión térmica de Orvita Print (WooCommerce)"
sc.exe failure OrvitaPrint reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc.exe start OrvitaPrint
```

El MSI (WiX) de F3-01 hará esto y los permisos de `ProgramData`; hasta entonces, estos
comandos manuales sirven para probar en sesión 0.

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
