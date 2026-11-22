using System.Net;
using Orvita.Print.Contracts;

namespace Orvita.Print.Core;

/// <summary>
/// Base de los errores de transporte. Permite al poller decidir entre detenerse (401),
/// hacer backoff (429, red) o continuar (409/410/404), según Doc/03 §6.3.
/// </summary>
public abstract class TransportException : Exception
{
    protected TransportException(ErrorCode errorCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Código de contrato resuelto del cuerpo; Unknown si no se reconoció.</summary>
    public ErrorCode ErrorCode { get; }

    /// <summary>HTTP de la respuesta; null en errores de red (no hubo respuesta).</summary>
    public HttpStatusCode? StatusCode { get; protected init; }
}

/// <summary>401 con orp_invalid_token/orp_token_revoked: detener el poller y avisar (estado visible).</summary>
public sealed class TransportUnauthorizedException : TransportException
{
    public TransportUnauthorizedException(ErrorCode errorCode, string message, Exception? inner = null)
        : base(errorCode, message, inner)
    {
        StatusCode = HttpStatusCode.Unauthorized;
    }
}

/// <summary>429 orp_rate_limited: backoff respetando Retry-After si el servidor lo envió.</summary>
public sealed class TransportRateLimitedException : TransportException
{
    public TransportRateLimitedException(ErrorCode errorCode, string message, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(errorCode, message, inner)
    {
        StatusCode = HttpStatusCode.TooManyRequests;
        RetryAfter = retryAfter;
    }

    /// <summary>Espera solicitada por el servidor vía Retry-After; null si no vino la cabecera.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Error de red o de timeout: sin respuesta del servidor. El poller hace backoff exponencial.</summary>
public sealed class TransportNetworkException : TransportException
{
    public TransportNetworkException(string message, Exception? inner = null)
        : base(ErrorCode.Unknown, message, inner)
    {
    }
}

/// <summary>Respuesta inesperada del servidor que no es 401/429 ni caso manejado (p. ej. 500, 404 en /jobs).</summary>
public sealed class TransportProtocolException : TransportException
{
    public TransportProtocolException(HttpStatusCode statusCode, ErrorCode errorCode, string message, Exception? inner = null)
        : base(errorCode, message, inner)
    {
        StatusCode = statusCode;
    }
}
