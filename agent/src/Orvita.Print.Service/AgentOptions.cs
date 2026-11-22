using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>URL/token del servidor (desarrollo). En producción llegan del almacén SQLite (F2-10) vía la pipe de gestión.</summary>
public sealed class ServerOptions
{
    public string? Url { get; set; }

    public string? Token { get; set; }

    /// <summary> Solo para desarrollo (wp-env); en producción el agente exige HTTPS (S-03).</summary>
    public bool AllowInsecureHttp { get; set; }
}

/// <summary>
/// Configuración bootstrap del servicio (Doc/03 §6.2.1, capa «estática»): SOLO infraestructura
/// — rutas, log, intervalos de respaldo y, mientras no exista el almacén F2-10, la URL/token de
/// desarrollo y las salidas. Nada de esto es configurable del documento: eso llega por
/// <c>GET /config</c> (Doc/04 §5.8) y vive en <see cref="RemoteConfigState"/>.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Orvita";

    /// <summary>
    /// Directorio raíz de datos. Por defecto <c>%ProgramData%\OrvitaPrint</c> (Doc/03 §6.2);
    /// una ruta relativa se resuelve contra el directorio del ejecutable (modo desarrollo).
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>Frecuencia del heartbeat en segundos (Doc/04 §5.3: 60 s).</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>Intervalo de respaldo del poller hasta el primer GET /config (que es autoridad).</summary>
    public int PollIntervalSeconds { get; set; } = 10;

    /// <summary>Reintentos locales ante fallo transitorio de impresión antes del release.</summary>
    public int TransientPrintRetries { get; set; } = 3;

    /// <summary>Base de espera entre reintentos transitorios (2 s → 4 s → 6 s…).</summary>
    public int TransientRetryDelaySeconds { get; set; } = 2;

    public ServerOptions Server { get; set; } = new();

    /// <summary>Salidas del agente (Doc/03 §6.7). F2-10 las moverá a SQLite vía CLI.</summary>
    public List<DestinationOptions> Destinations { get; set; } = [];

    /// <summary>Resuelve el directorio de datos a ruta absoluta.</summary>
    public string ResolveDataDirectory()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "OrvitaPrint");
        }

        return Path.IsPathRooted(DataDirectory)
            ? DataDirectory
            : Path.Combine(AppContext.BaseDirectory, DataDirectory);
    }
}
