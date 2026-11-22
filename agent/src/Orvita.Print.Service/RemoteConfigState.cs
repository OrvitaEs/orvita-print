using Microsoft.Extensions.Logging;
using Orvita.Print.Contracts;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Estado de la configuración remota (<c>GET /config</c>, Doc/04 §5.8) que el agente aplica
/// cuando <c>config_version</c> cambia (heartbeat Doc/04 §5.3). Solo lo común al documento:
/// codificación y ancho de papel; el resto (corte, cajón) lo llevan los bloques del propio
/// documento, y el intervalo de poll lo aplica el Service reiniciando el Poller.
/// </summary>
public sealed class RemoteConfigState
{
    private readonly object _lock = new();
    private AgentConfig? _current;

    /// <summary>Versión de configuración aplicada; null si aún no hubo sincronización.</summary>
    public long? ConfigVersion
    {
        get
        {
            lock (_lock)
            {
                return _current?.ConfigVersion;
            }
        }
    }

    /// <summary>Intervalo de poll remoto (autoridad sobre el de appsettings); null si no hay config.</summary>
    public int? PollIntervalSeconds
    {
        get
        {
            lock (_lock)
            {
                return _current?.PollIntervalSeconds;
            }
        }
    }

    /// <summary>Opciones del renderizador a partir de la config remota; por defecto CP850 (Doc/03 §6.5).</summary>
    public EscPosRendererOptions ToRendererOptions()
    {
        lock (_lock)
        {
            if (_current is null)
            {
                return new EscPosRendererOptions();
            }

            return new EscPosRendererOptions
            {
                TextEncoding = ParseEncoding(_current.Encoding),
                // El ancho por documento lo deduce el renderizador de paper.width_mm;
                // solo fijamos columnas si el servidor manda un ancho no estándar.
                Columns = _current.PaperWidthMm is { } mm && mm is not (58 or 80)
                    ? EscPosRendererOptions.ColumnsForPaperWidth(mm)
                    : null,
            };
        }
    }

    /// <summary>Aplica una configuración recibida; devuelve true si cambió algo respecto a la anterior.</summary>
    public bool Update(AgentConfig config, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_lock)
        {
            var changed = _current is null
                || _current.ConfigVersion != config.ConfigVersion
                || _current.Encoding != config.Encoding
                || _current.PaperWidthMm != config.PaperWidthMm
                || _current.PollIntervalSeconds != config.PollIntervalSeconds;

            _current = config;
            if (changed)
            {
                logger.LogInformation(
                    "Configuración remota aplicada (config_version {Version}): encoding {Encoding}, papel {Paper} mm, poll {Poll} s.",
                    config.ConfigVersion, config.Encoding ?? "CP850", config.PaperWidthMm ?? 80, config.PollIntervalSeconds ?? 10);
            }

            return changed;
        }
    }

    private static EscPosEncoding ParseEncoding(string? name) =>
        Enum.TryParse<EscPosEncoding>(name?.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : EscPosEncoding.Cp850;
}
