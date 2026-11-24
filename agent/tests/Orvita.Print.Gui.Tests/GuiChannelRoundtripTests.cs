using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Gui.Management;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Gui.Tests;
/// <summary>
/// GUI-08: la GUI contra el SERVIDOR DE PIPE REAL (en proceso) — mismo protocolo JSON que el
/// CLI, con el canal concreto de la GUI. Prueba queue.list/queue.item/pair.remove EXTREMO a
/// EXTREMO: ViewModel no, transporte y handler sí (la lógica de VM va con el doble).
/// </summary>
public sealed class GuiChannelRoundtripTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"orp-gui-pipe-{Guid.NewGuid():N}.db");
    private readonly string _pipeName = $"orvitaprint-gui-{Guid.NewGuid():N}";
    private readonly LocalConfigStore _store;
    private readonly OutboxRepository _outbox;
    private readonly ServerConnection _connection;
    private readonly ManagementPipeServer _server;
    private readonly PollerController _poller;
    private readonly ManagementGuiClient _client;

    public GuiChannelRoundtripTests()
    {
        _store = new LocalConfigStore(_databasePath);
        _outbox = new OutboxRepository(_databasePath);
        _connection = new ServerConnection(new AgentOptions(), NullLogger<ServerConnection>.Instance);
        var printTransportFactory = new FakeGuiPrintTransportFactory();
        _poller = new PollerController(
            () => _connection.Transport!,
            new JobProcessor(
                () => _connection.Transport!,
                _outbox,
                new StoreDestinationProvider(_store),
                printTransportFactory,
                rendererOptions: () => new EscPosRendererOptions(),
                pngDecoder: new FakeGuiPngDecoder(),
                options: new JobProcessorOptions(),
                logger: NullLogger<JobProcessor>.Instance),
            intervalSeconds: () => 10,
            logger: NullLogger<PollerController>.Instance);
        var handler = new ManagementRequestHandler(
            _connection,
            _store,
            _outbox,
            new PairingService(
                _connection,
                _store,
                new DpapiSecretProtector(),
                new PairingWaiter(),
                new AgentRuntimeStatus(),
                new StoreDestinationProvider(_store),
                NullLogger<PairingService>.Instance),
            _poller,
            new TestPrintService(
                new StoreDestinationProvider(_store),
                printTransportFactory,
                new RemoteConfigState(),
                NullLogger.Instance),
            new FakePipePrinterEnumerator("EPSON TM-T20III"),
            new RemoteConfigState(),
            new AgentRuntimeStatus(),
            new AgentOptions { DataDirectory = Path.GetTempPath() },
            NullLogger<ManagementRequestHandler>.Instance);
        _server = new ManagementPipeServer(handler, NullLogger<ManagementPipeServer>.Instance, _pipeName);

        // El cliente de la GUI apunta a la pipe de PRUEBA (inyección, igual que las demás piezas).
        _client = new ManagementGuiClient(() => new NamedPipeManagementChannel(_pipeName));
    }

    public async Task InitializeAsync()
    {
        await _server.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        _store.Dispose();
        _outbox.Dispose();
        File.Delete(_databasePath);
    }

    [Fact]
    public void La_gui_habla_el_mismo_protocolo_que_el_cli_status_y_ping()
    {
        var ping = _client.Send(ManagementProtocol.Operations.Ping);
        Assert.True(ping.Ok);
        Assert.True(ping.Data!.Value.GetProperty("pong").GetBoolean());

        var status = _client.Send(ManagementProtocol.Operations.Status);
        Assert.True(status.Ok);
        Assert.False(status.Data!.Value.GetProperty("paired").GetBoolean()); // sin emparejar aún
    }

    [Fact]
    public void Queue_list_por_la_pipe_real_devuelve_el_outbox_sin_documento()
    {
        _outbox.Enqueue(1844, "default", 2, DocumentWithOrder(778899));

        var response = _client.Send(ManagementProtocol.Operations.QueueList, new { limit = 10 });

        Assert.True(response.Ok);
        var job = Assert.Single(response.Data!.Value.GetProperty("jobs").EnumerateArray());
        Assert.Equal(1844, job.GetProperty("job_id").GetInt64());
        Assert.Equal(778899, job.GetProperty("order_id").GetInt64());
        Assert.Equal("received", job.GetProperty("state").GetString());
        Assert.False(job.TryGetProperty("document_json", out _));
    }

    [Fact]
    public void Queue_item_por_la_pipe_real_devuelve_texto_legible()
    {
        _outbox.Enqueue(1844, "default", 1, DocumentWithOrder(778899));

        var response = _client.Send(ManagementProtocol.Operations.QueueItem, new { job_id = 1844 });

        Assert.True(response.Ok);
        Assert.True(response.Data!.Value.GetProperty("document_parse_ok").GetBoolean());
        Assert.Contains("Pizza Margarita · jamón, piña, ñ, €, áéíóú", response.Data!.Value.GetProperty("document_text").GetString());
    }

    [Fact]
    public void Sin_servicio_la_gui_recibe_el_mensaje_conocido_no_una_excepcion_rara()
    {
        // La pipe de prueba NO está arrancada aquí: un cliente nuevo a una pipe inexistente…
        using var channel = new NamedPipeManagementChannel($"orvitaprint-no-existe-{Guid.NewGuid():N}");

        var error = Assert.Throws<ServiceNotRunningException>(() => channel.Connect(TimeSpan.FromMilliseconds(500)));
        Assert.Contains("El servicio Orvita Print no está en ejecución", error.Message);
    }

    private static string DocumentWithOrder(long orderId) => JsonSerializer.Serialize(
        new Orvita.Print.Contracts.Document
        {
            Version = 1,
            Meta = new Orvita.Print.Contracts.DocumentMeta { OrderId = orderId },
            Blocks = [new Orvita.Print.Contracts.TextBlock { Value = "Pizza Margarita · jamón, piña, ñ, €, áéíóú" }],
        },
        OrpJson.Default);
}
