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

/// <summary>Canal de actualización (F2-11): appsettings bootstrap (Doc/03 §6.2.1, capa estática).</summary>
public sealed class UpdateServiceOptions
{
    /// <summary>Interruptor maestro (por defecto activo: con ManifestUrl vacío no hay red).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>URL del latest.json (https://updates.…/agent/latest.json). Vacío → canal inactivo.</summary>
    public string? ManifestUrl { get; set; }

    /// <summary>Cadencia de comprobación en horas (Doc/03 §10: 1×/día).</summary>
    public int CheckIntervalHours { get; set; } = 24;

    /// <summary>Retardo del primer chequeo tras arrancar el servicio (no compite con el arranque).</summary>
    public int FirstCheckDelayMinutes { get; set; } = 5;
}

/// <summary>
/// Configuración bootstrap del servicio (Doc/03 §6.2.1, capa «estática»): SOLO infraestructura
/// — rutas, log, intervalos de respaldo y, como comodín de DESARROLLO, la URL/token directos
/// (<c>--server/--token</c>). Todo lo gestionable vive en el almacén local SQLite (F2-10):
/// ServerUrl, token DPAPI y salidas; la configuración del documento llega por
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

    /// <summary>
    /// Canal de actualización automática (F2-11, Doc/03 §10). Todo es infraestructura bootstrap
    /// (appsettings.json): el manifiesto llega de una URL estática que publica F3-03; la clave
    /// pública de verificación NO es configuración, va embebida (<see cref="UpdateTrust"/>).
    /// </summary>
    public UpdateServiceOptions Update { get; set; } = new();

    public ServerOptions Server { get; set; } = new();

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
