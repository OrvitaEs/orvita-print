using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orvita.Print.Contracts;

/// <summary>Códigos de error del contrato (Doc/04 §6) más los códigos que el propio agente reporta en el ack.</summary>
public enum ErrorCode
{
    [JsonStringEnumMemberName("orp_invalid_token")]
    OrpInvalidToken,

    [JsonStringEnumMemberName("orp_token_revoked")]
    OrpTokenRevoked,

    [JsonStringEnumMemberName("orp_not_found")]
    OrpNotFound,

    [JsonStringEnumMemberName("orp_already_claimed")]
    OrpAlreadyClaimed,

    [JsonStringEnumMemberName("orp_job_expired")]
    OrpJobExpired,

    [JsonStringEnumMemberName("orp_rate_limited")]
    OrpRateLimited,

    [JsonStringEnumMemberName("orp_invalid_payload")]
    OrpInvalidPayload,

    [JsonStringEnumMemberName("orp_pairing_invalid")]
    OrpPairingInvalid,

    [JsonStringEnumMemberName("orp_pairing_expired")]
    OrpPairingExpired,

    [JsonStringEnumMemberName("orp_pairing_locked")]
    OrpPairingLocked,

    /// <summary>Código que reporta el agente en el ack; no es un error HTTP.</summary>
    [JsonStringEnumMemberName("orp_printer_not_found")]
    OrpPrinterNotFound,

    /// <summary>Código que reporta el agente en el ack; no es un error HTTP.</summary>
    [JsonStringEnumMemberName("orp_unsupported_document")]
    OrpUnsupportedDocument,

    /// <summary>El servidor envió un código que no está en el contrato (04 §6).</summary>
    [JsonStringEnumMemberName("orp_unknown_error")]
    Unknown,
}

/// <summary>Acción canónica del agente ante cada código de error (Doc/04 §6, columna «Acción del agente»).</summary>
public enum AgentAction
{
    /// <summary>Detener el poller y avisar (visible en status y en el panel).</summary>
    Stop,

    /// <summary>Otro agente lo cogió: continuar con el siguiente.</summary>
    Continue,

    /// <summary>Descartar el trabajo localmente.</summary>
    Discard,

    /// <summary>Backoff (respetando Retry-After si lo hay).</summary>
    Backoff,

    /// <summary>Pedir de nuevo / repetir la operación.</summary>
    Retry,

    /// <summary>Marcar el trabajo como failed.</summary>
    MarkFailed,
}

/// <summary>Tabla de verdad del §6: cada código con su HTTP esperado y su acción.</summary>
public static class ErrorCodes
{
    public static int HttpStatus(this ErrorCode code) => code switch
    {
        ErrorCode.OrpInvalidToken => 401,
        ErrorCode.OrpTokenRevoked => 401,
        ErrorCode.OrpNotFound => 404,
        ErrorCode.OrpAlreadyClaimed => 409,
        ErrorCode.OrpJobExpired => 410,
        ErrorCode.OrpRateLimited => 429,
        ErrorCode.OrpInvalidPayload => 400,
        ErrorCode.OrpPairingInvalid => 400,
        ErrorCode.OrpPairingExpired => 410,
        ErrorCode.OrpPairingLocked => 429,
        // Códigos de ack del agente y desconocidos: no tienen HTTP asociado.
        _ => 0,
    };

    public static AgentAction Action(this ErrorCode code) => code switch
    {
        ErrorCode.OrpInvalidToken => AgentAction.Stop,
        ErrorCode.OrpTokenRevoked => AgentAction.Stop,
        ErrorCode.OrpNotFound => AgentAction.Discard,
        ErrorCode.OrpAlreadyClaimed => AgentAction.Continue,
        ErrorCode.OrpJobExpired => AgentAction.Discard,
        ErrorCode.OrpRateLimited => AgentAction.Backoff,
        ErrorCode.OrpInvalidPayload => AgentAction.MarkFailed,
        ErrorCode.OrpPairingInvalid => AgentAction.Retry,
        ErrorCode.OrpPairingExpired => AgentAction.Retry,
        ErrorCode.OrpPairingLocked => AgentAction.Backoff,
        ErrorCode.OrpPrinterNotFound => AgentAction.MarkFailed,
        ErrorCode.OrpUnsupportedDocument => AgentAction.MarkFailed,
        ErrorCode.Unknown => AgentAction.Backoff,
        _ => AgentAction.Backoff,
    };

    /// <summary>
    /// Resuelve el código del contrato desde el cuerpo de error, aceptando las dos formas
    /// que usa el servidor: el cuerpo literal { "error": "…" } (409/410, F0-11) y el
    /// envoltorio estándar de WordPress { "code": "…", "message": …, "data": … } (401/429).
    /// </summary>
    public static ErrorCode? TryFromWire(string? wireCode) => wireCode?.Trim() switch
    {
        "orp_invalid_token" => ErrorCode.OrpInvalidToken,
        "orp_token_revoked" => ErrorCode.OrpTokenRevoked,
        "orp_not_found" => ErrorCode.OrpNotFound,
        "orp_already_claimed" => ErrorCode.OrpAlreadyClaimed,
        "orp_job_expired" => ErrorCode.OrpJobExpired,
        "orp_rate_limited" => ErrorCode.OrpRateLimited,
        "orp_invalid_payload" => ErrorCode.OrpInvalidPayload,
        "orp_pairing_invalid" => ErrorCode.OrpPairingInvalid,
        "orp_pairing_expired" => ErrorCode.OrpPairingExpired,
        "orp_pairing_locked" => ErrorCode.OrpPairingLocked,
        "orp_printer_not_found" => ErrorCode.OrpPrinterNotFound,
        "orp_unsupported_document" => ErrorCode.OrpUnsupportedDocument,
        _ => null,
    };
}

/// <summary>Elemento "data" del envoltorio estándar de WordPress.</summary>
public sealed class ErrorData
{
    [JsonPropertyName("status")]
    public int? Status { get; set; }
}

/// <summary>
/// Cuerpo de error del contrato. Conviven dos formas (Doc/04 §5.5 y §6):
///  - literal (409/410):  { "error": "orp_already_claimed", "status": "claimed" }
///  - envoltorio WP (401/429): { "code": "orp_invalid_token", "message": …, "data": { "status": 401 } }
/// </summary>
public sealed class ErrorEnvelope
{
    /// <summary>Código en el cuerpo literal del contrato.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Código en el envoltorio estándar de WordPress.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>Estado del trabajo en el cuerpo literal (p. ej. "claimed").</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("data")]
    public ErrorData? Data { get; set; }

    /// <summary>Primer código de error reconocible, en cualquiera de las dos formas.</summary>
    public string? PrimaryCode => Error ?? Code;

    /// <summary>Parse tolerante: cuerpo vacío o no JSON → null, nunca excepción.</summary>
    public static ErrorEnvelope? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var env = JsonSerializer.Deserialize<ErrorEnvelope>(json, OrpJson.Inner);
            if (env is null) return null;
            // Cuerpo literal sin "error" pero con "code" de WP ya cubierto; si no hay
            // ningún código reconocible devolvemos el envoltorio igualmente para
            // conservar el mensaje legible.
            return env.Error is null && env.Code is null && env.Message is null ? null : env;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
