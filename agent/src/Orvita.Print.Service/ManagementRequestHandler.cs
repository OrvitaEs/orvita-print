using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Printing.Windows;

namespace Orvita.Print.Service;

/// <summary>
/// Argumentos de las operaciones de la pipe (serializados con ManagementProtocol.Options).
/// </summary>
public sealed class PairArgs
{
    public string? Server { get; set; }

    public string? Code { get; set; }

    public string? AgentName { get; set; }

    public bool AllowInsecureHttp { get; set; }
}

public sealed class DestinationAddArgs
{
    public string? Role { get; set; }

    public string? Label { get; set; }

    public string? Printer { get; set; }

    public int? Copies { get; set; }

    public bool? Enabled { get; set; }

    public string? Kind { get; set; }

    public string? Address { get; set; }
}

public sealed class DestinationRemoveArgs
{
    public string? Role { get; set; }
}

public sealed class TestArgs
{
    public string? Role { get; set; }
}

public sealed class DoctorArgs
{
    /// <summary>true → el servicio genera el ZIP de diagnóstico y devuelve su ruta (F2-12).</summary>
    public bool Zip { get; set; }
}

public sealed class QueueListArgs
{
    /// <summary>Máximo de trabajos devueltos (Doc/08 §3: por defecto 50, tope 200).</summary>
    public int? Limit { get; set; }
}

public sealed class QueueItemArgs
{
    public long? JobId { get; set; }
}

/// <summary>
/// Despachador de operaciones de la pipe de gestión (F2-08, ADR-13): pair, destination
/// add/list/remove, status, test, doctor y restart-poller. NO expone el token NUNCA (S-05):
/// <c>status</c> lo oculta (la URL no lo contiene) y <c>pair</c> responde sin eco.
/// Sin estado propio: cada operación lee el almacén/cola/transporte al vuelo, de modo que
/// responde siempre lo que el servicio vive en ese instante.
/// </summary>
public sealed class ManagementRequestHandler
{
    private readonly ServerConnection _connection;
    private readonly LocalConfigStore _store;
    private readonly OutboxRepository _outbox;
    private readonly PairingService _pairing;
    private readonly PollerController _poller;
    private readonly TestPrintService _testPrint;
    private readonly IPrinterEnumerator _printers;
    private readonly RemoteConfigState _configState;
    private readonly AgentRuntimeStatus _runtime;
    private readonly AgentOptions _options;
    private readonly DiagnosticsBundleBuilder? _diagnostics;
    private readonly LicenseState? _license;
    private readonly ILogger _logger;

    public ManagementRequestHandler(
        ServerConnection connection,
        LocalConfigStore store,
        OutboxRepository outbox,
        PairingService pairing,
        PollerController poller,
        TestPrintService testPrint,
        IPrinterEnumerator printers,
        RemoteConfigState configState,
        AgentRuntimeStatus runtime,
        AgentOptions options,
        ILogger<ManagementRequestHandler> logger,
        DiagnosticsBundleBuilder? diagnostics = null,
        LicenseState? license = null)
    {
        _connection = connection;
        _store = store;
        _outbox = outbox;
        _pairing = pairing;
        _poller = poller;
        _testPrint = testPrint;
        _printers = printers;
        _configState = configState;
        _runtime = runtime;
        _options = options;
        _logger = logger;
        _diagnostics = diagnostics;
        _license = license;
    }

    /// <summary>Procesa una solicitud. Nunca lanza: los errores van como respuesta (ok:false).</summary>
    public async Task<ManagementResponse> HandleAsync(ManagementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Op))
        {
            return ManagementResponse.Failure(request.Id, "Falta la operación (op).", "unknown_operation");
        }

        try
        {
            return await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // parada del servicio: la propagamos (el servidor de pipe la entiende)
        }
        catch (PairingException ex)
        {
            _logger.LogWarning("pair rechazado por el servidor ({Code}): {Message}", ex.Code, ex.Message);
            return ManagementResponse.Failure(request.Id, ex.Message, ex.Code);
        }
        catch (ArgumentException ex)
        {
            return ManagementResponse.Failure(request.Id, ex.Message, "invalid_argument");
        }
        catch (PrintTransportException ex)
        {
            return ManagementResponse.Failure(request.Id, ex.Message, "print_error");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "La operación {Op} falló de forma inesperada.", request.Op);
            return ManagementResponse.Failure(request.Id, $"Error interno de la operación «{request.Op}»: {ex.Message}", "internal_error");
        }
    }

    private async Task<ManagementResponse> DispatchAsync(ManagementRequest request, CancellationToken cancellationToken)
    {
        return request.Op switch
        {
            ManagementProtocol.Operations.Ping => ManagementResponse.Success(request.Id, new
            {
                pong = true,
                version = AgentVersion.Current,
            }),
            ManagementProtocol.Operations.Pair => await PairAsync(request, cancellationToken).ConfigureAwait(false),
            ManagementProtocol.Operations.DestinationAdd => DestinationAdd(request),
            ManagementProtocol.Operations.DestinationList => DestinationList(request.Id),
            ManagementProtocol.Operations.DestinationRemove => DestinationRemove(request),
            ManagementProtocol.Operations.Status => Status(request.Id),
            ManagementProtocol.Operations.Test => await TestAsync(request, cancellationToken).ConfigureAwait(false),
            ManagementProtocol.Operations.Doctor => Doctor(request),
            ManagementProtocol.Operations.RestartPoller => await RestartPollerAsync(request, cancellationToken).ConfigureAwait(false),
            ManagementProtocol.Operations.QueueList => QueueList(request),
            ManagementProtocol.Operations.QueueItem => QueueItem(request),
            _ => ManagementResponse.Failure(request.Id, $"Operación desconocida «{request.Op}».", "unknown_operation"),
        };
    }

    // ------------------------------------------------------------------------------- pair

    private async Task<ManagementResponse> PairAsync(ManagementRequest request, CancellationToken cancellationToken)
    {
        var args = BindArgs<PairArgs>(request);
        if (string.IsNullOrWhiteSpace(args.Server))
        {
            return ManagementResponse.Failure(request.Id, "Falta --server <url> (https://tienda.example/wp-json/orp-print/v1).", "invalid_argument");
        }

        if (string.IsNullOrWhiteSpace(args.Code))
        {
            return ManagementResponse.Failure(request.Id, "Falta el código de emparejamiento (6 dígitos, lo genera el panel del plugin).", "invalid_argument");
        }

        var outcome = await _pairing.ApplyPairingAsync(
            args.Server,
            args.Code.Trim(),
            args.AgentName,
            args.AllowInsecureHttp,
            cancellationToken).ConfigureAwait(false);

        // SIN eco del token (S-05): el servidor lo mostró una sola vez al generarlo (Doc/04 §5.2).
        return ManagementResponse.Success(request.Id, new
        {
            server_url = outcome.ServerUrl,
            location = new
            {
                id = outcome.LocationId,
                code = outcome.LocationCode,
                name = outcome.LocationName,
            },
            poll_interval = outcome.PollIntervalSeconds,
        });
    }

    // ----------------------------------------------------------------------- destination

    private ManagementResponse DestinationAdd(ManagementRequest request)
    {
        var args = BindArgs<DestinationAddArgs>(request);
        var role = string.IsNullOrWhiteSpace(args.Role) ? "default" : args.Role.Trim();
        var kind = LocalConfigStore.ParseKindName(args.Kind);
        var address = string.IsNullOrWhiteSpace(args.Address) ? null : args.Address.Trim();
        var printer = string.IsNullOrWhiteSpace(args.Printer) ? null : args.Printer.Trim();

        // Salida tipo «pdf» (pruebas/demos): la impresora es SIEMPRE «Microsoft Print to PDF»
        // (sin diálogos); si algún cliente de la pipe manda otra, se registra el aviso y se
        // ignora. Sin directorio, el default del transporte (%ProgramData%\OrvitaPrint\prints\)
        // se guarda explícito para que status/destination list lo muestren.
        var warnings = new List<string>();
        if (kind == PrintTransportKind.Pdf)
        {
            if (!string.IsNullOrWhiteSpace(printer)
                && !string.Equals(printer, PdfTransport.PrinterName, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"En una salida pdf la impresora es siempre «{PdfTransport.PrinterName}»: se ignora «{printer}» (el directorio de los PDFs va en --address).");
            }

            printer = PdfTransport.PrinterName;
            address ??= PdfTransport.DefaultDirectory;
        }

        if (kind == PrintTransportKind.Tcp9100 && string.IsNullOrWhiteSpace(address))
        {
            return ManagementResponse.Failure(
                request.Id,
                "Una salida TCP 9100 necesita --address host[:puerto] (p. ej. 192.168.1.50 o 192.168.1.50:9100).",
                "invalid_argument");
        }

        if (string.IsNullOrWhiteSpace(printer) && string.IsNullOrWhiteSpace(address))
        {
            return ManagementResponse.Failure(
                request.Id,
                "Falta --printer \"nombre exacto de la impresora\" (o --address para una salida TCP 9100). " +
                "Usa orvitaprint destination list para ver los nombres EXACTOS instalados (Doc/03 §6.6).",
                "invalid_argument");
        }

        var destination = new DestinationOptions
        {
            Role = role,
            Label = string.IsNullOrWhiteSpace(args.Label) ? null : args.Label.Trim(),
            PrinterName = printer,
            Copies = Math.Clamp(args.Copies ?? 1, 1, 99),
            Enabled = args.Enabled ?? true,
            Kind = kind,
            Address = address,
        };

        // Validación contra la enumeración de impresoras (F2-05) y aviso de impresora repetida
        // (Doc/03 §6.7: para dos copias está copies, no dos salidas). Son AVISOS: se guarda igual,
        // porque la impresora puede conectarse más tarde (el fallo explícito llega al imprimir).
        // En pdf no aplica el aviso de impresora repetida: dos salidas pdf a directorios
        // distintos comparten impresora y es legítimo.
        var printerFound = true;
        if (printer is not null)
        {
            var installed = _printers.List();
            if (installed.Count == 0)
            {
                printerFound = false;
                warnings.Add("No hay impresoras instaladas en este equipo: la salida se guarda, pero no imprimirá hasta que instales la impresora (Doc/03 §6.6).");
            }
            else if (_printers.Find(printer) is null)
            {
                printerFound = false;
                warnings.Add($"La impresora «{printer}» no aparece entre las instaladas en Windows. Comprueba el nombre EXACTO con orvitaprint destination list (Doc/03 §6.6).");
            }
        }

        var twin = destination.Kind == PrintTransportKind.Pdf
            ? null
            : _store.ListDestinations().FirstOrDefault(d =>
                d.Role != destination.Role &&
                !string.IsNullOrWhiteSpace(d.PrinterName) &&
                string.Equals(d.PrinterName, destination.PrinterName, StringComparison.OrdinalIgnoreCase));
        if (twin is not null)
        {
            warnings.Add($"La salida «{twin.Role}» ya apunta a la misma impresora «{twin.PrinterName}»: para dos copias usa --copies (Doc/03 §6.7).");
        }

        var created = _store.UpsertDestination(destination);
        _logger.LogInformation(
            "Salida «{Role}» {Action} (impresora «{Printer}», copias {Copies}, activa {Enabled}) con {Warnings} aviso(s).",
            role, created ? "creada" : "actualizada", printer, destination.Copies, destination.Enabled, warnings.Count);

        return ManagementResponse.Success(request.Id, new
        {
            role = destination.Role,
            action = created ? "created" : "updated",
            printer_found = printerFound,
            destination = ToWire(destination),
            warnings,
        });
    }

    private ManagementResponse DestinationList(long id)
    {
        var destinations = _store.ListDestinations();
        var installed = _printers.List();
        return ManagementResponse.Success(id, new
        {
            destinations = destinations.Select(ToWire).ToArray(),
            printers = installed.Select(p => new { name = p.Name, @default = p.IsDefault, hint = p.StatusHint }).ToArray(),
            default_printer = _printers.DefaultPrinterName(),
            no_printers_note = installed.Count == 0 ? "No hay impresoras instaladas en este equipo (Doc/03 §6.6)." : null,
        });
    }

    private ManagementResponse DestinationRemove(ManagementRequest request)
    {
        var args = BindArgs<DestinationRemoveArgs>(request);
        var role = string.IsNullOrWhiteSpace(args.Role) ? "default" : args.Role.Trim();
        if (!_store.RemoveDestination(role))
        {
            return ManagementResponse.Failure(request.Id, $"La salida «{role}» no existe.", "destination_not_found");
        }

        return ManagementResponse.Success(request.Id, new { role, removed = true });
    }

    // ----------------------------------------------------------------------------- status

    private ManagementResponse Status(long id)
    {
        var pairing = _store.GetPairing();
        var transport = _connection.Transport;
        var (seen, attempt, heartbeatError) = _runtime.Snapshot();

        var pending = _outbox.CountByState(OutboxJobState.Received) + _outbox.CountByState(OutboxJobState.Printing);
        var done = _outbox.CountByState(OutboxJobState.Done);
        var errors = _outbox.CountByState(OutboxJobState.Error);
        var lastJob = _outbox.GetLatestJob();
        var lastError = _outbox.GetByState(OutboxJobState.Error)
            .OrderByDescending(j => j.UpdatedAt)
            .FirstOrDefault();
        var installed = _printers.List();

        return ManagementResponse.Success(id, new
        {
            version = AgentVersion.Current,
            platform = "windows",
            uptime_started = _runtime.StartedUtc,
            // Token OCULTO (S-05): la URL nunca lo contiene; del token ni el nombre.
            paired = pairing.IsPaired && transport is not null,
            server_url = pairing.ServerUrl,
            location = new { id = pairing.LocationId, code = pairing.LocationCode, name = pairing.LocationName },
            paired_at = pairing.PairedAtUtc,
            agent_name = pairing.AgentName,
            last_seen_by_server = seen,
            last_heartbeat_attempt = attempt,
            last_heartbeat_error = heartbeatError,
            poller_state = _poller.State.ToString().ToLowerInvariant(),
            poller_running = _poller.IsRunning,
            config_version = _configState.ConfigVersion,
            pending_local = pending,
            jobs_done = done,
            jobs_error = errors,
            last_job = lastJob is null
                ? null
                : new
                {
                    id = lastJob.JobId,
                    role = lastJob.PrinterRole,
                    state = lastJob.State.ToString().ToLowerInvariant(),
                    attempts = lastJob.Attempts,
                    duplicate_suspected = lastJob.DuplicateSuspected,
                    error = lastJob.ErrorMessage,
                    created_at = lastJob.CreatedAt,
                    updated_at = lastJob.UpdatedAt,
                    printed_at = lastJob.PrintedAt,
                },
            last_print_error = lastError?.ErrorMessage,
            destinations = _store.ListDestinations().Select(ToWire).ToArray(),
            printers = installed.Select(p => p.Name).ToArray(),
            no_printers_note = installed.Count == 0 ? "No hay impresoras instaladas en este equipo (Doc/03 §6.6)." : null,
            // F3-02: estado de la licencia (sin secretos — la licencia no contiene ninguno).
            // En pruebas que cablean el handler sin LicenseState la sección simplemente no aparece.
            license = _license?.ToWire(),
        });
    }

    // ------------------------------------------------------------------------------- test

    private async Task<ManagementResponse> TestAsync(ManagementRequest request, CancellationToken cancellationToken)
    {
        var args = BindArgs<TestArgs>(request);
        var result = await _testPrint.PrintAsync(args.Role, cancellationToken).ConfigureAwait(false);
        return ManagementResponse.Success(request.Id, new
        {
            destination = result.Destination,
            copies = result.Copies,
            elapsed_ms = result.ElapsedMs,
            bytes_printed = result.BytesPrinted,
        });
    }

    // ------------------------------------------------------------------------------ doctor

    private ManagementResponse Doctor(ManagementRequest request)
    {
        var args = BindArgs<DoctorArgs>(request);
        var pairing = _store.GetPairing();
        var dataDirectory = _options.ResolveDataDirectory();
        var logDirectory = Path.Combine(dataDirectory, "logs");
        var logFiles = Directory.Exists(logDirectory) ? Directory.GetFiles(logDirectory, "*.log").Length : 0;
        var installed = _printers.List();

        // F2-12: el ZIP lo genera el SERVICIO y por la pipe solo viaja la RUTA (nunca binarios).
        string? zipPath = null;
        long? zipBytes = null;
        if (args.Zip)
        {
            if (_diagnostics is null)
            {
                return ManagementResponse.Failure(
                    request.Id,
                    "El generador del ZIP de diagnóstico no está disponible en este servicio.",
                    "diagnostics_unavailable");
            }

            try
            {
                zipPath = _diagnostics.CreateZip(DateTimeOffset.UtcNow);
                zipBytes = new FileInfo(zipPath).Length;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo generar el ZIP de diagnóstico.");
                return ManagementResponse.Failure(
                    request.Id,
                    $"No se pudo generar el ZIP de diagnóstico: {ex.Message}",
                    "diagnostics_failed");
            }
        }

        return ManagementResponse.Success(request.Id, new
        {
            // Resumen básico (F2-08) + ZIP opcional (F2-12, S-05: sin secretos en el informe).
            version = AgentVersion.Current,
            platform = "windows",
            net_version = Environment.Version,
            os = Environment.OSVersion.VersionString,
            uptime_started = _runtime.StartedUtc,
            data_directory = dataDirectory,
            database_path = _outbox.DatabasePath,
            log_directory = logDirectory,
            log_files = logFiles,
            paired = pairing.IsPaired && _connection.Transport is not null,
            server_url = pairing.ServerUrl,
            location = new { id = pairing.LocationId, code = pairing.LocationCode, name = pairing.LocationName },
            poller_state = _poller.State.ToString().ToLowerInvariant(),
            pending_local = _outbox.CountByState(OutboxJobState.Received) + _outbox.CountByState(OutboxJobState.Printing),
            jobs_done = _outbox.CountByState(OutboxJobState.Done),
            jobs_error = _outbox.CountByState(OutboxJobState.Error),
            printers = installed.Select(p => new { name = p.Name, @default = p.IsDefault, hint = p.StatusHint }).ToArray(),
            no_printers_note = installed.Count == 0 ? "No hay impresoras instaladas en este equipo (Doc/03 §6.6)." : null,
            license = _license?.ToWire(),
            zip_path = zipPath,
            zip_bytes = zipBytes,
        });
    }

    // ------------------------------------------------------------------------ queue (GUI-02)

    /// <summary>
    /// Cola local para la GUI de bandeja (GUI-02, Doc/08 §3): lectura PURA del outbox vía
    /// <see cref="OutboxRepository.ListLatest"/> — sin efectos, sin estados del servidor
    /// (eso es territorio del panel web). SIN document_json: el detalle del documento viaja
    /// solo en <see cref="QueueItem"/>. El documento completo no sale por la pipe.
    /// </summary>
    private ManagementResponse QueueList(ManagementRequest request)
    {
        var args = BindArgs<QueueListArgs>(request);
        var limit = Math.Clamp(args.Limit ?? 50, 1, 200);
        var jobs = _outbox.ListLatest(limit);

        return ManagementResponse.Success(request.Id, new
        {
            limit,
            total_jobs = _outbox.CountByState(OutboxJobState.Received)
                + _outbox.CountByState(OutboxJobState.Printing)
                + _outbox.CountByState(OutboxJobState.Done)
                + _outbox.CountByState(OutboxJobState.Error),
            jobs = jobs.Select(ToQueueWire).ToArray(),
        });
    }

    /// <summary>Detalle de UN trabajo de la cola: campos + documento legible (GUI-02/04).</summary>
    private ManagementResponse QueueItem(ManagementRequest request)
    {
        var args = BindArgs<QueueItemArgs>(request);
        if (args.JobId is not { } jobId || jobId <= 0)
        {
            return ManagementResponse.Failure(request.Id, "Falta el job_id del trabajo (queue.item {job_id}).", "invalid_argument");
        }

        var job = _outbox.Get(jobId);
        if (job is null)
        {
            return ManagementResponse.Failure(request.Id, $"El trabajo {jobId} no está en la cola local (o ya se purgó).", "job_not_found");
        }

        var document = job.TryParseDocument();
        return ManagementResponse.Success(request.Id, new
        {
            job = ToQueueWire(job),
            document_parse_ok = job.DocumentJson == "{}" || document is not null,
            // Texto legible del documento neutro (Core, portable). NUNCA el JSON crudo con
            // todo su volumen: la GUI muestra texto, el ZIP de diagnóstico lleva el detalle.
            document_text = DocumentText.Render(document),
        });
    }

    private static object ToQueueWire(OutboxJob job) => new
    {
        job_id = job.JobId,
        order_id = job.TryParseDocument()?.Meta?.OrderId,
        printer_role = job.PrinterRole,
        copies = job.Copies,
        state = job.State.ToString().ToLowerInvariant(),
        duplicate_suspected = job.DuplicateSuspected,
        attempts = job.Attempts,
        error_code = job.ErrorCode,
        error_message = job.ErrorMessage,
        created_at = job.CreatedAt,
        updated_at = job.UpdatedAt,
        printed_at = job.PrintedAt,
    };

    // ---------------------------------------------------------------------- restart-poller

    private async Task<ManagementResponse> RestartPollerAsync(ManagementRequest request, CancellationToken cancellationToken)
    {
        if (_connection.Transport is null)
        {
            return ManagementResponse.Failure(request.Id, "El agente no está emparejado: usa orvitaprint pair primero.", "not_paired");
        }

        await _poller.RestartAsync(cancellationToken).ConfigureAwait(false);
        return ManagementResponse.Success(request.Id, new { poller_state = _poller.State.ToString().ToLowerInvariant() });
    }

    // ---------------------------------------------------------------------------- helpers

    private static T BindArgs<T>(ManagementRequest request) where T : class, new()
    {
        if (request.Args is not { ValueKind: JsonValueKind.Object } args)
        {
            return new T();
        }

        try
        {
            return args.Deserialize<T>(ManagementProtocol.Options) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    private static object ToWire(DestinationOptions d) => new
    {
        role = d.Role,
        label = d.Label,
        printer_name = d.PrinterName,
        copies = d.Copies,
        enabled = d.Enabled,
        kind = LocalConfigStore.ToKindName(d.Kind),
        address = d.Address,
    };
}
