using Microsoft.Extensions.Logging;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Conexión con el servidor a partir de la configuración bootstrap. Mientras no haya
/// emparejamiento (URL + token), <see cref="Transport"/> es null y el servicio queda a la
/// espera con un mensaje claro: en producción el token va cifrado con DPAPI en el almacén
/// local (F2-10, S-01) y JAMÁS en appsettings ni en logs (S-05).
/// </summary>
public sealed class ServerConnection : IDisposable
{
    public DirectTransport? Transport { get; }

    public ServerConnection(AgentOptions options, ILogger<ServerConnection> logger)
    {
        var url = options.Server.Url?.Trim();
        var token = options.Server.Token?.Trim();

        if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(token))
        {
            return; // sin emparejar: estado normal hasta orvitaprint pair (F2-09/F2-10)
        }

        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
        {
            logger.LogError("Configuración de servidor incompleta: hacen falta Server.Url y Server.Token (o ninguno).");
            return;
        }

        try
        {
            Transport = new DirectTransport(
                new Uri(url, UriKind.Absolute),
                token,
                allowInsecureHttp: options.Server.AllowInsecureHttp);
            logger.LogInformation(
                "Servidor configurado: {Url} (HTTP sin TLS permitido: {Insecure})",
                Transport.BaseUrl, // la URL no es secreta; el token nunca se registra (S-05)
                options.Server.AllowInsecureHttp);
        }
        catch (ArgumentException ex)
        {
            // URL inválida o HTTP sin la bandera de desarrollo: visible y sin derriba del servicio.
            logger.LogError("La URL del servidor no es válida: {Reason}", ex.Message);
        }
    }

    public void Dispose() => Transport?.Dispose();
}
