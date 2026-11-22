using System.Text;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// Pipeline claim → render → transporte → ack con transportes falsos (sin impresora ni
/// servidor), sobre una cola SQLite temporal. Cubre: happy path con copias, salida no
/// configurada (no reclama), claim perdido, documento no soportado, transitorios con
/// release sin consumir intento, fallo definitivo y drenaje de arranque.
/// </summary>
public sealed class JobProcessorTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"orp-outbox-{Guid.NewGuid():N}.db");

    private readonly FakeServerTransport _server = new();

    private readonly FakePrintTransport _print = new();

    private readonly FakePngDecoder _png = new();

    public void Dispose()
    {
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

    private JobProcessor CreateProcessor(
        DestinationOptions? destination = null,
        JobProcessorOptions? processorOptions = null,
        OutboxRepository? outbox = null,
        IPrintTransportFactory? factory = null,
        ITransport? serverOverride = null)
    {
        outbox ??= new OutboxRepository(_databasePath);
        return new JobProcessor(
            serverOverride is null ? () => _server : () => serverOverride,
            outbox,
            new StaticDestinationProvider(destination ?? new DestinationOptions
            {
                Role = "default",
                Label = "Mostrador",
                PrinterName = "EPSON TM-T20III",
                Copies = 2,
                Enabled = true,
            }),
            factory ?? new FakePrintTransportFactory(_print),
            rendererOptions: () => new EscPosRendererOptions(),
            pngDecoder: _png,
            options: processorOptions ?? new JobProcessorOptions { MaxTransientRetries = 3, TransientRetryDelay = TimeSpan.FromMilliseconds(1) });
    }

    private static Job FetchedJob(long id = 1844, string role = "default", Document? document = null) => new()
    {
        Id = id,
        OrderId = 5678,
        PrinterRole = role,
        Attempt = 0,
        Document = document,
    };

    private static Job ClaimedJob(long id = 1844, string role = "default", Document? document = null) => new()
    {
        Id = id,
        OrderId = 5678,
        PrinterRole = role,
        Attempt = 1,
        Document = document ?? TestDocuments.Ticket(id, role: role),
    };

    // ------------------------------------------------------------ happy path

    [Fact]
    public async Task Pipeline_completo_imprime_con_copias_y_ack_delivered()
    {
        var claimed = ClaimedJob(1844, document: TestDocuments.Ticket(1844, copies: 2));
        _server.ClaimResponses[1844] = claimed;
        using var outbox = new OutboxRepository(_databasePath);
        var processor = CreateProcessor(outbox: outbox);

        await processor.ProcessJobAsync(FetchedJob(1844), CancellationToken.None);

        // Impresión: una llamada con las copias del TRABAJO (frozen, Doc/04 §2.4) y bytes ESC/POS.
        Assert.Equal(1, _print.Calls);
        Assert.Equal(2, _print.LastRequest!.Copies);
        Assert.Equal(0x1B, _print.LastRequest.EscPos[0]); // empieza con ESC @
        Assert.Equal(0x40, _print.LastRequest.EscPos[1]);
        var cp850 = Encoding.GetEncoding(850);
        Assert.Contains(Encoding.ASCII.GetString(cp850.GetBytes("PIZZERIA RIO ROSSO")), Encoding.Latin1.GetString(_print.LastRequest.EscPos));

        // Cola local: done.
        var row = outbox.Get(1844);
        Assert.Equal(OutboxJobState.Done, row!.State);

        // Ack delivered con impresora y tiempos (Doc/04 §5.6).
        var ack = Assert.Single(_server.Acks);
        Assert.Equal(JobResultStatus.Delivered, ack.Status);
        Assert.Equal("EPSON TM-T20III", ack.Printer);
        Assert.Equal(1, ack.Attempt);
        Assert.True(ack.ElapsedMs >= 0);
        Assert.Null(ack.ErrorCode);
    }

    [Fact]
    public async Task Salida_no_configurada_no_reclama_ni_encola_nada()
    {
        using var outbox = new OutboxRepository(_databasePath);
        var processor = CreateProcessor(destination: null, outbox: outbox);

        await processor.ProcessJobAsync(FetchedJob(10, role: "cocina"), CancellationToken.None);

        Assert.Equal(0, _print.Calls);
        Assert.Empty(_server.Acks);
        Assert.Null(outbox.Get(10)); // el trabajo solo existe en el servidor (pending, Doc/04 §5.8)
    }

    [Fact]
    public async Task Salida_desactivada_no_reclama()
    {
        using var outbox = new OutboxRepository(_databasePath);
        var processor = CreateProcessor(
            destination: new DestinationOptions { Role = "default", PrinterName = "X", Enabled = false },
            outbox: outbox);

        await processor.ProcessJobAsync(FetchedJob(11), CancellationToken.None);

        Assert.Equal(0, _print.Calls);
        Assert.Empty(_server.Acks);
        Assert.Null(outbox.Get(11));
    }

    [Fact]
    public async Task Claim_perdido_409_410_404_descarta_local_sin_ack()
    {
        using var outbox = new OutboxRepository(_databasePath);
        // Una fila preexistente en received (de un ciclo anterior): el claim fallará.
        outbox.Enqueue(99, "default", 1, "{}");
        var processor = CreateProcessor(outbox: outbox, serverOverride: new ClaimFailingTransport());

        await processor.ProcessStartupQueueAsync(CancellationToken.None);

        Assert.Equal(0, _print.Calls);
        Assert.Empty(_server.Acks); // no es nuestro: sin ack
        var row = outbox.Get(99);
        Assert.Equal(OutboxJobState.Error, row!.State);
        Assert.Equal("orp_job_not_claimed", row.ErrorCode);
    }

    private sealed class ClaimFailingTransport : ITransport
    {
        public Task<JobBatch> FetchAsync(Cursor cursor, CancellationToken ct) =>
            Task.FromResult(new JobBatch { Jobs = [], Cursor = cursor.Value });

        public Task<Job?> ClaimAsync(long jobId, CancellationToken ct) => Task.FromResult<Job?>(null);

        public Task AckAsync(long jobId, JobResult result, CancellationToken ct) => Task.CompletedTask;
    }

    // ------------------------------------------------------------ documento no soportado

    [Fact]
    public async Task Documento_version_futura_ack_failed_orp_unsupported_document()
    {
        var document = TestDocuments.Ticket(5);
        document.Version = 2;
        _server.ClaimResponses[5] = ClaimedJob(5, document: document);
        using var outbox = new OutboxRepository(_databasePath);
        var processor = CreateProcessor(outbox: outbox);

        await processor.ProcessJobAsync(FetchedJob(5), CancellationToken.None);

        Assert.Equal(0, _print.Calls);
        var ack = Assert.Single(_server.Acks);
        Assert.Equal(JobResultStatus.Failed, ack.Status);
        Assert.Equal("orp_unsupported_document", ack.ErrorCode);
        Assert.Contains("documento 2", ack.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OutboxJobState.Error, outbox.Get(5)!.State);
    }

    [Fact]
    public async Task Claim_sin_documento_violacion_de_protocolo_ack_failed()
    {
        _server.ClaimResponses[6] = new Job { Id = 6, OrderId = 5678, PrinterRole = "default", Attempt = 1, Document = null };
        using var outbox = new OutboxRepository(_databasePath);
        var processor = CreateProcessor(outbox: outbox);

        await processor.ProcessJobAsync(FetchedJob(6), CancellationToken.None);

        Assert.Equal(0, _print.Calls);
        var ack = Assert.Single(_server.Acks);
        Assert.Equal(JobResultStatus.Failed, ack.Status);
        Assert.Equal("orp_unsupported_document", ack.ErrorCode);
        Assert.Equal(OutboxJobState.Error, outbox.Get(6)!.State);
    }

    // ------------------------------------------------------------ transitorios y release

    [Fact]
    public async Task Transitorio_reintenta_y_al_final_ack_delivered()
    {
        _server.ClaimResponses[7] = ClaimedJob(7);
        using var outbox = new OutboxRepository(_databasePath);
        var flaky = new FakePrintTransport(script: new Queue<PrintFailureKind?>([
            PrintFailureKind.Transient,
            PrintFailureKind.Transient,
            null, // tercer intento: bien
        ]));
        var processor = CreateProcessor(
            outbox: outbox,
            factory: new FakePrintTransportFactory(flaky),
            processorOptions: new JobProcessorOptions { MaxTransientRetries = 3, TransientRetryDelay = TimeSpan.FromMilliseconds(1) });

        await processor.ProcessJobAsync(FetchedJob(7), CancellationToken.None);

        Assert.Equal(3, flaky.Calls);
        Assert.Empty(_server.Releases); // no hizo falta release
        var ack = Assert.Single(_server.Acks);
        Assert.Equal(JobResultStatus.Delivered, ack.Status);
        Assert.Equal(OutboxJobState.Done, outbox.Get(7)!.State);
    }

    [Fact]
    public async Task Transitorio_agotado_release_sin_ack_y_vuelve_a_received()
    {
        _server.ClaimResponses[8] = ClaimedJob(8);
        using var outbox = new OutboxRepository(_databasePath);
        var flaky = new FakePrintTransport(script: new Queue<PrintFailureKind?>(
            [PrintFailureKind.Transient, PrintFailureKind.Transient, PrintFailureKind.Transient]));
        var processor = CreateProcessor(
            outbox: outbox,
            factory: new FakePrintTransportFactory(flaky),
            processorOptions: new JobProcessorOptions { MaxTransientRetries = 3, TransientRetryDelay = TimeSpan.FromMilliseconds(1) });

        await processor.ProcessJobAsync(FetchedJob(8), CancellationToken.None);

        // Reintentos locales + release SIN consumir intento (Doc/04 §5.7), sin ack.
        Assert.Equal(3, flaky.Calls);
        var release = Assert.Single(_server.Releases);
        Assert.Equal(8, release.JobId);
        Assert.Equal("spooler_timeout", release.Reason);
        Assert.Empty(_server.Acks);
        Assert.Equal(OutboxJobState.Received, outbox.Get(8)!.State); // reintento en el próximo ciclo/reinicio
    }

    [Fact]
    public async Task Trabajo_liberado_no_se_reprocesa_en_el_mismo_ciclo()
    {
        _server.ClaimResponses[9] = ClaimedJob(9);
        using var outbox = new OutboxRepository(_databasePath);
        var flaky = new FakePrintTransport(script: new Queue<PrintFailureKind?>(
            [PrintFailureKind.Transient, PrintFailureKind.Transient, PrintFailureKind.Transient]));
        var processor = CreateProcessor(
            outbox: outbox,
            factory: new FakePrintTransportFactory(flaky),
            processorOptions: new JobProcessorOptions { MaxTransientRetries = 1, TransientRetryDelay = TimeSpan.FromMilliseconds(1) });

        await processor.ProcessJobAsync(FetchedJob(9), CancellationToken.None);
        await processor.ProcessStartupQueueAsync(CancellationToken.None); // el drenaje no debe repetirlo

        Assert.Equal(1, flaky.Calls); // el segundo pase lo saltó (releasedThisRun)
        Assert.Single(_server.Releases);
        Assert.Empty(_server.Acks);
    }

    [Fact]
    public async Task Transitorio_sin_transporte_de_release_marca_error_y_ack_failed()
    {
        _server.ClaimResponses[12] = ClaimedJob(12);
        using var outbox = new OutboxRepository(_databasePath);
        var flaky = new FakePrintTransport(script: new Queue<PrintFailureKind?>(
            [PrintFailureKind.Transient, PrintFailureKind.Transient, PrintFailureKind.Transient]));
        // Servidor SIN IJobReleaseTransport (solo el ITransport mínimo).
        var bareServer = new BareServerTransport();
        var processor = CreateProcessor(
            outbox: outbox,
            factory: new FakePrintTransportFactory(flaky),
            processorOptions: new JobProcessorOptions { MaxTransientRetries = 1, TransientRetryDelay = TimeSpan.FromMilliseconds(1) },
            serverOverride: bareServer);

        await processor.ProcessJobAsync(FetchedJob(12), CancellationToken.None);

        var ack = Assert.Single(bareServer.Acks);
        Assert.Equal(JobResultStatus.Failed, ack.Status);
        Assert.Equal("orp_print_transient", ack.ErrorCode);
        Assert.Equal(OutboxJobState.Error, outbox.Get(12)!.State);
    }

    private sealed class BareServerTransport : ITransport
    {
        public List<JobResult> Acks { get; } = [];

        public Task<JobBatch> FetchAsync(Cursor cursor, CancellationToken ct) =>
            Task.FromResult(new JobBatch { Jobs = [], Cursor = cursor.Value });

        public Task<Job?> ClaimAsync(long jobId, CancellationToken ct) =>
            Task.FromResult<Job?>(jobId == 12 ? ClaimedJob(12) : null);

        public Task AckAsync(long jobId, JobResult result, CancellationToken ct)
        {
            Acks.Add(result);
            return Task.CompletedTask;
        }
    }

    // ------------------------------------------------------------ fallo definitivo

    [Fact]
    public async Task Fallo_definitivo_ack_failed_con_codigo_del_agente()
    {
        _server.ClaimResponses[13] = ClaimedJob(13);
        using var outbox = new OutboxRepository(_databasePath);
        var broken = new FakePrintTransport(script: new Queue<PrintFailureKind?>([PrintFailureKind.Definitive]));
        var processor = CreateProcessor(outbox: outbox, factory: new FakePrintTransportFactory(broken));

        await processor.ProcessJobAsync(FetchedJob(13), CancellationToken.None);

        Assert.Equal(1, broken.Calls); // sin reintentos: definitivo no reintenta
        var ack = Assert.Single(_server.Acks);
        Assert.Equal(JobResultStatus.Failed, ack.Status);
        Assert.Equal("orp_print_failed", ack.ErrorCode);
        Assert.Contains("fallo simulado", ack.Error);
        Assert.Equal(OutboxJobState.Error, outbox.Get(13)!.State);
    }

    // ------------------------------------------------------------ arranque y recuperación

    [Fact]
    public async Task Arranque_drena_la_cola_pendiente_con_documento()
    {
        // Fila de un ciclo anterior: recibida con documento (p. ej. release por spooler ocupado).
        using var outbox = new OutboxRepository(_databasePath);
        var document = TestDocuments.Ticket(21);
        outbox.Enqueue(21, "default", 1, System.Text.Json.JsonSerializer.Serialize(document, OrpJson.Default));
        _server.ClaimResponses[21] = ClaimedJob(21, document: document);
        var processor = CreateProcessor(outbox: outbox);

        await processor.ProcessStartupQueueAsync(CancellationToken.None);

        Assert.Equal(1, _print.Calls);
        Assert.Equal(OutboxJobState.Done, outbox.Get(21)!.State);
        var ack = Assert.Single(_server.Acks);
        Assert.Equal(JobResultStatus.Delivered, ack.Status);
    }

    [Fact]
    public async Task Trabajo_recuperado_duplicate_suspected_se_reimprime()
    {
        using var outbox = new OutboxRepository(_databasePath);
        var document = TestDocuments.Ticket(22);
        var documentJson = System.Text.Json.JsonSerializer.Serialize(document, OrpJson.Default);
        outbox.Enqueue(22, "default", 1, documentJson);
        outbox.TryClaim(22); // queda "printing": simulamos la caída aquí
        Assert.NotEmpty(outbox.RecoverInterrupted()); // arranque: reencolado y marcado
        _server.ClaimResponses[22] = ClaimedJob(22, document: document);
        var processor = CreateProcessor(outbox: outbox);

        await processor.ProcessStartupQueueAsync(CancellationToken.None);

        Assert.Equal(1, _print.Calls);
        Assert.Equal(OutboxJobState.Done, outbox.Get(22)!.State);
        Assert.True(outbox.Get(22)!.DuplicateSuspected); // el marcador queda para el informe
        Assert.Equal(JobResultStatus.Delivered, Assert.Single(_server.Acks).Status);
    }

    // ------------------------------------------------------------ logos

    [Fact]
    public async Task Logo_png_se_normaliza_a_raster_antes_de_renderizar()
    {
        var document = TestDocuments.Ticket(30);
        document.Blocks.Add(new ImageBlock { Data = Convert.ToBase64String(new byte[] { 0x89, 0x50 }), WidthDots = 8 });
        _server.ClaimResponses[30] = ClaimedJob(30, document: document);
        using var outbox = new OutboxRepository(_databasePath);
        var processor = CreateProcessor(outbox: outbox);

        await processor.ProcessJobAsync(FetchedJob(30), CancellationToken.None);

        Assert.Equal(1, _png.Calls);
        Assert.Equal(8, _png.LastTargetWidth);
        // GS v 0 + raster falso del decoder (0x80) en el flujo enviado.
        var escPos = _print.LastRequest!.EscPos;
        Assert.True(escPos.AsSpan().IndexOf((ReadOnlySpan<byte>)new byte[] { 0x1D, 0x76, 0x30, 0x00, 0x01, 0x00, 0x01, 0x00, 0x80 }) >= 0,
            "El flujo ESC/POS debería contener el raster normalizado: GS v 0 con xL=1 byte/fila, alto 1 y el bit 0x80.");
        Assert.Equal(JobResultStatus.Delivered, Assert.Single(_server.Acks).Status);
    }
}
