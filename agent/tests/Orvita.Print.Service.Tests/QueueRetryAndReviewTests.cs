using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Printing.Windows;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// Reimprimir y marcar como revisada desde la GUI (GUI-15): op pipe queue.retry (reimpresión
/// LOCAL con ack de cortesía, decisiones A y B), op queue.review (marca local, decisión D) y
/// la columna reviewed_at del outbox con su migración 1→2.
/// </summary>
public sealed class QueueRetryAndReviewTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"orp-retry-{Guid.NewGuid():N}.db");
    private readonly LocalConfigStore _store;
    private readonly OutboxRepository _outbox;
    private readonly FakeServerTransport _server = new();
    private readonly FakePrintTransport _printer = new();
    private readonly JobProcessor _processor;
    private readonly ManagementRequestHandler _handler;

    public QueueRetryAndReviewTests()
    {
        _store = new LocalConfigStore(_databasePath);
        _outbox = new OutboxRepository(_databasePath);
        var connection = new ServerConnection(new AgentOptions(), NullLogger<ServerConnection>.Instance);
        var printTransportFactory = new FakePrintTransportFactory(_printer);
        var destinations = new StaticDestinationProvider(new DestinationOptions
        {
            Role = "default",
            PrinterName = "EPSON TM-T20III",
            Copies = 1,
            Enabled = true,
        });
        _processor = new JobProcessor(
            () => _server,
            _outbox,
            destinations,
            printTransportFactory,
            rendererOptions: () => new EscPosRendererOptions(),
            pngDecoder: new FakePngDecoder(),
            options: new JobProcessorOptions { TransientRetryDelay = TimeSpan.Zero },
            logger: NullLogger<JobProcessor>.Instance);
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
                _processor,
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
            NullLogger<ManagementRequestHandler>.Instance,
            processor: _processor);
        connection.SetTransport(new Uri("https://tienda.test/wp-json/orp-print/v1"), "token", allowInsecureHttp: false);
    }

    public void Dispose()
    {
        _store.Dispose();
        _outbox.Dispose();
        File.Delete(_databasePath);
        if (File.Exists(_databasePath + "-wal"))
        {
            File.Delete(_databasePath + "-wal");
        }

        if (File.Exists(_databasePath + "-shm"))
        {
            File.Delete(_databasePath + "-shm");
        }
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

    private void EnqueueErrorJob(long jobId)
    {
        _outbox.Enqueue(jobId, "default", 1, DocumentWithOrder(1000 + jobId));
        _outbox.TryClaim(jobId);
        _outbox.MarkError(jobId, "orp_printer_not_found", "La impresora no existe.");
    }

    private void EnqueueDoneJob(long jobId)
    {
        _outbox.Enqueue(jobId, "default", 1, DocumentWithOrder(1000 + jobId));
        _outbox.TryClaim(jobId);
        _outbox.MarkDone(jobId);
    }

    // ------------------------------------------------------------------ queue.retry (A)

    [Fact]
    public async Task Retry_de_un_error_lo_imprime_y_queda_done()
    {
        EnqueueErrorJob(225);
        _server.ClaimResponses = new Dictionary<long, Job> { [225] = null! }; // claim 409 (terminal): no toca
        _printer.MarkCalls();

        var result = await _processor.RetryAsync(225, CancellationToken.None);

        Assert.Equal(JobProcessor.LocalRetryStatus.Completed, result.Status);
        Assert.Equal(OutboxJobState.Done, result.Job!.State);
        Assert.Equal(1, _printer.CallsSinceMark);
        Assert.Null(result.Job.ErrorCode);
        Assert.Equal(2, result.Job.Attempts); // intento original + la reimpresión
        // Ack de cortesía entregado (decisión B): el trabajo del servidor estaba terminal.
        var (_, ack) = Assert.Single(_server.DetailedAcks);
        Assert.Equal(JobResultStatus.Delivered, ack.Status);
        Assert.DoesNotContain(_server.Acks, a => a.Status == JobResultStatus.Delivered); // AckAsync normal no se usa aquí
    }

    [Fact]
    public async Task Retry_de_un_done_reimprime_otra_copia()
    {
        EnqueueDoneJob(228);
        _printer.MarkCalls();

        var result = await _processor.RetryAsync(228, CancellationToken.None);

        Assert.Equal(OutboxJobState.Done, result.Job!.State);
        Assert.Equal(1, _printer.CallsSinceMark);
        Assert.Equal(2, result.Job.Attempts);
    }

    [Fact]
    public async Task Retry_que_vuelve_a_fallar_queda_en_error_con_el_nuevo_codigo()
    {
        EnqueueErrorJob(230);
        var transport = new FakePrintTransport(new Queue<PrintFailureKind?>(
            [PrintFailureKind.PrinterNotFound]));
        var processor = new JobProcessor(
            () => _server,
            _outbox,
            new StaticDestinationProvider(new DestinationOptions { Role = "default", PrinterName = "EPSON", Enabled = true }),
            new FakePrintTransportFactory(transport),
            pngDecoder: new FakePngDecoder(),
            options: new JobProcessorOptions { TransientRetryDelay = TimeSpan.Zero },
            logger: NullLogger<JobProcessor>.Instance);

        var result = await processor.RetryAsync(230, CancellationToken.None);

        Assert.Equal(OutboxJobState.Error, result.Job!.State);
        Assert.Equal("orp_printer_not_found", result.Job.ErrorCode);
        // El ack failed de cortesía también intentó ir al servidor (best-effort).
        var (_, ack) = Assert.Single(_server.DetailedAcks);
        Assert.Equal(JobResultStatus.Failed, ack.Status);
        Assert.Equal("orp_printer_not_found", ack.ErrorCode);
    }

    [Fact]
    public async Task El_ack_de_cortesia_no_propaga_ningun_fallo_al_llamador()
    {
        EnqueueErrorJob(231);
        _server.ThrowOnNextAck = new TransportNetworkException("red caída");

        var result = await _processor.RetryAsync(231, CancellationToken.None);

        Assert.Equal(OutboxJobState.Done, result.Job!.State); // el papel salió aunque el ack no llegó
    }

    [Fact]
    public async Task Si_el_servidor_mantiene_failed_lo_registra_y_no_falla()
    {
        EnqueueErrorJob(232);
        _server.NextAckResponse = new DocumentAck { Ok = true, JobStatus = "failed" };
        var logs = new CapturingLogger();

        var processor = new JobProcessor(
            () => _server,
            _outbox,
            new StaticDestinationProvider(new DestinationOptions { Role = "default", PrinterName = "EPSON", Enabled = true }),
            new FakePrintTransportFactory(new FakePrintTransport()),
            pngDecoder: new FakePngDecoder(),
            logger: logs);
        await processor.RetryAsync(232, CancellationToken.None);

        Assert.Contains(logs.Warnings, w => w.Contains("el servidor mantiene failed"));
    }

    [Fact]
    public async Task Si_el_trabajo_sigue_pending_en_el_servidor_se_reclama_y_no_hay_duplicados()
    {
        // Decisión A (claim primero): el job sigue pending en el servidor (release anterior).
        // El claim lo recupera con documento y el ack delivered lo cierra — la ronda del
        // poller NO lo reimprimirá después.
        EnqueueErrorJob(233);
        _server.ClaimResponses = new Dictionary<long, Job>
        {
            [233] = new()
            {
                Id = 233,
                Attempt = 4,
                Document = JsonSerializer.Deserialize<Document>(DocumentWithOrder(1233), OrpJson.Default),
            },
        };

        var result = await _processor.RetryAsync(233, CancellationToken.None);

        Assert.Equal(OutboxJobState.Done, result.Job!.State);
        var (_, ack) = Assert.Single(_server.DetailedAcks);
        Assert.Equal(4, ack.Attempt); // usa el intento del claim del servidor
    }

    [Fact]
    public async Task Retry_de_un_trabajo_pendiente_o_inexistente_no_toca_nada()
    {
        _outbox.Enqueue(240, "default", 1, DocumentWithOrder(1240)); // received
        var pendiente = await _processor.RetryAsync(240, CancellationToken.None);
        Assert.Equal(JobProcessor.LocalRetryStatus.NotRetryable, pendiente.Status);
        Assert.Equal(OutboxJobState.Received, pendiente.Job!.State);

        var inexistente = await _processor.RetryAsync(99999, CancellationToken.None);
        Assert.Equal(JobProcessor.LocalRetryStatus.NotFound, inexistente.Status);

        // Sin salida configurada para el rol: tampoco cambia el estado.
        EnqueueErrorJob(241);
        var processor = new JobProcessor(
            () => _server,
            _outbox,
            new StaticDestinationProvider(), // sin salidas
            new FakePrintTransportFactory(new FakePrintTransport()),
            pngDecoder: new FakePngDecoder(),
            logger: NullLogger<JobProcessor>.Instance);
        var sinSalida = await processor.RetryAsync(241, CancellationToken.None);
        Assert.Equal(JobProcessor.LocalRetryStatus.NotRetryable, sinSalida.Status);
        Assert.Equal(OutboxJobState.Error, sinSalida.Job!.State);
    }

    // ------------------------------------------------------------------ op pipe queue.retry

    [Fact]
    public async Task La_op_queue_retry_devuelve_el_estado_final_para_pintar_la_fila()
    {
        EnqueueErrorJob(250);

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueRetry, new { job_id = 250 }), CancellationToken.None);

        Assert.True(response.Ok, $"ok={response.Ok} error={response.Error} code={response.Code}");
        Assert.Equal("done", response.Data!.Value.GetProperty("state").GetString());
        // El retry limpió la marca de revisión (si vuelve a fallar será una incidencia nueva):
        // sin reviewed_at en el cable (WhenWritingNull).
        Assert.False(response.Data!.Value.TryGetProperty("reviewed_at", out _));
    }

    [Fact]
    public async Task La_op_queue_retry_con_job_inexistente_o_invalido_da_error_claro()
    {
        var sinId = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueRetry, null), CancellationToken.None);
        Assert.False(sinId.Ok);
        Assert.Equal("invalid_argument", sinId.Code);

        var inexistente = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueRetry, new { job_id = 99999 }), CancellationToken.None);
        Assert.False(inexistente.Ok);
        Assert.Equal("job_not_found", inexistente.Code);
    }

    // ------------------------------------------------------------------ queue.review (D)

    [Fact]
    public async Task La_op_queue_review_marca_y_desmarca_sin_tocar_el_estado()
    {
        EnqueueErrorJob(260);
        _outbox.SetReviewed(260, true);

        var marcado = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueReview, new { job_id = 260 }), CancellationToken.None);
        Assert.True(marcado.Ok, marcado.Error ?? "(sin error)");
        Assert.Equal("error", marcado.Data!.Value.GetProperty("state").GetString());
        Assert.True(marcado.Data!.Value.GetProperty("reviewed_at").ValueKind is JsonValueKind.String);

        var desmarcado = await _handler.HandleAsync(
            Request(ManagementProtocol.Operations.QueueReview, new { job_id = 260, reviewed = false }), CancellationToken.None);
        Assert.True(desmarcado.Ok, desmarcado.Error ?? "(sin error)");
        Assert.False(desmarcado.Data!.Value.TryGetProperty("reviewed_at", out _)); // WhenWritingNull: sin marca no viaja
    }

    [Fact]
    public async Task La_op_queue_review_de_un_trabajo_inexistente_da_error()
    {
        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueReview, new { job_id = 424242 }), CancellationToken.None);
        Assert.False(response.Ok);
        Assert.Equal("job_not_found", response.Code);
    }

    [Fact]
    public async Task Queue_list_incluye_reviewed_at_en_el_cable()
    {
        EnqueueErrorJob(270);
        _outbox.SetReviewed(270, true);

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.QueueList, null), CancellationToken.None);

        var job = response.Data!.Value.GetProperty("jobs").EnumerateArray().Single();
        Assert.True(job.TryGetProperty("reviewed_at", out _));
        Assert.Equal(1, _outbox.CountByState(OutboxJobState.Error)); // el estado local no cambia: la marca es local
    }

    // ------------------------------------------------------------------ migración 1 → 2

    [Fact]
    public void La_migracion_1_a_2_se_aplica_sin_perder_datos()
    {
        // BD «antigua»: esquema v1 (sin reviewed_at) con datos dentro.
        var oldPath = Path.Combine(Path.GetTempPath(), $"orp-mig-{Guid.NewGuid():N}.db");
        using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={oldPath}"))
        {
            old.Open();
            using var command = old.CreateCommand();
            command.CommandText = """
                CREATE TABLE outbox_jobs (
                    job_id INTEGER PRIMARY KEY, printer_role TEXT, copies INTEGER NOT NULL DEFAULT 1,
                    document_json TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'received', attempts INTEGER NOT NULL DEFAULT 0,
                    duplicate_suspected INTEGER NOT NULL DEFAULT 0, error_code TEXT, error_message TEXT,
                    created_at TEXT NOT NULL, updated_at TEXT NOT NULL, printed_at TEXT);
                CREATE TABLE outbox_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO outbox_meta VALUES ('schema_version', '1');
                INSERT INTO outbox_jobs VALUES (1, 'default', 2, '{}', 'error', 3, 0, 'orp_print_failed', 'sin papel',
                    '2026-11-24T10:00:00.0000000+00:00', '2026-11-24T10:00:05.0000000+00:00', NULL);
                """;
            _ = command.ExecuteNonQuery();
        }

        using (var outbox = new OutboxRepository(oldPath)) // abrir = migrar
        {
            var row = outbox.Get(1)!;
            Assert.Equal(OutboxJobState.Error, row.State);
            Assert.Equal("sin papel", row.ErrorMessage);
            Assert.Equal(3, row.Attempts);
            Assert.Null(row.ReviewedAt); // la columna nueva nace vacía, sin reventar lo viejo

            Assert.True(outbox.SetReviewed(1, true));
            Assert.NotNull(outbox.Get(1)!.ReviewedAt);
            Assert.True(outbox.SetReviewed(1, false));
            Assert.Null(outbox.Get(1)!.ReviewedAt);

            // Reabrir es idempotente: la versión ya es 2 y no re-altera nada.
            using var reopened = new OutboxRepository(oldPath);
            Assert.True(reopened.RetryRequeue(1));
            Assert.Equal(OutboxJobState.Received, reopened.Get(1)!.State);
        }

        // Limpiar todos los pools de SQLite antes de borrar (el pool del primer open vive aparte).
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(oldPath);
        if (File.Exists(oldPath + "-wal"))
        {
            File.Delete(oldPath + "-wal");
        }

        if (File.Exists(oldPath + "-shm"))
        {
            File.Delete(oldPath + "-shm");
        }
    }

    [Fact]
    public void RetryRequeue_solo_error_o_done_y_limpia_error_y_revision()
    {
        using var outbox = new OutboxRepository(_databasePath);
        EnqueueErrorJob(280);
        outbox.SetReviewed(280, true);

        Assert.True(outbox.RetryRequeue(280));
        var row = outbox.Get(280)!;
        Assert.Equal(OutboxJobState.Received, row.State);
        Assert.Null(row.ErrorCode);
        Assert.Null(row.ErrorMessage);
        Assert.Null(row.ReviewedAt); // un re-fallo es una incidencia nueva: vuelve al banner
        Assert.Equal(1, row.Attempts); // los intentos ya contados no se borran

        outbox.TryClaim(280);
        outbox.MarkDone(280);
        Assert.True(outbox.RetryRequeue(280)); // un done también se reimprime (GUI-15)

        outbox.TryClaim(280);
        Assert.False(outbox.RetryRequeue(280)); // printing no es re-imprimible
    }

    /// <summary>Logger mínimo que guarda los Warning para asertar mensajes de cortesía.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>Enumerador falso con impresoras fijas (sin depender del equipo real).</summary>
    private sealed class FakePrinterEnumerator(params string[] names) : IPrinterEnumerator
    {
        private readonly List<PrinterInfo> _printers = [.. names.Select(n => new PrinterInfo { Name = n })];

        public IReadOnlyList<PrinterInfo> List() => _printers;

        public string? DefaultPrinterName() => _printers.FirstOrDefault()?.Name;

        public PrinterInfo? Find(string name) => _printers.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.Ordinal));
    }
}
