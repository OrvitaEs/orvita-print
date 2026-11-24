using System.Text.Json;

namespace Orvita.Print.Gui.Models;

/// <summary>
/// Fila de la cola local (GUI-04, Doc/08 §4.2). Estado local del OUTBOX (received/printing/
/// done/error) — nunca el estado del servidor (territorio del panel web).
/// </summary>
public sealed class QueueJobRow
{
    public long JobId { get; init; }

    public long? OrderId { get; init; }

    public string? PrinterRole { get; init; }

    public int Copies { get; init; }

    /// <summary>Estado en crudo del outbox (received/printing/done/error).</summary>
    public string State { get; init; } = "received";

    public bool DuplicateSuspected { get; init; }

    public int Attempts { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? PrintedAt { get; init; }

    /// <summary>Etiqueta en español (Doc/08 §4.2).</summary>
    public string StateLabel => QueueStatusMapper.Label(State, DuplicateSuspected);

    /// <summary>Color del badge (hex, misma paleta que Doc/08 §4.2).</summary>
    public string StateColor => QueueStatusMapper.Color(State, DuplicateSuspected);

    /// <summary>Nº de pedido tal cual se busca («por nº de pedido»); vacío si el servidor no lo mandó.</summary>
    public string OrderDisplay => OrderId is { } id ? $"#{id}" : string.Empty;

    public string? LastErrorDisplay => DuplicateSuspected
        ? $"Reimpreso tras corte: {ErrorMessage}"
        : ErrorMessage;

    public static QueueJobRow FromJson(JsonElement job) => new()
    {
        JobId = job.TryGetProperty("job_id", out var id) && id.TryGetInt64(out var jobId) ? jobId : 0,
        OrderId = job.TryGetProperty("order_id", out var order) && order.ValueKind == JsonValueKind.Number && order.TryGetInt64(out var orderId) ? orderId : null,
        PrinterRole = StatusSnapshot.GetString(job, "printer_role"),
        Copies = StatusSnapshot.GetInt32(job, "copies"),
        State = StatusSnapshot.GetString(job, "state") ?? "received",
        DuplicateSuspected = job.TryGetProperty("duplicate_suspected", out var dup) && dup.ValueKind == JsonValueKind.True,
        Attempts = StatusSnapshot.GetInt32(job, "attempts"),
        ErrorCode = StatusSnapshot.GetString(job, "error_code"),
        ErrorMessage = StatusSnapshot.GetString(job, "error_message"),
        CreatedAt = StatusSnapshot.GetTimestamp(job, "created_at") ?? DateTimeOffset.MinValue,
        UpdatedAt = StatusSnapshot.GetTimestamp(job, "updated_at") ?? DateTimeOffset.MinValue,
        PrintedAt = StatusSnapshot.GetTimestamp(job, "printed_at"),
    };
}

/// <summary>Etiqueta y color de los cinco estados locales (Doc/08 §4.2) — puro y testeable.</summary>
public static class QueueStatusMapper
{
    public static string Label(string state, bool duplicate) => duplicate
        ? "Reimpreso tras corte"
        : state switch
        {
            "received" => "Pendiente",
            "printing" => "Imprimiéndose",
            "done" => "Impreso",
            "error" => "Error",
            _ => state,
        };

    public static string Color(string state, bool duplicate) => duplicate
        ? "#F9A825" // ámbar: badge sobre el estado
        : state switch
        {
            "received" => "#9E9E9E",    // gris
            "printing" => "#1565C0",    // azul (spinner en la UI)
            "done" => "#2E7D32",        // verde
            "error" => "#C62828",       // rojo
            _ => "#9E9E9E",
        };
}

/// <summary>
/// Filtros de la cola (GUI-04): por estado y búsqueda por nº de pedido. Puros: la cola llega
/// recortada a los últimos 50 y se filtra en la GUI (volumen ínfimo, Doc/08 §2).
/// </summary>
public static class QueueFilter
{
    /// <summary>Valor especial del combo «Todos».</summary>
    public const string AllStates = "todos";

    /// <summary>Filtro especial de reimpresos tras corte (badge, no un estado).</summary>
    public const string DuplicateState = "duplicate";

    public static IEnumerable<QueueJobRow> Apply(IEnumerable<QueueJobRow> rows, string? stateFilter, string? search)
    {
        var normalizedSearch = NormalizeSearch(search);
        return rows.Where(row => MatchesState(row, stateFilter) && MatchesSearch(row, normalizedSearch));
    }

    private static bool MatchesState(QueueJobRow row, string? stateFilter) =>
        stateFilter is null || stateFilter == AllStates
        || (stateFilter == DuplicateState
            ? row.DuplicateSuspected
            : !row.DuplicateSuspected && string.Equals(row.State, stateFilter, StringComparison.Ordinal));

    private static bool MatchesSearch(QueueJobRow row, string search)
    {
        if (search.Length == 0)
        {
            return true;
        }

        if (row.OrderId is { } orderId && orderId.ToString().Contains(search, StringComparison.Ordinal))
        {
            return true;
        }

        if (row.JobId.ToString().Contains(search, StringComparison.Ordinal))
        {
            return true;
        }

        return row.PrinterRole?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false;
    }

    /// <summary>La búsqueda busca nº de pedido (y acepta con o sin #, o job id, o role).</summary>
    public static string NormalizeSearch(string? search) =>
        search?.Trim().TrimStart('#') ?? string.Empty;
}
