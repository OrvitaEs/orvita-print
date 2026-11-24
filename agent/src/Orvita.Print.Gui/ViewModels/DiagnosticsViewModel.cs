using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Orvita.Print.Core;
using Orvita.Print.Gui.Management;
using Orvita.Print.Gui.Models;

namespace Orvita.Print.Gui.ViewModels;

/// <summary>Resumen del doctor parseado (GUI-06) — modelo puro, formateable y testeable.</summary>
public sealed class DoctorSummary
{
    public string? Version { get; init; }

    public string? NetVersion { get; init; }

    public string? Os { get; init; }

    public bool Paired { get; init; }

    public string? LocationName { get; init; }

    public string? PollerState { get; init; }

    public int PendingLocal { get; init; }

    public int JobsDone { get; init; }

    public int JobsError { get; init; }

    public int PrinterCount { get; init; }

    public string? DataDirectory { get; init; }

    public string? LogDirectory { get; init; }

    public int LogFiles { get; init; }

    public string? LicenseStatus { get; init; }

    public string? ZipPath { get; init; }

    public long? ZipBytes { get; init; }

    public static DoctorSummary FromJson(JsonElement el)
    {
        var license = el.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.Object
            ? StatusSnapshot.GetString(lic, "status")
            : null;
        return new DoctorSummary
        {
            Version = StatusSnapshot.GetString(el, "version"),
            NetVersion = StatusSnapshot.GetString(el, "net_version"),
            Os = StatusSnapshot.GetString(el, "os"),
            Paired = el.TryGetProperty("paired", out var p) && p.ValueKind == JsonValueKind.True,
            LocationName = el.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object
                ? StatusSnapshot.GetString(loc, "name")
                : null,
            PollerState = StatusSnapshot.GetString(el, "poller_state"),
            PendingLocal = StatusSnapshot.GetInt32(el, "pending_local"),
            JobsDone = StatusSnapshot.GetInt32(el, "jobs_done"),
            JobsError = StatusSnapshot.GetInt32(el, "jobs_error"),
            PrinterCount = el.TryGetProperty("printers", out var printers) && printers.ValueKind == JsonValueKind.Array
                ? printers.GetArrayLength()
                : 0,
            DataDirectory = StatusSnapshot.GetString(el, "data_directory"),
            LogDirectory = StatusSnapshot.GetString(el, "log_directory"),
            LogFiles = StatusSnapshot.GetInt32(el, "log_files"),
            LicenseStatus = license,
            ZipPath = StatusSnapshot.GetString(el, "zip_path"),
            ZipBytes = el.TryGetProperty("zip_bytes", out var zb) && zb.TryGetInt64(out var bytes) ? bytes : null,
        };
    }

    /// <summary>Texto del resumen (§4.5: versiones, licencia, poller, cola).</summary>
    public string Format() =>
        string.Join(Environment.NewLine,
        [
            $"Versión del agente: {Version ?? "—"}",
            $"Red .NET: {NetVersion ?? "—"}",
            $"Sistema: {Os ?? "—"}",
            $"Emparejado: {(Paired ? $"sí ({LocationName ?? "local sin nombre"})" : "NO — pendiente de emparejar")}",
            $"Poller: {PollerState ?? "—"}",
            $"Cola local: {PendingLocal} pendientes · {JobsDone} impresas · {JobsError} con error",
            $"Impresoras instaladas: {PrinterCount}",
            $"Licencia: {LicenseStatus ?? "(sin información)"}",
            $"Datos: {DataDirectory ?? "—"}",
            $"Registros: {LogDirectory ?? "—"} ({LogFiles} ficheros)",
        ]);
}

/// <summary>
/// Pestaña «Diagnóstico» (GUI-06, Doc/08 §4.5): resumen del doctor, generación del ZIP (la
/// ruta la devuelve la pipe — Doc/03: el servicio genera el ZIP y solo viaja la ruta) y
/// visor de las últimas líneas del log.
///
/// Decisión documentada (GUI-06): la ruta del log NO se deduce de appsettings — la pide por
/// la pipe (doctor devuelve <c>log_directory</c>) y la GUI lee el fichero más reciente como
/// vista de solo lectura. El token y la BD NUNCA se leen ni se muestran (S-05: el informe
/// del servicio ya los excluye).
/// </summary>
public partial class DiagnosticsViewModel : ObservableObject
{
    /// <summary>Líneas del log mostradas (cola en memoria: suficiente para ver el problema).</summary>
    public const int LogTailLines = 200;

    private readonly IManagementClient _client;

    [ObservableProperty]
    private string _summaryText = "Pulsa «Actualizar» para pedir el diagnóstico al servicio.";

    [ObservableProperty]
    private string? _zipPath;

    [ObservableProperty]
    private string? _logText;

    [ObservableProperty]
    private string? _logPath;

    [ObservableProperty]
    private string? _error;

    /// <summary>Abrir carpeta del ZIP inyectado por la vista (Process.Start explorer).</summary>
    public Action<string>? OpenFolder { get; set; }

    public DiagnosticsViewModel(IManagementClient client)
    {
        _client = client;
    }

    /// <summary>Pide el resumen del doctor (sin ZIP). No lanza.</summary>
    public void LoadSummary()
    {
        ManagementResponse response;
        try
        {
            response = _client.Send(ManagementProtocol.Operations.Doctor);
        }
        catch (Exception ex) when (MainViewModel.IsPipeFault(ex))
        {
            Error = "El servicio Orvita Print no está en ejecución: no hay diagnóstico que mostrar.";
            return;
        }

        if (!response.Ok || response.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            Error = response.Error is { } error ? $"El diagnóstico falló: {error}" : "El diagnóstico falló.";
            return;
        }

        Error = null;
        SummaryText = DoctorSummary.FromJson(data).Format();
        if (StatusSnapshot.GetString(data, "log_directory") is { Length: > 0 } logDirectory)
        {
            LogPath = Path.Combine(logDirectory, "*.log");
        }
    }

    /// <summary>Genera el ZIP (doctor zip:true) y muestra la ruta devuelta por el servicio.</summary>
    public void CreateZip()
    {
        ManagementResponse response;
        try
        {
            response = _client.Send(ManagementProtocol.Operations.Doctor, new { zip = true });
        }
        catch (Exception ex) when (MainViewModel.IsPipeFault(ex))
        {
            Error = "El servicio Orvita Print no está en ejecución.";
            return;
        }

        if (!response.Ok || response.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            Error = response.Error is { } error ? $"No se pudo generar el ZIP: {error}" : "No se pudo generar el ZIP.";
            return;
        }

        Error = null;
        ZipPath = StatusSnapshot.GetString(data, "zip_path");
    }

    /// <summary>Lee las últimas líneas del log más reciente (solo lectura; nunca token ni BD).</summary>
    public void LoadLog()
    {
        var directory = LogPath is { Length: > 0 } ? Path.GetDirectoryName(LogPath) : null;
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            LogText = "(sin registros aún: el servicio aún no ha escrito ninguno)";
            return;
        }

        var newest = Directory.GetFiles(directory, "*.log")
            .Select(f => new FileInfo(f))
            // Rotación diaria con fecha EN EL NOMBRE: el orden por nombre es determinista
            // (dos escrituras en el mismo tick comparten LastWriteTime).
            .OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (newest is null)
        {
            LogText = "(sin registros aún)";
            return;
        }

        try
        {
            LogPath = newest.FullName;
            LogText = string.Join(Environment.NewLine, ReadTail(newest.FullName, LogTailLines));
        }
        catch (IOException)
        {
            LogText = "(el fichero de registro está ocupado por el servicio; reintenta)";
        }
    }

    /// <summary>Cola del fichero (lee el final, tolera el fichero en uso por el servicio).</summary>
    public static string[] ReadTail(string path, int lines)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var queue = new Queue<string>(lines);
        while (reader.ReadLine() is { } line)
        {
            if (queue.Count == lines)
            {
                queue.Dequeue();
            }

            queue.Enqueue(line);
        }

        return [.. queue];
    }
}
