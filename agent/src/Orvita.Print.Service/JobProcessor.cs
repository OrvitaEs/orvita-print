using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orvita.Print.Contracts;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>Parámetros del procesador (de <see cref="AgentOptions"/>).</summary>
public sealed class JobProcessorOptions
{
    /// <summary>Reintentos locales ante fallo transitorio (spooler ocupado/red) antes del release.</summary>
    public int MaxTransientRetries { get; init; } = 3;

    /// <summary>Base de espera entre reintentos: se multiplica por el intento (2 s → 4 s → 6 s…).</summary>
    public TimeSpan TransientRetryDelay { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Pipeline de un trabajo (Doc/03 §6.3, tarea F2-07): reclamar en el servidor → insertar en la
/// cola local (idempotente) → guardar el documento → reclamar localmente (printing) →
/// normalizar logos → renderizar ESC/POS → imprimir (con copias, Doc/04 §2.8) → ack
/// delivered/failed → done/error local.
///
/// Semántica de fallos (Doc/04 §5.6/§5.7):
/// - Transitorio (spooler ocupado, red): reintentos locales; agotados → <c>POST /release</c>
///   SIN consumir intento y el trabajo vuelve a <c>received</c> local. Sin ack.
/// - Impresora inexistente / documento no soportado / definitivo: ack failed con el código del
///   agente (orp_printer_not_found, orp_unsupported_document, orp_print_failed) y estado error local.
/// - Un trabajo recuperado tras caída se reimprime (criterio de producto: mejor un duplicado
///   que un pedido perdido, Doc/03 §6.4); el marcador <c>duplicate_suspected</c> queda en la cola.
///
/// Un trabajo cuyo rol no corresponde a una salida configurada NO se reclama: permanece
/// <c>pending</c> en el servidor (Doc/04 §5.8) y no se pierde.
///
/// El transporte de servidor se resuelve de forma perezosa: el agente sin emparejar (sin
/// URL/token aún) arranca vivo y sin trabajos, y DI no intenta construir un <c>DirectTransport</c> nulo.
/// </summary>
public sealed class JobProcessor
{
    private readonly Func<ITransport> _server;
    private readonly OutboxRepository _outbox;
    private readonly IDestinationProvider _destinations;
    private readonly IPrintTransportFactory _transportFactory;
    private readonly Func<EscPosRendererOptions> _rendererOptions;
    private readonly IPngDecoder? _pngDecoder;
    private readonly JobProcessorOptions _options;
    private readonly ILogger _logger;

    /// <summary>Trabajos liberados en esta ejecución: no se reprocesan hasta el siguiente fetch/reinicio.</summary>
    private readonly HashSet<long> _releasedThisRun = [];

    public JobProcessor(
        Func<ITransport> server,
        OutboxRepository outbox,
        IDestinationProvider destinations,
        IPrintTransportFactory transportFactory,
        Func<EscPosRendererOptions>? rendererOptions = null,
        IPngDecoder? pngDecoder = null,
        JobProcessorOptions? options = null,
        ILogger? logger = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _destinations = destinations ?? throw new ArgumentNullException(nameof(destinations));
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _rendererOptions = rendererOptions ?? (() => new EscPosRendererOptions());
        _pngDecoder = pngDecoder;
        _options = options ?? new JobProcessorOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>Número de trabajos pendientes en la cola local (received + printing), para el heartbeat.</summary>
    public int PendingLocalCount =>
        _outbox.GetByState(OutboxJobState.Received).Count + _outbox.GetByState(OutboxJobState.Printing).Count;

    /// <summary>Último error registrado en la cola local (para el heartbeat), o null.</summary>
    public string? LastError =>
        _outbox.GetByState(OutboxJobState.Error)
            .OrderByDescending(j => j.UpdatedAt)
            .FirstOrDefault()?.ErrorMessage;

    /// <summary>Estado del resultado de una reimpresión local (GUI-15, decisión A).</summary>
    public enum LocalRetryStatus
    {
        /// <summary>El trabajo no está en la cola local.</summary>
        NotFound,

        /// <summary>El trabajo no está en error/done (o la salida no existe): sin cambios.</summary>
        NotRetryable,

        /// <summary>Reimpresión ejecutada: el trabajo quedó done o error (ver el OutboxJob).</summary>
        Completed,
    }

    /// <summary>Resultado de <see cref="RetryAsync"/>: el trabajo actualizado (o el motivo).</summary>
    public sealed record LocalRetryResult(
        long JobId,
        LocalRetryStatus Status,
        OutboxJob? Job,
        string? Reason = null)
    {
        public static LocalRetryResult NotFound(long jobId) => new(jobId, LocalRetryStatus.NotFound, null);
    }

    /// <summary>Punto de entrada del Poller: cada trabajo del lote.</summary>
    public Task ProcessJobAsync(Job job, CancellationToken cancellationToken) =>
        ProcessClaimedFlowAsync(job.Id, job.PrinterRole, cancellationToken);

    /// <summary>Drenaje de arranque: trabajos ya recibidos con documento (de un ciclo anterior o recuperación).</summary>
    public async Task ProcessStartupQueueAsync(CancellationToken cancellationToken)
    {
        foreach (var row in _outbox.GetByState(OutboxJobState.Received).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessClaimedFlowAsync(row.JobId, row.PrinterRole, cancellationToken).ConfigureAwait(false);
        }
    }

    // ----------------------------------------------------------------- reimpresión local (GUI-15)

    /// <summary>
    /// Reimprimir un trabajo de la cola local (GUI-15, decisión A): el outbox ya tiene el
    /// documento — el agente NO necesita al servidor para sacar papel. Válido para trabajos
    /// en <c>error</c> y en <c>done</c>. NUNCA lanza: los problemas son estados visibles.
    /// <list type="bullet">
    /// <item>Primero se intenta reclamar en el servidor (best-effort): si el trabajo sigue
    /// <c>pending</c> allí (p. ej. release anterior), el claim lo recupera y evita que la
    /// ronda del poller lo reimprima de nuevo (duplicado).</item>
    /// <item>Si no se puede reclamar (delivered/failed terminal, otro agente o red caída),
    /// se imprime 100 % LOCAL: el servidor queda como estaba y el ack es de cortesía
    /// (decisión B — el ack del plugin es idempotente y responde job_status sin error).</item>
    /// </list>
    /// </summary>
    public async Task<LocalRetryResult> RetryAsync(long jobId, CancellationToken cancellationToken)
    {
        var row = _outbox.Get(jobId);
        if (row is null)
        {
            return LocalRetryResult.NotFound(jobId);
        }

        if (row.State is OutboxJobState.Received or OutboxJobState.Printing)
        {
            return new LocalRetryResult(
                jobId, LocalRetryStatus.NotRetryable, row,
                "El trabajo ya está pendiente o imprimiéndose en la cola local.");
        }

        if (row.State is not (OutboxJobState.Error or OutboxJobState.Done))
        {
            return new LocalRetryResult(jobId, LocalRetryStatus.NotRetryable, row, $"Estado «{row.State}» no re-imprimible.");
        }

        var destination = ResolveDestination(row.PrinterRole);
        if (destination is null)
        {
            return new LocalRetryResult(
                jobId, LocalRetryStatus.NotRetryable, row,
                $"La salida «{row.PrinterRole ?? "default"}» no está configurada o está desactivada en este agente.");
        }

        _outbox.RetryRequeue(jobId); // error|done → received (limpia error y marca de revisión)
        row = _outbox.Get(jobId)!;
        _logger.LogInformation(
            "Reimpresión local del trabajo {JobId} pedida por la GUI (estado anterior: {State}, intentos: {Attempts}).",
            jobId, row.State, row.Attempts);

        // Claim best-effort al servidor: 200 solo si el trabajo sigue pending allí.
        Document? serverDocument = null;
        var serverAttempt = 0;
        try
        {
            var claimed = await _server().ClaimAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (claimed is not null)
            {
                serverAttempt = claimed.Attempt;
                if (claimed.Document is not null)
                {
                    serverDocument = claimed.Document;
                    _outbox.UpdateDocument(jobId, JsonSerializer.Serialize(claimed.Document, OrpJson.Default));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Sin servidor también se reimprime (reimpresión LOCAL): el ack de cortesía
            // informará al servidor si vuelve a estar disponible.
            _logger.LogWarning(
                "No se pudo reclamar el trabajo {JobId} en el servidor ({Message}); se reimprime localmente con el documento del outbox.",
                jobId, ex.Message);
        }

        row = _outbox.Get(jobId)!;
        if (!_outbox.TryClaim(jobId))
        {
            // Otro hilo se lo llevó en el ínterin: reportamos su estado tal cual.
            return new LocalRetryResult(jobId, LocalRetryStatus.NotRetryable, _outbox.Get(jobId), "El trabajo cambió de estado mientras se reclamaba.");
        }

        row = _outbox.Get(jobId)!;
        await PrintRetryAsync(row, serverDocument, destination, serverAttempt, cancellationToken).ConfigureAwait(false);

        row = _outbox.Get(jobId)!;
        return new LocalRetryResult(jobId, LocalRetryStatus.Completed, row);
    }

    /// <summary>
    /// Bucle de impresión de la reimpresión local (reintentos transitorios como el flujo
    /// normal) con acks BEST-EFFORT: un fallo de red en el ack jamás revienta la operación
    /// de la GUI (decisión B). Sin release al servidor: la reimpresión es local.
    /// </summary>
    private async Task PrintRetryAsync(
        OutboxJob row,
        Document? serverDocument,
        DestinationOptions destination,
        int serverAttempt,
        CancellationToken cancellationToken)
    {
        Document document;
        try
        {
            document = serverDocument ?? row.TryParseDocument()
                ?? throw new PrintTransportException(
                    PrintFailureKind.UnsupportedDocument,
                    "El documento del trabajo no se pudo interpretar (JSON malformado).");
            if (document.Version > 1)
            {
                throw new PrintTransportException(
                    PrintFailureKind.UnsupportedDocument,
                    $"Versión de documento {document.Version} no soportada por este agente (Doc/04 §8).");
            }
        }
        catch (PrintTransportException ex)
        {
            _logger.LogError("El trabajo {JobId} no se puede reimprimir ({Code}): {Message}", row.JobId, ex.ErrorCode, ex.Message);
            _outbox.MarkError(row.JobId, ex.ErrorCode, ex.Message);
            await AckBestEffortAsync(row, destination, JobResultStatus.Failed, ex.ErrorCode, ex.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] escPos;
        try
        {
            escPos = Render(document);
        }
        catch (NotSupportedException ex)
        {
            _outbox.MarkError(row.JobId, ErrorCode.OrpUnsupportedDocument.ToWireCode(), ex.Message);
            await AckBestEffortAsync(row, destination, JobResultStatus.Failed, ErrorCode.OrpUnsupportedDocument.ToWireCode(), ex.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var request = new PrintRequest
        {
            JobId = row.JobId,
            Destination = destination,
            Document = document,
            EscPos = escPos,
            Copies = Math.Max(1, row.Copies),
        };

        var transport = _transportFactory.Create(destination);
        var clock = Stopwatch.StartNew();
        var attempt = 1;

        while (true)
        {
            try
            {
                await transport.PrintAsync(request, cancellationToken).ConfigureAwait(false);

                _outbox.MarkDone(row.JobId);
                _logger.LogInformation(
                    "Trabajo {JobId} reimpreso en «{Printer}» ({Copies} copia(s), {Elapsed} ms).",
                    row.JobId, destination.PrinterName ?? destination.Address ?? destination.Role, request.Copies, clock.ElapsedMilliseconds);

                await AckRetryDeliveredAsync(row, destination, serverAttempt, clock.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (PrintTransportException ex) when (ex.Kind == PrintFailureKind.Transient)
            {
                if (attempt >= Math.Max(1, _options.MaxTransientRetries))
                {
                    _logger.LogError(
                        "La reimpresión del trabajo {JobId} agotó los reintentos transitorios: {Message}",
                        row.JobId, ex.Message);
                    _outbox.MarkError(row.JobId, ex.ErrorCode, ex.Message);
                    await AckBestEffortAsync(row, destination, JobResultStatus.Failed, ex.ErrorCode, ex.Message, null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                _logger.LogWarning(
                    "Fallo transitorio reimprimiendo el trabajo {JobId} (intento {Attempt}/{Max}): {Message}",
                    row.JobId, attempt, _options.MaxTransientRetries, ex.Message);

                await Task.Delay(_options.TransientRetryDelay * attempt, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
            catch (PrintTransportException ex)
            {
                _logger.LogError(
                    "La reimpresión del trabajo {JobId} falló definitivamente ({Code}): {Message}",
                    row.JobId, ex.ErrorCode, ex.Message);
                _outbox.MarkError(row.JobId, ex.ErrorCode, ex.Message);
                await AckBestEffortAsync(row, destination, JobResultStatus.Failed, ex.ErrorCode, ex.Message, clock.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>
    /// Ack de cortesía (decisión B): intenta ack delivered tras una reimpresión exitosa. Si
    /// el servidor mantiene el trabajo en <c>failed</c> (terminal: el ack es idempotente y
    /// responde su estado real sin error), se registra en el log y se sigue — la GUI nunca
    /// ve un error por esto.
    /// </summary>
    private async Task AckRetryDeliveredAsync(
        OutboxJob row,
        DestinationOptions destination,
        int serverAttempt,
        long elapsedMs,
        CancellationToken cancellationToken)
    {
        try
        {
            var ack = await _server().AckDetailedAsync(
                row.JobId,
                new JobResult
                {
                    Status = JobResultStatus.Delivered,
                    Attempt = serverAttempt > 0 ? serverAttempt : row.Attempts,
                    Printer = destination.PrinterName,
                    ElapsedMs = elapsedMs,
                },
                cancellationToken).ConfigureAwait(false);

            if (ack is { JobStatus: not null and not "delivered" })
            {
                _logger.LogWarning(
                    "Papel impreso localmente para el trabajo {JobId}; el servidor mantiene {ServerStatus} — " +
                    "usa «Reimprimir ticket» del panel para regenerar el trabajo en la cola del servidor.",
                    row.JobId, ack.JobStatus);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "El ack de cortesía del trabajo {JobId} no llegó al servidor (best-effort): {Message}",
                row.JobId, ex.Message);
        }
    }

    /// <summary>Ack failed best-effort: igual que el flujo normal pero SIN propagar el fallo.</summary>
    private async Task AckBestEffortAsync(
        OutboxJob row,
        DestinationOptions destination,
        JobResultStatus status,
        string errorCode,
        string message,
        long? elapsedMs,
        CancellationToken cancellationToken)
    {
        try
        {
            await _server().AckDetailedAsync(
                row.JobId,
                new JobResult
                {
                    Status = status,
                    Attempt = row.Attempts > 0 ? row.Attempts : 1,
                    Printer = destination.PrinterName,
                    ElapsedMs = elapsedMs,
                    ErrorCode = errorCode,
                    Error = message,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "El ack de cortesía (failed) del trabajo {JobId} no llegó al servidor: {Message}",
                row.JobId, ex.Message);
        }
    }

    // ------------------------------------------------------------ pipeline

    private async Task ProcessClaimedFlowAsync(long jobId, string? role, CancellationToken cancellationToken)
    {
        if (_releasedThisRun.Contains(jobId))
        {
            return;
        }

        var destination = ResolveDestination(role);
        if (destination is null)
        {
            return;
        }

        var server = _server();
        var claimed = await server.ClaimAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            // 409 (otro agente), 410 (caducado) o 404 (ajeno): el trabajo ya no es nuestro; sin ack.
            if (_outbox.TryClaim(jobId))
            {
                _outbox.MarkError(
                    jobId,
                    "orp_job_not_claimed",
                    "Reclamado por otro agente, caducado o ajeno a este local (409/410/404); no se imprime.");
            }

            _logger.LogWarning("El trabajo {JobId} ya no se puede reclamar (409/410/404); se descarta localmente.", jobId);
            return;
        }

        _outbox.Enqueue(claimed); // idempotente por job id

        if (claimed.Document is null)
        {
            // Claim 200 sin documento: violación de protocolo; lo tratamos como no soportado.
            const string message = "El servidor no incluyó el documento neutro en la respuesta del claim.";
            if (_outbox.TryClaim(jobId))
            {
                _outbox.MarkError(jobId, ErrorCode.OrpUnsupportedDocument.ToWireCode(), message);
            }

            await AckFailedAsync(jobId, claimed.Attempt, destination, ErrorCode.OrpUnsupportedDocument.ToWireCode(), message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        _outbox.UpdateDocument(jobId, JsonSerializer.Serialize(claimed.Document, OrpJson.Default));

        if (!_outbox.TryClaim(jobId))
        {
            // Ya estaba en marcha (otro hilo) o el estado cambió: no lo duplicamos.
            return;
        }

        var row = _outbox.Get(jobId);
        if (row is null)
        {
            return;
        }

        if (row.DuplicateSuspected)
        {
            _logger.LogWarning(
                "El trabajo {JobId} se reimprime tras una caída o corte (duplicate_suspected): mejor un duplicado que un pedido perdido (Doc/03 §6.4).",
                jobId);
        }

        var document = row.TryParseDocument();
        if (document is null || document.Version > 1)
        {
            var reason = document is null
                ? "El documento del trabajo no se pudo interpretar (JSON malformado)."
                : $"Versión de documento {document.Version} no soportada por este agente (Doc/04 §8).";
            await FailUnsupportedAsync(row, destination, reason, cancellationToken).ConfigureAwait(false);
            return;
        }

        await PrintWithRetriesAsync(row, document, destination, claimed.Attempt, cancellationToken).ConfigureAwait(false);
    }

    private async Task PrintWithRetriesAsync(
        OutboxJob row,
        Document document,
        DestinationOptions destination,
        int serverAttempt,
        CancellationToken cancellationToken)
    {
        byte[] escPos;
        try
        {
            escPos = Render(document);
        }
        catch (NotSupportedException ex)
        {
            await FailUnsupportedAsync(row, destination, ex.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        var request = new PrintRequest
        {
            JobId = row.JobId,
            Destination = destination,
            Document = document,
            EscPos = escPos,
            Copies = Math.Max(1, row.Copies),
        };

        var transport = _transportFactory.Create(destination);
        var clock = Stopwatch.StartNew();
        var attempt = 1;

        while (true)
        {
            try
            {
                await transport.PrintAsync(request, cancellationToken).ConfigureAwait(false);

                _outbox.MarkDone(row.JobId);
                _logger.LogInformation(
                    "Trabajo {JobId} impreso en «{Printer}» ({Copies} copia(s), {Elapsed} ms).",
                    row.JobId, destination.PrinterName ?? destination.Address ?? destination.Role, request.Copies, clock.ElapsedMilliseconds);

                await _server().AckAsync(
                    row.JobId,
                    new JobResult
                    {
                        Status = JobResultStatus.Delivered,
                        Attempt = serverAttempt > 0 ? serverAttempt : row.Attempts,
                        Printer = destination.PrinterName,
                        ElapsedMs = clock.ElapsedMilliseconds,
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (PrintTransportException ex) when (ex.Kind == PrintFailureKind.Transient)
            {
                if (attempt >= Math.Max(1, _options.MaxTransientRetries))
                {
                    await ReleaseAfterTransientAsync(row, destination, serverAttempt, ex, cancellationToken).ConfigureAwait(false);
                    return;
                }

                _logger.LogWarning(
                    "Fallo transitorio imprimiendo el trabajo {JobId} (intento {Attempt}/{Max}): {Message} Reintento en {Delay} s…",
                    row.JobId, attempt, _options.MaxTransientRetries, ex.Message,
                    (int)_options.TransientRetryDelay.TotalSeconds * attempt);

                await Task.Delay(_options.TransientRetryDelay * attempt, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
            catch (PrintTransportException ex)
            {
                // Impresora no encontrada, documento no soportado o fallo definitivo: ack failed.
                _logger.LogError(
                    "El trabajo {JobId} falló definitivamente ({Code}): {Message}",
                    row.JobId, ex.ErrorCode, ex.Message);
                _outbox.MarkError(row.JobId, ex.ErrorCode, ex.Message);
                await AckFailedAsync(row.JobId, serverAttempt, destination, ex.ErrorCode, ex.Message, clock.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>Agotados los reintentos transitorios: release sin consumir intento (Doc/04 §5.7) y vuelta a la cola local.</summary>
    private async Task ReleaseAfterTransientAsync(
        OutboxJob row,
        DestinationOptions destination,
        int serverAttempt,
        PrintTransportException failure,
        CancellationToken cancellationToken)
    {
        _logger.LogError(
            "El trabajo {JobId} agotó los reintentos transitorios: {Message} Se libera en el servidor (release) sin consumir intento.",
            row.JobId, failure.Message);

        if (_server() is IJobReleaseTransport releaser)
        {
            try
            {
                await releaser.ReleaseAsync(row.JobId, "spooler_timeout", cancellationToken).ConfigureAwait(false);
                _outbox.ResetToReceived(row.JobId);
                _releasedThisRun.Add(row.JobId);
                _logger.LogInformation(
                    "El trabajo {JobId} vuelve a pending en el servidor y a received en la cola local; se reintentará más tarde.",
                    row.JobId);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo liberar el trabajo {JobId} en el servidor (release); se marca como error local.", row.JobId);
            }
        }

        // Sin transporte de release (o release fallido): queda error local y ack failed transitorio.
        _outbox.MarkError(row.JobId, failure.ErrorCode, failure.Message);
        await AckFailedAsync(row.JobId, serverAttempt, destination, failure.ErrorCode, failure.Message, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailUnsupportedAsync(OutboxJob row, DestinationOptions destination, string reason, CancellationToken cancellationToken)
    {
        _logger.LogError("El trabajo {JobId} no se puede imprimir: {Reason}", row.JobId, reason);
        _outbox.MarkError(row.JobId, ErrorCode.OrpUnsupportedDocument.ToWireCode(), reason);
        await AckFailedAsync(row.JobId, row.Attempts, destination, ErrorCode.OrpUnsupportedDocument.ToWireCode(), reason, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task AckFailedAsync(
        long jobId,
        int attempt,
        DestinationOptions destination,
        string errorCode,
        string message,
        long? elapsedMs,
        CancellationToken cancellationToken)
    {
        await _server().AckAsync(
            jobId,
            new JobResult
            {
                Status = JobResultStatus.Failed,
                Attempt = attempt > 0 ? attempt : 1,
                Printer = destination.PrinterName,
                ElapsedMs = elapsedMs,
                ErrorCode = errorCode,
                Error = message,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private DestinationOptions? ResolveDestination(string? role)
    {
        var destination = _destinations.Resolve(role);
        if (destination is null)
        {
            _logger.LogInformation(
                "La salida «{Role}» no está configurada en este agente: el trabajo permanece pending en el servidor (Doc/04 §5.8).",
                role ?? "default");
            return null;
        }

        if (!destination.Enabled)
        {
            _logger.LogInformation("La salida «{Role}» está desactivada: el trabajo permanece pending en el servidor.", role ?? "default");
            return null;
        }

        return destination;
    }

    private byte[] Render(Document document)
    {
        // Normalización de logos (Doc/04 §7.2: PNG base64): a raster 1bpp con .NET puro
        // antes del renderizado; si un bloque de imagen no se puede decodificar, el
        // propio decoder avisa y lo deja fuera (nunca bytes sospechosos a la impresora).
        var normalized = _pngDecoder is null ? document : NormalizeImages(document);

        var renderer = new EscPosRenderer(_rendererOptions(), message => _logger.LogWarning("{Message}", message));
        return renderer.Render(normalized);
    }

    private Document NormalizeImages(Document document)
    {
        var changed = false;
        var blocks = new List<Block>(document.Blocks.Count);

        foreach (var block in document.Blocks)
        {
            if (block is ImageBlock { Data: not null, WidthDots: > 0 } image)
            {
                var raster = _pngDecoder!.TryDecodeToRaster(image.Data, Math.Min(image.WidthDots!.Value, EscPosRendererOptions.MaxImageWidthDots), message => _logger.LogWarning("{Message}", message));
                if (raster is not null)
                {
                    blocks.Add(new ImageBlock
                    {
                        Data = Convert.ToBase64String(raster.Bitmap1Bpp),
                        WidthDots = raster.WidthDots,
                    });
                    changed = true;
                    continue;
                }
            }

            blocks.Add(block);
        }

        return changed
            ? new Document { Version = document.Version, Meta = document.Meta, Paper = document.Paper, Blocks = blocks }
            : document;
    }
}
