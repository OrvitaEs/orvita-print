# Orvita Print — Agente (.NET 10)

Solución del agente (Doc/03 §6, Doc/07 §2.2). Estado actual: **F2-01 y F2-02**.

## Proyectos

| Proyecto | Tipo | Contenido |
|---|---|---|
| `src/Orvita.Print.Contracts` | lib | Modelos del contrato congelado (Doc/04): documento neutro v1 (§7), `Job`/`JobBatch`/`JobResult`, códigos de error (§6) con sus dos formas de cuerpo, y modelos REST (health, heartbeat, config, pair). |
| `src/Orvita.Print.Core` | lib portable | `ITransport` (Doc/03 §7), `DirectTransport` (HttpClient, Bearer, HTTPS obligatorio — S-03), `Poller` (10 s ± 20 % jitter, backoff 10→20→40→60 s, parada en 401). **Sin nada de Windows** (ADR-12). |
| `tests/Orvita.Print.Core.Tests` | xUnit | 63 pruebas sin servidor real: roundtrip del documento (§7.3 incluido), mapeo de errores, `DirectTransport` con handler falso y poller con jitter/backoff determinables. |

`tools/Smoke/`: consola de humo contra el wp-env real (no está en la solución).

## Compilar y probar

```powershell
cd agent
dotnet build
dotnet test
```

Objetivo `net10.0`, `x64` documentado en `Directory.Build.props` para publicación
self-contained posterior (ADR-10).

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
