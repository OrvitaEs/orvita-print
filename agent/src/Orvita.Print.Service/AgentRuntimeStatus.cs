namespace Orvita.Print.Service;

/// <summary>
/// Estado de ejecución observable por la pipe de gestión (<c>orvitaprint status</c>,
/// Doc/03 §11): última vez visto por el servidor (heartbeat 200), último error de latido,
/// intervalo de poll entregado por /pair y arranque del proceso. Solo lo escribe el servicio.
/// </summary>
public sealed class AgentRuntimeStatus
{
    private readonly object _lock = new();

    /// <summary>Instante de arranque del proceso (para doctor).</summary>
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>Última respuesta 200 de POST /heartbeat (Doc/03 §11: «última vez visto por el servidor»).</summary>
    public DateTimeOffset? LastSeenByServerUtc { get; private set; }

    /// <summary>Último intento de heartbeat (éxito o no).</summary>
    public DateTimeOffset? LastHeartbeatAttemptUtc { get; private set; }

    /// <summary>Error del último latido fallido; null si el último fue correcto.</summary>
    public string? LastHeartbeatError { get; private set; }

    /// <summary>Intervalo de poll que devolvió POST /pair (Doc/04 §5.2), hasta el primer GET /config.</summary>
    public int? PairPollIntervalSeconds { get; private set; }

    public void MarkHeartbeatSuccess()
    {
        lock (_lock)
        {
            LastSeenByServerUtc = DateTimeOffset.UtcNow;
            LastHeartbeatAttemptUtc = LastSeenByServerUtc;
            LastHeartbeatError = null;
        }
    }

    public void RecordHeartbeatError(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (_lock)
        {
            LastHeartbeatAttemptUtc = DateTimeOffset.UtcNow;
            LastHeartbeatError = message;
        }
    }

    public void SetPairPollInterval(int? seconds)
    {
        lock (_lock)
        {
            PairPollIntervalSeconds = seconds is > 0 ? seconds : null;
        }
    }

    public (DateTimeOffset? Seen, DateTimeOffset? Attempt, string? Error) Snapshot()
    {
        lock (_lock)
        {
            return (LastSeenByServerUtc, LastHeartbeatAttemptUtc, LastHeartbeatError);
        }
    }
}
