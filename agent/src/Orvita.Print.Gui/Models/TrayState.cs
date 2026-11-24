using System.Text.Json;

namespace Orvita.Print.Gui.Models;

/// <summary>
/// Estado del icono de bandeja (Doc/08 §4.1): los cinco estados del agente vistos SIN abrir
/// la ventana. La lógica de mapeo es PURA (testeable, GUI-03): la UI solo dibuja el color.
/// </summary>
public enum TrayState
{
    /// <summary>Emparejado y poller corriendo — verde.</summary>
    Running,

    /// <summary>Emparejado pero servidor sin latido reciente (o poller parado) — ámbar.</summary>
    NoConnection,

    /// <summary>Incidencia de impresión reciente sin ver — rojo.</summary>
    Incident,

    /// <summary>Sin emparejar — gris.</summary>
    Unpaired,

    /// <summary>El servicio no responde (pipe cerrada) — icono apagado/gris tenue.</summary>
    ServiceDown,
}

/// <summary>Entradas del mapeo bandeja (todo lo que la GUI sabe por <c>status</c> y sus intentos de pipe).</summary>
public sealed record TrayInputs
{
    public required bool ServiceReachable { get; init; }

    public required bool Paired { get; init; }

    public required bool PollerRunning { get; init; }

    /// <summary>Última vez visto por el servidor (heartbeat 200); null si nunca.</summary>
    public DateTimeOffset? LastSeenByServer { get; init; }

    /// <summary>Error del último latido fallido; null si el último fue correcto.</summary>
    public string? LastHeartbeatError { get; init; }

    /// <summary>Incidencia de impresión RECIENTE (trabajo en error o reimpresión tras corte, Doc/08 §4.1).</summary>
    public bool RecentIncident { get; init; }

    /// <summary>Ahora (inyectable para pruebas).</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Umbral de latido caducado (Doc/03 §11: avisos > 3 min). Por defecto 3 min.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Ventana de «reciente» para las incidencias (por defecto 24 h: la incidencia manda rojo hasta revisarse en la cola).</summary>
    public TimeSpan IncidentLookback { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>Estado de bandeja + tooltip EXACTO de Doc/08 §4.1, derivado solo del payload de status.</summary>
public static class TrayStateMapper
{
    public static (TrayState State, string Tooltip) Map(TrayInputs inputs) => inputs switch
    {
        { ServiceReachable: false } => (
            TrayState.ServiceDown,
            "El servicio Orvita Print no está en ejecución"),
        { Paired: false } => (
            TrayState.Unpaired,
            "Sin emparejar: ejecuta el emparejamiento"),
        { RecentIncident: true } => (
            TrayState.Incident,
            "Incidencia de impresión; abre para ver detalles"),
        { PollerRunning: false } => (
            TrayState.NoConnection,
            "Sin conexión con la tienda; reintentando"),
        _ => IsHeartbeatStale(inputs)
            ? (TrayState.NoConnection, "Sin conexión con la tienda; reintentando")
            : (TrayState.Running, "Orvita Print · imprimiendo normalmente"),
    };

    private static bool IsHeartbeatStale(TrayInputs inputs)
    {
        if (inputs.LastSeenByServer is not { } seen)
        {
            // Emparejado pero aún sin primer latido confirmado: ámbar hasta el primer 200.
            return true;
        }

        return inputs.Now - seen > inputs.StaleAfter;
    }
}

/// <summary>Deriva <see cref="TrayInputs.RecentIncident"/> del payload de <c>status</c> (puro, testeable).</summary>
public static class TrayInputsBuilder
{
    public static bool HasRecentIncident(StatusSnapshot? status, DateTimeOffset now, TimeSpan lookback)
    {
        var job = status?.LastJob;
        if (job is null || !(job.State == "error" || job.DuplicateSuspected))
        {
            return false;
        }

        if (job.UpdatedAt is not { } updated)
        {
            return true; // sin marca de tiempo, no se puede afirmar que sea vieja: mejor avisar
        }

        return now - updated <= lookback;
    }
}

/// <summary>Colores del icono (hex ARGB sin recursos externos: los iconos se generan en código).</summary>
public static class TrayColors
{
    public const string Running = "#2E7D32";      // verde
    public const string NoConnection = "#F9A825"; // ámbar
    public const string Incident = "#C62828";     // rojo
    public const string Unpaired = "#9E9E9E";     // gris
    public const string ServiceDown = "#BDBDBD";  // gris tenue (apagado)
}
