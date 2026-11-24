using System.Globalization;
using System.Text.Json;

namespace Orvita.Print.Gui.Models;

/// <summary>Último trabajo visto en <c>status</c> (una línea del payload last_job).</summary>
public sealed class StatusLastJob
{
    public long? Id { get; init; }

    public string? Role { get; init; }

    public string? State { get; init; }

    public int? Attempts { get; init; }

    public bool DuplicateSuspected { get; init; }

    public string? Error { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Instantánea de <c>status</c> (F2-08): lo que la GUI muestra en la barra de estado y de lo
/// que nace el estado del icono de bandeja (GUI-03). El token NO existe aquí (S-05): el
/// servicio nunca lo manda; <c>server_url</c> no es secreta.
/// </summary>
public sealed class StatusSnapshot
{
    public string? Version { get; init; }

    public bool Paired { get; init; }

    public string? ServerUrl { get; init; }

    public string? LocationName { get; init; }

    public string? LocationCode { get; init; }

    public DateTimeOffset? LastSeenByServer { get; init; }

    public DateTimeOffset? LastHeartbeatAttempt { get; init; }

    public string? LastHeartbeatError { get; init; }

    public string? PollerState { get; init; }

    public bool PollerRunning { get; init; }

    public int PendingLocal { get; init; }

    public int JobsDone { get; init; }

    public int JobsError { get; init; }

    public string? LastPrintError { get; init; }

    public StatusLastJob? LastJob { get; init; }

    public string? LicenseStatus { get; init; }

    public static StatusSnapshot FromJson(JsonElement element)
    {
        var location = element.TryGetProperty("location", out var loc) ? loc : default;
        var lastJob = element.TryGetProperty("last_job", out var lj) && lj.ValueKind == JsonValueKind.Object
            ? new StatusLastJob
            {
                Id = lj.TryGetProperty("id", out var ljid) && ljid.TryGetInt64(out var id) ? id : null,
                Role = lj.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String ? role.GetString() : null,
                State = lj.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null,
                Attempts = lj.TryGetProperty("attempts", out var at) && at.TryGetInt32(out var attempts) ? attempts : null,
                DuplicateSuspected = lj.TryGetProperty("duplicate_suspected", out var dup) && dup.ValueKind == JsonValueKind.True,
                Error = lj.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null,
                UpdatedAt = StatusSnapshot.GetTimestamp(lj, "updated_at"),
            }
            : null;

        var license = element.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.Object
            ? lic.TryGetProperty("status", out var lst) && lst.ValueKind == JsonValueKind.String ? lst.GetString() : null
            : null;

        return new StatusSnapshot
        {
            Version = GetString(element, "version"),
            Paired = element.TryGetProperty("paired", out var paired) && paired.ValueKind == JsonValueKind.True,
            ServerUrl = GetString(element, "server_url"),
            LocationName = location.ValueKind == JsonValueKind.Object ? GetString(location, "name") : null,
            LocationCode = location.ValueKind == JsonValueKind.Object ? GetString(location, "code") : null,
            LastSeenByServer = GetTimestamp(element, "last_seen_by_server"),
            LastHeartbeatAttempt = GetTimestamp(element, "last_heartbeat_attempt"),
            LastHeartbeatError = GetString(element, "last_heartbeat_error"),
            PollerState = GetString(element, "poller_state"),
            PollerRunning = element.TryGetProperty("poller_running", out var running) && running.ValueKind == JsonValueKind.True,
            PendingLocal = GetInt32(element, "pending_local"),
            JobsDone = GetInt32(element, "jobs_done"),
            JobsError = GetInt32(element, "jobs_error"),
            LastPrintError = GetString(element, "last_print_error"),
            LastJob = lastJob,
            LicenseStatus = license,
        };
    }

    internal static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    internal static int GetInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value) ? value : 0;

    internal static DateTimeOffset? GetTimestamp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
}
