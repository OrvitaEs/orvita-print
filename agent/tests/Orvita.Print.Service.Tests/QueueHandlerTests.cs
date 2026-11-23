using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Printing.Windows;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// Op «queue.list» y «queue.item» (GUI-02, Doc/08 §3): lectura pura del outbox local para la
/// GUI de bandeja — job_id, order_id, role, copias, estado, duplicate_suspected, timestamps y
/// último error, SIN document_json (el detalle solo viaja en queue.item, como texto legible).
/// </summary>
public sealed class QueueHandlerTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"orp-queue-{Guid.NewGuid():N}.db");
    private readonly LocalConfigStore _store;
    private readonly OutboxRepository _outbox;
    private readonly ManagementRequestHandler _handler;

    public QueueHandlerTests()
    {
        _store = new LocalConfigStore(_databasePath);
        _outbox = new OutboxRepository(_databasePath);
        var connection = new ServerConnection(new AgentOptions(), NullLogger<ServerConnection>.Instance);
        var printTransportFactory = new FakePrintTransportFactory(new FakePrintTransport());
        _handler = new ManagementRequestHandler(
            connection,
            _store,
            _outbox,
            new PairingService(
                connection,
                _store,
                new DpapiSecretProtector(),
                new PairingWaiter(),
                new AgentRuntimeStatus(),
                new StoreDestinationProvider(_store),
                NullLogger<PairingService>.Instance),
            new PollerController(
                () => connection.Transport!,
                new JobProcessor(
                    () => connection.Transport!,
                    _outbox,
                    new StoreDestinationProvider(_store),
                    printTransportFactory,
                    rendererOptions: () => new EscPosRendererOptions(),
                    pngDecoder: new FakePngDecoder(),
                    options: new JobProcessorOptions(),
                    logger: NullLogger<JobProcessor>.Instance),
                intervalSeconds: () => 10,
                logger: NullLogger<PollerController>.Instance),
            new TestPrintService(
                new StoreDestinationProvider(_store),
                printTransportFactory,
                new RemoteConfigState(),
                NullLogger.Instance),
            new FakePrinterEnumerator("EPSON TM-T20III"),
            new RemoteConfigState(),
            new AgentRuntimeStatus(),
            new AgentOptions { DataDirectory = Path.GetTempPath() },
            NullLogger<ManagementRequestHandler>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        _outbox.Dispose();
        File.Delete(_databasePath);
    }

    private ManagementRequest Request(string op, object? args) => new()
    {
        Id = 42,
        Op = op,
        Args = args is null ? null : JsonSerializer.SerializeToElement(args, ManagementProtocol.Options),
    };

    private static string DocumentWithOrder(long orderId) => JsonSerializer.Serialize(
        new Document
        {
            Version = 1,
            Meta = new DocumentMeta { OrderId = orderId, JobId = 9000, Copies = 1, PrinterRole = "default" },
            Blocks = [new TextBlock { Value = "Pizza Margarita · jamón, piña, ñ, €, áéíóú" }],
        },
        OrpJson.Default);

    // --------------------------------------------------------------------------- queue.list

    [Fact]
    public async Task Queue_list_en_cola_vacia_devuelve_lista_vacia_y_total_cero()
    {
        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueList, null), CancellationToken.None);

        Assert.True(response.Ok);
        var data = response.Data!.Value;
        Assert.Equal(0, data.GetProperty("total_jobs").GetInt32());
        Assert.Equal(0, data.GetProperty("jobs").GetArrayLength());
        Assert.Equal(50, data.GetProperty("limit").GetInt32()); // límite por defecto (Doc/08 §3)
    }

    [Fact]
    public async Task Queue_list_devuelve_los_campos_del_trabajo_sin_documento()
    {
        _outbox.Enqueue(1844, "default", 2, DocumentWithOrder(778899));
        _outbox.TryClaim(1844);
        _outbox.MarkDone(1844);

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueList, null), CancellationToken.None);

        Assert.True(response.Ok);
        var job = response.Data!.Value.GetProperty("jobs").EnumerateArray().Single();
        Assert.Equal(1844, job.GetProperty("job_id").GetInt64());
        Assert.Equal(778899, job.GetProperty("order_id").GetInt64());
        Assert.Equal("default", job.GetProperty("printer_role").GetString());
        Assert.Equal(2, job.GetProperty("copies").GetInt32());
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.False(job.GetProperty("duplicate_suspected").GetBoolean());
        Assert.Equal(1, job.GetProperty("attempts").GetInt32());
        Assert.False(job.TryGetProperty("error_message", out _)); // WhenWritingNull: sin error no viaja
        Assert.True(job.TryGetProperty("created_at", out _));
        Assert.True(job.TryGetProperty("updated_at", out _));
        Assert.True(job.TryGetProperty("printed_at", out _));

        // SIN document_json por la pipe (Doc/08 §3): el documento solo viaja en queue.item.
        var wire = JsonSerializer.Serialize(response, ManagementProtocol.Options);
        Assert.DoesNotContain("document_json", wire);
        Assert.DoesNotContain("Margarita", wire);
    }

    [Fact]
    public async Task Queue_list_incluye_estado_error_y_ultimo_error()
    {
        _outbox.Enqueue(1850, "cocina", 1, DocumentWithOrder(555));
        _outbox.TryClaim(1850);
        _outbox.MarkError(1850, "orp_print_failed", "La impresora «EPSON» no responde.");

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueList, null), CancellationToken.None);

        var job = response.Data!.Value.GetProperty("jobs").EnumerateArray().Single();
        Assert.Equal("error", job.GetProperty("state").GetString());
        Assert.Equal("orp_print_failed", job.GetProperty("error_code").GetString());
        Assert.Contains("no responde", job.GetProperty("error_message").GetString());
    }

    [Fact]
    public async Task Queue_list_aplica_el_limite_y_reporta_el_total()
    {
        for (var i = 1; i <= 7; i++)
        {
            _outbox.Enqueue(i, "default", 1, DocumentWithOrder(1000 + i));
        }

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueList, new { limit = 3 }), CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal(3, response.Data!.Value.GetProperty("jobs").GetArrayLength());
        Assert.Equal(7, response.Data!.Value.GetProperty("total_jobs").GetInt32());

        var limitado = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueList, new { limit = 999 }), CancellationToken.None);
        Assert.Equal(200, limitado.Data!.Value.GetProperty("limit").GetInt32()); // tope 200
        Assert.Equal(7, limitado.Data!.Value.GetProperty("jobs").GetArrayLength());
    }

    [Fact]
    public async Task Queue_list_devuelve_marca_duplicate_suspected()
    {
        _outbox.Enqueue(1900, "default", 1, DocumentWithOrder(1));
        _outbox.TryClaim(1900); // queda en printing…
        _outbox.RecoverInterrupted(); // …y la recuperación de arranque lo marca y reencola

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueList, null), CancellationToken.None);

        var job = response.Data!.Value.GetProperty("jobs").EnumerateArray().Single();
        Assert.True(job.GetProperty("duplicate_suspected").GetBoolean());
        Assert.Equal("received", job.GetProperty("state").GetString());
    }

    // --------------------------------------------------------------------------- queue.item

    [Fact]
    public async Task Queue_item_devuelve_el_documento_en_texto_legible()
    {
        _outbox.Enqueue(1844, "default", 1, DocumentWithOrder(778899));

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueItem, new { job_id = 1844 }), CancellationToken.None);

        Assert.True(response.Ok);
        var data = response.Data!.Value;
        Assert.True(data.GetProperty("document_parse_ok").GetBoolean());
        var text = data.GetProperty("document_text").GetString()!;
        // La cadena de compilación (regla del proyecto): acentos, ñ y € completos en la vista.
        Assert.Contains("Pizza Margarita · jamón, piña, ñ, €, áéíóú", text);
        Assert.Equal(1844, data.GetProperty("job").GetProperty("job_id").GetInt64());
        Assert.Equal(778899, data.GetProperty("job").GetProperty("order_id").GetInt64());
    }

    [Fact]
    public async Task Queue_item_con_documento_invalido_no_rompe_y_lo_dice()
    {
        _outbox.Enqueue(1851, "default", 1, "{no-es-json");

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueItem, new { job_id = 1851 }), CancellationToken.None);

        Assert.True(response.Ok);
        Assert.False(response.Data!.Value.GetProperty("document_parse_ok").GetBoolean());
        Assert.Equal("(sin documento)", response.Data!.Value.GetProperty("document_text").GetString());
    }

    [Fact]
    public async Task Queue_item_sin_job_id_o_inexistente_da_error_claro()
    {
        var sinId = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueItem, null), CancellationToken.None);
        Assert.False(sinId.Ok);
        Assert.Equal("invalid_argument", sinId.Code);

        var inexistente = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueItem, new { job_id = 99999 }), CancellationToken.None);
        Assert.False(inexistente.Ok);
        Assert.Equal("job_not_found", inexistente.Code);
    }

    [Fact]
    public async Task Queue_list_e_item_son_lecturas_puras_el_estado_no_cambia()
    {
        _outbox.Enqueue(1860, "default", 1, DocumentWithOrder(42));
        _outbox.TryClaim(1860);

        await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueList, null), CancellationToken.None);
        await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueItem, new { job_id = 1860 }), CancellationToken.None);
        await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueList, new { limit = 10 }), CancellationToken.None);

        var job = _outbox.Get(1860)!;
        Assert.Equal(OutboxJobState.Printing, job.State); // ni state ni attempts tocan
        Assert.Equal(1, job.Attempts);
    }

    /// <summary>Enumerador falso con impresoras fijas (sin depender del equipo real).</summary>
    private sealed class FakePrinterEnumerator(params string[] names) : IPrinterEnumerator
    {
        private List<PrinterInfo> _printers = [.. names.Select(n => new PrinterInfo { Name = n })];

        public IReadOnlyList<PrinterInfo> List() => _printers;

        public string? DefaultPrinterName() => _printers.FirstOrDefault()?.Name;

        public PrinterInfo? Find(string name) => _printers.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.Ordinal));
    }
}
