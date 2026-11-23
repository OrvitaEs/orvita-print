using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// Incidente 2026-11-23 (defecto B): la tabla <c>wp_orp_destinations</c> del servidor quedaba
/// vacía sin rastro. El contrato Doc/04 §5.3 fija el cuerpo del heartbeat (snake_case, salidas
/// con <c>role/label/printer_name/copies/enabled</c>) y el plugin PHP
/// (<c>DestinationRepository::upsert_many</c>) DESCARTA EN SILENCIO toda salida sin
/// <c>printer_name</c> — y una salida TCP 9100 configurada solo con <c>--address</c> no tiene.
/// Estos tests fijan el shape EXACTO que el PHP espera (la forma literal de Doc/04 §5.3) y
/// garantizan que ninguna salida del almacén sale sin <c>printer_name</c>.
/// </summary>
public sealed class HeartbeatContractShapeTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"orp-shape-{Guid.NewGuid():N}.db");

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
    public async Task El_cuerpo_del_heartbeat_cumple_la_forma_literal_de_Doc04_53()
    {
        string? body = null;
        var handler = new FakeHttpHandler(request =>
        {
            if (request.Content is not null)
            {
                body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }

            return OutboxClaimAndReleaseTests.Response(
                200,
                """{ "ok": true, "server_time": "2026-11-23T22:34:18+00:00", "pending_count": 0, "config_version": 37 }""");
        });

        using var outbox = new OutboxRepository(_databasePath);
        var provider = new StaticDestinationProvider(
            // Salida pdf (la del incidente): impresora «Microsoft Print to PDF», sin label.
            new DestinationOptions
            {
                Role = "default",
                PrinterName = "Microsoft Print to PDF",
                Copies = 1,
                Enabled = true,
                Kind = PrintTransportKind.Pdf,
                Address = @"C:\temp\prints",
            },
            // Salida TCP 9100 solo con dirección: SIN impresora (el caso que el PHP descartaba).
            new DestinationOptions
            {
                Role = "cocina",
                Copies = 1,
                Enabled = true,
                Kind = PrintTransportKind.Tcp9100,
                Address = "192.168.1.50:9100",
            });

        var sender = new HeartbeatSender(
            () => TransportWith(handler),
            outbox,
            provider,
            new RemoteConfigState(),
            NullLogger.Instance);

        await sender.SendOnceAsync(CancellationToken.None);

        Assert.NotNull(body);
        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;

        // Claves del cuerpo exactamente como Doc/04 §5.3 (snake_case; NUNCA camelCase).
        Assert.True(root.TryGetProperty("agent_version", out _));
        Assert.True(root.TryGetProperty("pending_local", out _));
        Assert.True(root.TryGetProperty("destinations", out _));
        Assert.False(root.TryGetProperty("agentVersion", out _));
        Assert.False(root.TryGetProperty("pendingLocal", out _));

        var destinations = root.GetProperty("destinations");
        Assert.Equal(2, destinations.GetArrayLength());

        var pdf = destinations[0];
        Assert.Equal("default", pdf.GetProperty("role").GetString());
        Assert.Equal("Microsoft Print to PDF", pdf.GetProperty("printer_name").GetString());
        Assert.Equal(1, pdf.GetProperty("copies").GetInt32());
        Assert.True(pdf.GetProperty("enabled").GetBoolean());
        Assert.False(pdf.TryGetProperty("printerName", out _)); // nunca camelCase

        // La salida TCP 9100 sin impresora NO se descarta: reporta la dirección (host:puerto).
        var tcp = destinations[1];
        Assert.Equal("cocina", tcp.GetProperty("role").GetString());
        var tcpPrinter = tcp.GetProperty("printer_name").GetString();
        Assert.False(string.IsNullOrWhiteSpace(tcpPrinter));
        Assert.Equal("192.168.1.50:9100", tcpPrinter);
        Assert.True(tcp.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Una_salida_sin_impresora_ni_direccion_reporta_su_rol_y_no_se_descarta()
    {
        string? body = null;
        var handler = new FakeHttpHandler(request =>
        {
            if (request.Content is not null)
            {
                body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }

            return OutboxClaimAndReleaseTests.Response(
                200,
                """{ "ok": true, "pending_count": 0, "config_version": 1 }""");
        });

        using var outbox = new OutboxRepository(_databasePath);
        var provider = new StaticDestinationProvider(new DestinationOptions { Role = "mostrador" });
        var sender = new HeartbeatSender(
            () => TransportWith(handler),
            outbox,
            provider,
            new RemoteConfigState(),
            NullLogger.Instance);

        await sender.SendOnceAsync(CancellationToken.None);

        using var json = JsonDocument.Parse(body!);
        var destination = json.RootElement.GetProperty("destinations")[0];
        Assert.Equal("mostrador", destination.GetProperty("role").GetString());
        // printer_name NUNCA vacío ni ausente: el PHP lo exige para el upsert (Doc/04 §5.3).
        Assert.Equal("mostrador", destination.GetProperty("printer_name").GetString());
    }

    [Fact]
    public void El_mapeo_de_pair_usa_el_mismo_mapeo_que_el_heartbeat()
    {
        // /pair (Doc/04 §5.2) y /heartbeat comparten mapping: mismas garantías de shape.
        var info = DestinationWire.From(new DestinationOptions
        {
            Role = "cliente",
            Copies = 2,
            Enabled = false,
            Kind = PrintTransportKind.Tcp9100,
            Address = "10.0.0.9",
        });

        Assert.Equal("cliente", info.Role);
        Assert.Equal("10.0.0.9", info.PrinterName);
        Assert.Equal(2, info.Copies);
        Assert.False(info.Enabled);
    }
}
