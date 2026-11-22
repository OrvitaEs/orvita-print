using Microsoft.Extensions.Logging.Abstractions;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// Heartbeat (Doc/04 §5.3) con DirectTransport sobre handler HTTP falso: envía versión,
/// pendientes, último error y destinos; re-sincroniza la configuración SOLO cuando cambia
/// config_version (Doc/04 §5.8). Sin servidor ni impresora.
/// </summary>
public sealed class HeartbeatSenderTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"orp-outbox-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        File.Delete(_databasePath);
    }

    private static DirectTransport TransportWith(FakeHttpHandler handler) =>
        new(
            new Uri("https://tienda.test/wp-json/orp-print/v1"),
            "orp_token_prueba",
            new HttpClient(handler));

    [Fact]
    public async Task Heartbeat_reporta_version_pendientes_errores_y_destinos()
    {
        string? heartbeatBody = null;
        var handler = new FakeHttpHandler(request =>
        {
            if (request.Content is not null)
            {
                heartbeatBody = request.Content.ReadAsStringAsync().Result;
            }

            return OutboxClaimAndReleaseTests.Response(
                200,
                """{ "ok": true, "server_time": "2026-11-23T10:00:00+01:00", "pending_count": 1, "config_version": 14 }""");
        });

        using var outbox = new OutboxRepository(_databasePath);
        outbox.Enqueue(1, "default", 1, "{}");
        outbox.TryClaim(1); // printing: cuenta como pendiente local

        var provider = new StaticDestinationProvider(
            new DestinationOptions { Role = "default", Label = "Mostrador", PrinterName = "EPSON TM-T20III", Copies = 2, Enabled = true },
            new DestinationOptions { Role = "cocina", Label = "Cocina", PrinterName = "XPRINTER", Copies = 1, Enabled = false });
        var state = new RemoteConfigState();
        var sender = new HeartbeatSender(() => TransportWith(handler), outbox, provider, state, NullLogger.Instance);

        var version = await sender.SendOnceAsync(CancellationToken.None);

        Assert.Equal(14, version);
        // El cuerpo del heartbeat lleva las dos salidas (activa e inactiva) y los pendientes.
        Assert.Contains("\"agent_version\":", heartbeatBody);
        Assert.Contains("\"pending_local\":1", heartbeatBody);
        Assert.Contains("\"printer_name\":\"EPSON TM-T20III\"", heartbeatBody);
        Assert.Contains("\"role\":\"cocina\"", heartbeatBody);
        Assert.Contains("\"enabled\":false", heartbeatBody);
    }

    [Fact]
    public async Task Config_se_resincroniza_solo_cuando_cambia_config_version()
    {
        var configFetches = 0;
        var configVersion = 14L;
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/config", StringComparison.Ordinal))
            {
                configFetches++;
                return OutboxClaimAndReleaseTests.Response(200, $$"""{ "config_version": {{configVersion}}, "paper_width_mm": 80, "encoding": "CP858", "poll_interval": 15 }""");
            }

            return OutboxClaimAndReleaseTests.Response(
                200,
                $$"""{ "ok": true, "config_version": {{configVersion}}, "pending_count": 0 }""");
        });

        using var outbox = new OutboxRepository(_databasePath);
        var sender = new HeartbeatSender(
            () => TransportWith(handler),
            outbox,
            new StaticDestinationProvider(),
            state,
            NullLogger.Instance);

        await sender.SendOnceAsync(CancellationToken.None);
        Assert.Equal(1, configFetches);
        Assert.Equal(15, state.PollIntervalSeconds);

        // Mismo config_version: NO re-descarga.
        await sender.SendOnceAsync(CancellationToken.None);
        Assert.Equal(1, configFetches);

        // Cambia config_version: re-descarga y aplica.
        configVersion = 15;
        await sender.SendOnceAsync(CancellationToken.None);
        Assert.Equal(2, configFetches);
        Assert.Equal(15, state.ConfigVersion);
    }

    private readonly RemoteConfigState state = new();
}
