using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orvita.Print.Core;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// Integración F2-08 + F2-10 sobre el host REAL (modo consola): host sin emparejar → pair por
/// la vía del servicio (la misma que invoca la operación «pair» de la pipe) → el worker
/// despierta, lanza el heartbeat con las salidas del almacén y el poller hace su primer fetch,
/// SIN reiniciar el proceso. Todo contra un handler HTTP falso (sin red ni WordPress).
/// </summary>
public sealed class PairWakeUpTests
{
    [Fact]
    public async Task Pair_despierta_al_worker_arranca_heartbeat_y_poller()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"orp-wake-{Guid.NewGuid():N}");
        try
        {
            var pairSeen = false;
            var heartbeatSeen = false;
            var jobsSeen = false;
            var heartbeatBody = "";
            var handler = new FakeHttpHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/pair", StringComparison.Ordinal))
                {
                    pairSeen = true;
                    return Json(200, """{ "token": "orp_token_wake_123", "location": { "id": 3, "code": "rio-rosso", "name": "Río Rosso" }, "poll_interval": 10 }""");
                }

                if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
                {
                    heartbeatSeen = true;
                    heartbeatBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    return Json(200, """{ "ok": true, "server_time": "2026-11-23T10:00:00+01:00", "pending_count": 0, "config_version": 1 }""");
                }

                if (path.EndsWith("/jobs", StringComparison.Ordinal))
                {
                    jobsSeen = true;
                    return Json(200, """{ "jobs": [], "cursor": 0 }""");
                }

                if (path.EndsWith("/config", StringComparison.Ordinal))
                {
                    return Json(200, """{ "config_version": 1, "paper_width_mm": 80, "encoding": "CP850", "poll_interval": 10 }""");
                }

                return Json(404, """{ "error": "no_route" }""");
            });

            using var host = AgentHostBuilder.BuildHost(
                ["--data-dir", dataDirectory, "--console"],
                services =>
                {
                    // PairingService CON el HttpClient falso: el DirectTransport que el pair crea
                    // comparte ese HttpClient (DirectTransport no lo dispone) → sin red real.
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

            await host.StartAsync();

            // Antes del pair: sin transporte, worker a la espera.
            var connection = host.Services.GetRequiredService<ServerConnection>();
            Assert.Null(connection.Transport);

            // pair por la vía del servicio (la operación «pair» de la pipe llama exactamente a esto).
            var pairing = host.Services.GetRequiredService<PairingService>();
            var outcome = await pairing.ApplyPairingAsync(
                "https://tienda.example/wp-json/orp-print/v1", "482913", "TPV Wake", allowInsecureHttp: false, CancellationToken.None);

            Assert.True(pairSeen, "el POST /pair debe haberse enviado");
            Assert.Equal("https://tienda.example/wp-json/orp-print/v1/", outcome.ServerUrl);
            Assert.NotNull(connection.Transport); // transporte aplicado en caliente

            // Almacén F2-10 real: emparejado y token cifrado (S-01).
            var store = host.Services.GetRequiredService<LocalConfigStore>();
            var stored = store.GetPairing();
            Assert.True(stored.IsPaired);
            Assert.NotEqual("orp_token_wake_123", stored.TokenProtected);

            // El worker despierta: heartbeat inicial + primer fetch del poller (sin reinicio).
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while ((!heartbeatSeen || !jobsSeen) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.True(heartbeatSeen, "el worker debería enviar el heartbeat inicial tras el pair");
            Assert.True(jobsSeen, "el poller debería hacer su primer fetch tras el pair");
            Assert.Contains("\"agent_version\":\"", heartbeatBody); // el heartbeat reporta versión y salidas (Doc/04 §5.3)

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
