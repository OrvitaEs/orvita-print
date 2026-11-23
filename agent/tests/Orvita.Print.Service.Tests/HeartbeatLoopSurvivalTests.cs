using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orvita.Print.Core;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// Incidente 2026-11-23 (defecto A): el bucle de latidos dejaba de latir sin rastro en el log
/// (ni INF ni WRN) tras 1–2 latidos; <c>status</c> seguía mostrando el poller «running» y la
/// última vez visto por el servidor quedaba congelada. Causas defendidas aquí:
/// 1) una respuesta 200 malformada mataba la tarea del poller para siempre (ver
///    <c>PollerTests</c>) y un reinicio del poller posterior podía arrastrarse al bucle;
/// 2) el cuerpo del tick (<c>heartbeat</c> + reinicio del poller por cambio de intervalo) no
///    estaba protegido: cualquier excepción inesperada mataba el latido en silencio.
/// Regresión: con reloj/HTTP falsos, el latido SOBREVIVE ≥ 5 intervalos incluyendo una
/// respuesta malformada del poller, un cambio de <c>config_version</c> y el reinicio del
/// poller que provoca (la interacción heartbeat → config → reinicio del poller).
/// </summary>
public sealed class HeartbeatLoopSurvivalTests
{
    [Fact]
    public async Task El_latido_sobrevive_cinco_intervalos_con_malformacion_cambio_de_config_y_reinicio_del_poller()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"orp-heartbeat-{Guid.NewGuid():N}");
        try
        {
            var heartbeatCount = 0;
            var configFetchCount = 0;
            var jobsFetchCount = 0;
            var lastHeartbeatBody = "";
            var configVersion = 1L;

            var handler = new FakeHttpHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/pair", StringComparison.Ordinal))
                {
                    return Json(200, """{ "token": "orp_token_survival", "location": { "id": 9, "code": "sur", "name": "Pizzería Sur" }, "poll_interval": 10 }""");
                }

                if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
                {
                    heartbeatCount++;
                    lastHeartbeatBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    // El latido 2 trae config_version 2 (como en el incidente: la respuesta del
                    // propio latido manda una config nueva que el agente debe aplicar).
                    if (heartbeatCount >= 2)
                    {
                        configVersion = 2;
                    }

                    return Json(200, $$"""{ "ok": true, "pending_count": 0, "config_version": {{configVersion}} }""");
                }

                if (path.EndsWith("/config", StringComparison.Ordinal))
                {
                    configFetchCount++;
                    // La config nueva cambia el intervalo de poll (10 → 2): el bucle del latido
                    // debe reiniciar el poller con el worker en marcha (interacción del incidente).
                    return Json(200, $$"""{ "config_version": {{configVersion}}, "paper_width_mm": 80, "encoding": "CP850", "poll_interval": {{(configVersion == 1 ? 10 : 2)}} }""");
                }

                if (path.EndsWith("/jobs", StringComparison.Ordinal))
                {
                    jobsFetchCount++;
                    if (jobsFetchCount == 1)
                    {
                        // 200 con cuerpo NO contractual (notice de PHP delante del JSON): antes
                        // mataba la tarea del poller para siempre.
                        return Json(200, "Notice: ob_end_flush(): failed to send buffer. {\"jobs\":");
                    }

                    return Json(200, """{ "jobs": [], "cursor": 0 }""");
                }

                return Json(404, """{ "error": "no_route" }""");
            });

            using var host = AgentHostBuilder.BuildHost(
                ["--data-dir", dataDirectory, "--console"],
                services =>
                {
                    // Latidos cada 1 s: cinco intervalos en ~6 s de prueba (el mínimo del
                    // PeriodicTimer del worker es 1 s).
                    services.AddSingleton(sp => new AgentOptions { HeartbeatIntervalSeconds = 1 });
                    services.AddSingleton(sp => new PairingService(
                        sp.GetRequiredService<ServerConnection>(),
                        sp.GetRequiredService<LocalConfigStore>(),
                        sp.GetRequiredService<ISecretProtector>(),
                        sp.GetRequiredService<PairingWaiter>(),
                        sp.GetRequiredService<AgentRuntimeStatus>(),
                        sp.GetRequiredService<IDestinationProvider>(),
                        sp.GetRequiredService<ILogger<PairingService>>(),
                        new HttpClient(handler)));
                });

            // La salida del incidente en el almacén ANTES del pair: pdf sin impresora
            // (solo dirección) — el shape que antes el servidor descartaba en silencio.
            var store = host.Services.GetRequiredService<LocalConfigStore>();
            store.UpsertDestination(new DestinationOptions
            {
                Role = "default",
                Kind = PrintTransportKind.Pdf,
                Address = Path.Combine(dataDirectory, "prints"),
            });

            await host.StartAsync();

            var pairing = host.Services.GetRequiredService<PairingService>();
            await pairing.ApplyPairingAsync(
                "https://tienda.example/wp-json/orp-print/v1", "482913", "TPV Survival",
                allowInsecureHttp: false, CancellationToken.None);

            // El latido debe SEGUIR latiendo ≥ 5 intervalos (1 s cada uno): ≥ 6 latidos en total
            // (el inicial + 5 ticks), A PESAR de la respuesta malformada del poller, el cambio
            // de config_version y el reinicio del poller en caliente.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
            while (heartbeatCount < 6 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.True(
                heartbeatCount >= 6,
                $"el latido debería sobrevivir ≥ 5 intervalos; solo llegaron {heartbeatCount}");

            // La re-sincronización de config ocurrió una vez (cuando cambió config_version).
            Assert.Equal(2, configFetchCount);

            // El poller se recuperó de la respuesta malformada y siguió haciendo fetch después
            // del reinicio: fetch 1 malformado → el poller viejo queda en backoff de 10 s; el
            // fetch 2 SOLO puede venir del poller NUEVO arrancado por el cambio de intervalo.
            Assert.True(jobsFetchCount >= 2, $"el poller debería seguir haciendo fetch tras el reinicio; hubo {jobsFetchCount}");

            // El shape del latido sigue siendo el contractual mientras todo esto ocurría:
            // la salida pdf sin impresora sale con su dirección (nunca descartada).
            Assert.Contains("\"role\":\"default\"", lastHeartbeatBody);
            Assert.Contains("\"printer_name\":\"", lastHeartbeatBody);
            Assert.DoesNotContain("printerName", lastHeartbeatBody);

            // El worker sigue vivo: el estado de runtime refleja un latido reciente y correcto.
            var runtime = host.Services.GetRequiredService<AgentRuntimeStatus>();
            Assert.NotNull(runtime.LastSeenByServerUtc);
            Assert.Null(runtime.LastHeartbeatError);

            await host.StopAsync();
        }
        finally
        {
            try
            {
                if (Directory.Exists(dataDirectory))
                {
                    Directory.Delete(dataDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Limpieza de buena fe en %TEMP%.
            }
        }
    }

    private static HttpResponseMessage Json(int status, string body) => new((System.Net.HttpStatusCode)status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };
}
