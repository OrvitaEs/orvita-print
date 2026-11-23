namespace Orvita.Print.Service;

/// <summary>
/// Estado del canal de actualización observable por el diagnóstico (F2-12) y el doctor.
/// Solo lo escribe el propio flujo de actualización; lectura concurrente con candado.
/// </summary>
public sealed class UpdateStatus
{
    private readonly object _lock = new();

    public DateTimeOffset? LastCheckUtc { get; private set; }

    /// <summary>Resultado del último intento: up_to_date · applied · reboot_required · failed · disabled.</summary>
    public string? LastResult { get; private set; }

    /// <summary>Versión anunciada por el manifiesto del último intento (aunque no se aplicara).</summary>
    public string? LastVersionSeen { get; private set; }

    /// <summary>Error del último intento (con su código UpdateException); null si fue correcto.</summary>
    public string? LastError { get; private set; }

    public void RecordCheckStarted() => LastCheckUtc = DateTimeOffset.UtcNow;

    public void RecordResult(string result, string? versionSeen, string? error)
    {
        lock (_lock)
        {
            LastCheckUtc = DateTimeOffset.UtcNow;
            LastResult = result;
            LastVersionSeen = versionSeen;
            LastError = error;
        }
    }

    public void RecordUpToDate(string? versionSeen) => RecordResult("up_to_date", versionSeen, null);

    public void RecordDisabled() => RecordResult("disabled", null, null);

    public void RecordApplied(string versionSeen, string detail) =>
        RecordResult(detail, versionSeen, null);

    public void RecordFailure(string error) => RecordResult("failed", LastVersionSeen, error);

    public (DateTimeOffset? Check, string? Result, string? Version, string? Error) Snapshot()
    {
        lock (_lock)
        {
            return (LastCheckUtc, LastResult, LastVersionSeen, LastError);
        }
    }
}
