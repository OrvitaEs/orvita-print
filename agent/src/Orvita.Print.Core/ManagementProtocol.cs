using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orvita.Print.Core;

/// <summary>
/// Protocolo de la pipe de gestión (F2-08, ADR-13): JSON de líneas sobre una named pipe de
/// «duplex» (una solicitud, una respuesta, correlación por <c>id</c> para clientes
/// secuenciales). El nombre de la pipe es estable y documentado: los clientes son el CLI
/// <c>orvitaprint</c> (F2-09) y, en el futuro, la GUI de bandeja (backlog, ADR-13).
///
/// Seguridad (Doc/03 §6.2 y §8): la pipe solo la abre el usuario local configurado y los
/// administradores (PipeAccessRule en el servidor); el token JAMÁS viaja de vuelta por la
/// pipe — <c>status</c> lo oculta y <c>pair</c> responde «ok» sin eco del token (S-05).
/// Este fichero es SOLO el contrato JSON (portable, ADR-12): las primitivas de pipe viven
/// en el Service (servidor) y el Cli (cliente).
/// </summary>
public static class ManagementProtocol
{
    /// <summary>Nombre estable de la pipe de gestión (ruta completa: \\.\pipe\orvitaprint-management).</summary>
    public const string PipeName = "orvitaprint-management";

    /// <summary>Operaciones conocidas (la desconocida devuelve error unknown_operation).</summary>
    public static class Operations
    {
        public const string Pair = "pair";
        public const string DestinationAdd = "destination.add";
        public const string DestinationList = "destination.list";
        public const string DestinationRemove = "destination.remove";
        public const string Status = "status";
        public const string Test = "test";
        public const string Doctor = "doctor";
        public const string RestartPoller = "restart-poller";
        public const string Ping = "ping";
    }

    /// <summary>Serialización del protocolo: snake_case como el contrato REST (una sola casa).</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Los mensajes pueden llevar nombres de impresoras con acentos: sin escapar a \uXXXX.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>Solicitud del cliente: <c>{"id":1,"op":"status","args":{…}}</c>. <c>args</c> es específico de cada operación.</summary>
public sealed class ManagementRequest
{
    public long Id { get; set; } = 1;

    public string? Op { get; set; }

    public JsonElement? Args { get; set; }
}

/// <summary>
/// Respuesta del servidor: <c>{"id":1,"ok":true,"data":{…}}</c> o
/// <c>{"id":1,"ok":false,"error":"…","code":"…"}</c>. El <c>code</c> es el código contractual
/// (orp_pairing_*) o una categoría de gestión (unknown_operation, not_paired, print_error…).
/// </summary>
public sealed class ManagementResponse
{
    public long Id { get; set; }

    public bool Ok { get; set; }

    public JsonElement? Data { get; set; }

    public string? Error { get; set; }

    public string? Code { get; set; }

    public static ManagementResponse Success(long id, object? data) => new()
    {
        Id = id,
        Ok = true,
        Data = data is null ? null : JsonSerializer.SerializeToElement(data, ManagementProtocol.Options),
    };

    public static ManagementResponse Failure(long id, string error, string? code = null) => new()
    {
        Id = id,
        Ok = false,
        Error = error,
        Code = code,
    };
}
