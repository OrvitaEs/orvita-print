using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Generador del ZIP de diagnóstico (<c>orvitaprint doctor --zip</c>, F2-12, Doc/03 §11):
/// <c>%ProgramData%\OrvitaPrint\diagnostics\orvitaprint-&lt;timestamp&gt;.zip</c> con:
/// - <c>logs/</c>: los registros de los ÚLTIMOS 7 DÍAS (rotación diaria ya en disco);
/// - <c>diagnostics.json</c>: informe — versión del agente y del runtime, OS, estado de
///   emparejamiento (ServerUrl CON el token OCULTO), estado del poller, últimas 50 entradas
///   de la cola con estados, destinos configurados, últimos errores con códigos y versión de config.
/// NADA MÁS (S-05): ni el token, ni el blob DPAPI, ni datos personales — solo order_id/job_id.
/// La ruta del ZIP viaja por la pipe (los binarios nunca viajan por ella).
/// </summary>
public sealed class DiagnosticsBundleBuilder
{
    /// <summary>Ventana de log que se exporta (Doc/03 §11: 7 días).</summary>
    public static readonly TimeSpan LogWindow = TimeSpan.FromDays(7);

    /// <summary>Últimas entradas de cola que incluye el informe (Doc/07 F2-12: 50).</summary>
    public const int LatestJobsCount = 50;

    private readonly AgentOptions _options;
    private readonly LocalConfigStore _store;
    private readonly OutboxRepository _outbox;
    private readonly IPrinterEnumerator _printers;
    private readonly PollerController _poller;
    private readonly RemoteConfigState _configState;
    private readonly ServerConnection _connection;
    private readonly AgentRuntimeStatus _runtime;
    private readonly UpdateStatus _updates;
    private readonly ILogger _logger;

    public DiagnosticsBundleBuilder(
        AgentOptions options,
        LocalConfigStore store,
        OutboxRepository outbox,
        IPrinterEnumerator printers,
        PollerController poller,
        RemoteConfigState configState,
        ServerConnection connection,
        AgentRuntimeStatus runtime,
        UpdateStatus updates,
        ILogger? logger = null)
    {
        _options = options;
        _store = store;
        _outbox = outbox;
        _printers = printers;
        _poller = poller;
        _configState = configState;
        _connection = connection;
        _runtime = runtime;
        _updates = updates;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>Genera el ZIP y devuelve su ruta completa. Lanza ante fallos de disco (lo traduce la pipe).</summary>
    public string CreateZip(DateTimeOffset nowUtc)
    {
        var directory = Path.Combine(_options.ResolveDataDirectory(), "diagnostics");
        Directory.CreateDirectory(directory);
        var zipPath = UniqueZipPath(directory, nowUtc);

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddLogs(archive, nowUtc);
            AddDiagnosticsJson(archive, nowUtc);
        }

        _logger.LogInformation("ZIP de diagnóstico generado: {Zip}.", zipPath);
        return zipPath;
    }

    private static string UniqueZipPath(string directory, DateTimeOffset nowUtc)
    {
        var stem = $"orvitaprint-{nowUtc.ToLocalTime():yyyyMMdd-HHmmss}";
        var candidate = Path.Combine(directory, $"{stem}.zip");
        var attempt = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{stem}_{++attempt}.zip");
        }

        return candidate;
    }

    // ----------------------------------------------------------------------------------- logs

    private void AddLogs(ZipArchive archive, DateTimeOffset nowUtc)
    {
        var logDirectory = Path.Combine(_options.ResolveDataDirectory(), "logs");
        if (!Directory.Exists(logDirectory))
        {
            return;
        }

        var cutoff = nowUtc - LogWindow;
        foreach (var file in Directory.GetFiles(logDirectory, "*.log").Order())
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                continue; // fuera de la ventana de 7 días (Doc/03 §11)
            }

            try
            {
                // FileShare.ReadWrite: Serilog mantiene el fichero del día abierto.
                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var entry = archive.CreateEntry($"logs/{Path.GetFileName(file)}", CompressionLevel.Optimal);
                using var target = entry.Open();
                source.CopyTo(target);
            }
            catch (IOException ex)
            {
                _logger.LogWarning("El log «{File}» no se pudo copiar al ZIP de diagnóstico: {Message}.", file, ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------------ diagnostics.json

    private void AddDiagnosticsJson(ZipArchive archive, DateTimeOffset nowUtc)
    {
        var entry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, BuildReport(nowUtc), ManagementProtocol.Options);
    }

    /// <summary>
    /// Informe del diagnóstico. S-05 AUDITADO: esta forma nunca contiene el token ni el blob
    /// cifrado — del <see cref="PairingInfo"/> solo salen URL, local y metadatos.
    /// </summary>
    internal object BuildReport(DateTimeOffset nowUtc)
    {
        var pairing = _store.GetPairing();
        var (seen, heartbeatAttempt, heartbeatError) = _runtime.Snapshot();
        var (updateCheck, updateResult, updateVersion, updateError) = _updates.Snapshot();
        var installed = _printers.List();
        var jobs = _outbox.ListLatest(LatestJobsCount);

        return new
        {
            generated_at = nowUtc,
            agent = new
            {
                version = AgentVersion.Current,
                net_version = Environment.Version.ToString(),
                os = Environment.OSVersion.VersionString,
                platform = "windows",
                uptime_started = _runtime.StartedUtc,
                install_directory = AppContext.BaseDirectory,
                data_directory = _options.ResolveDataDirectory(),
            },
            // S-05: server_url sin token (la URL no lo contiene) y NINGÚN campo de token.
            pairing = new
            {
                paired = pairing.IsPaired,
                server_url = pairing.ServerUrl,
                location = new { id = pairing.LocationId, code = pairing.LocationCode, name = pairing.LocationName },
                agent_name = pairing.AgentName,
                paired_at = pairing.PairedAtUtc,
                connected = _connection.Transport is not null,
            },
            server = new
            {
                last_seen_by_server = seen,
                last_heartbeat_attempt = heartbeatAttempt,
                last_heartbeat_error = heartbeatError,
            },
            poller = new
            {
                state = _poller.State.ToString().ToLowerInvariant(),
                running = _poller.IsRunning,
            },
            config = new
            {
                config_version = _configState.ConfigVersion,
                poll_interval_seconds = _configState.PollIntervalSeconds,
            },
            updates = new
            {
                last_check = updateCheck,
                last_result = updateResult,
                last_version_seen = updateVersion,
                last_error = updateError,
            },
            queue = new
            {
                received = _outbox.CountByState(OutboxJobState.Received),
                printing = _outbox.CountByState(OutboxJobState.Printing),
                done = _outbox.CountByState(OutboxJobState.Done),
                error = _outbox.CountByState(OutboxJobState.Error),
                latest = jobs.Select(ToJobWire).ToArray(),
            },
            // Últimos errores con código (los de estado error, más recientes primero).
            last_errors = jobs
                .Where(j => j.State == OutboxJobState.Error)
                .Take(10)
                .Select(j => new
                {
                    job_id = j.JobId,
                    error_code = j.ErrorCode,
                    error_message = j.ErrorMessage,
                    updated_at = j.UpdatedAt,
                })
                .ToArray(),
            destinations = _store.ListDestinations().Select(d => new
            {
                role = d.Role,
                label = d.Label,
                printer_name = d.PrinterName,
                copies = d.Copies,
                enabled = d.Enabled,
                kind = LocalConfigStore.ToKindName(d.Kind),
                address = d.Address,
            }).ToArray(),
            printers = installed.Select(p => new
            {
                name = p.Name,
                is_default = p.IsDefault,
                hint = p.StatusHint,
            }).ToArray(),
        };
    }

    /// <summary>Campo a campo: el documento (DocumentJson) JAMÁS se exporta (datos del cliente).</summary>
    private static object ToJobWire(OutboxJob job) => new
    {
        job_id = job.JobId,
        role = job.PrinterRole,
        copies = job.Copies,
        state = job.State.ToString().ToLowerInvariant(),
        attempts = job.Attempts,
        duplicate_suspected = job.DuplicateSuspected,
        error_code = job.ErrorCode,
        error_message = job.ErrorMessage,
        created_at = job.CreatedAt,
        updated_at = job.UpdatedAt,
        printed_at = job.PrintedAt,
    };
}
