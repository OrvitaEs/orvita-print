using Orvita.Print.Contracts;

namespace Orvita.Print.Service;

/// <summary>
/// Extensión local del Service: código del contrato (Doc/04 §6) a su forma de cable para el
/// ack. El Core solo lo parsea (<see cref="ErrorCodes.TryFromWire"/>); el agente necesita
/// escribirlo en <c>JobResult.ErrorCode</c>.
/// </summary>
public static class ErrorCodeWire
{
    public static string ToWireCode(this ErrorCode code) => code switch
    {
        ErrorCode.OrpInvalidToken => "orp_invalid_token",
        ErrorCode.OrpTokenRevoked => "orp_token_revoked",
        ErrorCode.OrpNotFound => "orp_not_found",
        ErrorCode.OrpAlreadyClaimed => "orp_already_claimed",
        ErrorCode.OrpJobExpired => "orp_job_expired",
        ErrorCode.OrpRateLimited => "orp_rate_limited",
        ErrorCode.OrpInvalidPayload => "orp_invalid_payload",
        ErrorCode.OrpPairingInvalid => "orp_pairing_invalid",
        ErrorCode.OrpPairingExpired => "orp_pairing_expired",
        ErrorCode.OrpPairingLocked => "orp_pairing_locked",
        ErrorCode.OrpPrinterNotFound => "orp_printer_not_found",
        ErrorCode.OrpUnsupportedDocument => "orp_unsupported_document",
        _ => "orp_print_failed",
    };
}
