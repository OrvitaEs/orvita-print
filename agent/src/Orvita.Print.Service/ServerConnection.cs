using Microsoft.Extensions.Logging;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Conexión con el servidor, MUTABLE (F2-08/F2-10): nace de la configuración bootstrap
/// (modo desarrollo por argumentos) o vacía, y la pipe de gestión la rellena al emparejar
/// (<c>orvitaprint pair</c>) SIN reiniciar el proceso. El transporte sustituido se libera.
/// El token vive en memoria clara SOLO dentro del <see cref="DirectTransport"/> (S-05: nunca
/// a disco plano ni a log; en reposo va cifrado con DPAPI en el almacén F2-10, S-01).
/// </summary>
public sealed class ServerConnection : IDisposable
{
    private readonly ILogger<ServerConnection> _logger;
    private readonly object _gate = new();
    private DirectTransport? _transport;

    /// <summary>Señal de desarrollo: HTTP sin TLS permitido (S-03, solo wp-env/pruebas).</summary>
    public bool AllowInsecureHttp { get; }

    public ServerConnection(AgentOptions options, ILogger<ServerConnection> logger)
    {
        _logger = logger;
        AllowInsecureHttp = options.Server.AllowInsecureHttp;

        var url = options.Server.Url?.Trim();
        var token = options.Server.Token?.Trim();

        if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(token))
        {
            return; // sin emparejar: estado normal hasta orvitaprint pair (pipe de gestión)
        }

        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
        {
            logger.LogError("Configuración de servidor incompleta: hacen falta Server.Url y Server.Token (o ninguno).");
            return;
        }

        Apply(url, token, nameof(ServerConnection));
    }

    /// <summary>Transporte activo; null mientras no haya emparejamiento.</summary>
    public DirectTransport? Transport
    {
        get
        {
            lock (_gate)
            {
                return _transport;
            }
        }
    }

    /// <summary>URL base del transporte activo (no es secreta); null si no está emparejado.</summary>
    public Uri? ServerUrl
    {
        get
        {
            lock (_gate)
            {
                return _transport?.BaseUrl;
            }
        }
    }

    /// <summary>
    /// Sustituye el transporte (pair o re-pair por la pipe). El transporte anterior se libera.
    /// <paramref name="httpClient"/> opcional (pruebas): si llega, el transporte lo comparte en
    /// lugar de crear el suyo. Devuelve el nuevo transporte activo.
    /// </summary>
    public DirectTransport SetTransport(Uri serverUrl, string token, bool allowInsecureHttp, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        DirectTransport previous;
        DirectTransport next;
        lock (_gate)
        {
            previous = _transport!;
            next = new DirectTransport(serverUrl, token, httpClient, allowInsecureHttp: allowInsecureHttp);
            _transport = next;
        }

        previous?.Dispose();
        _logger.LogInformation("Transporte del agente actualizado: {Url} (HTTP sin TLS permitido: {Insecure}).", serverUrl, allowInsecureHttp);
        return next;
    }

    /// <summary>Quita el transporte activo (sin emparejar otra vez). Lo usan las pruebas y un futuro unpair.</summary>
    public void ClearTransport()
    {
        DirectTransport? previous;
        lock (_gate)
        {
            previous = _transport;
            _transport = null;
        }

        previous?.Dispose();
    }

    /// <summary>Aplica la configuración bootstrap de desarrollo; registra el error visible si la URL no vale.</summary>
    private void Apply(string url, string token, string origin)
    {
        try
        {
            lock (_gate)
            {
                _transport = new DirectTransport(new Uri(url, UriKind.Absolute), token, allowInsecureHttp: AllowInsecureHttp);
            }

            _logger.LogInformation(
                "Servidor configurado ({Origin}): {Url} (HTTP sin TLS permitido: {Insecure})",
                origin,
                url, // la URL no es secreta; el token nunca se registra (S-05)
                AllowInsecureHttp);
        }
        catch (ArgumentException ex)
        {
            // URL inválida o HTTP sin la bandera de desarrollo: visible y sin derribo del servicio.
            _logger.LogError("La URL del servidor no es válida: {Reason}", ex.Message);
        }
    }

    public void Dispose()
    {
        DirectTransport? transport;
        lock (_gate)
        {
            transport = _transport;
            _transport = null;
        }

        transport?.Dispose();
    }
}
