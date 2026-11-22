using System.Text.Json.Serialization;

namespace Orvita.Print.Contracts;

/// <summary>Salida física del agente: una impresora con su rol, copias y estado (Doc/04 §2.5). El agente es su dueño y la reporta en /pair y /heartbeat.</summary>
public sealed class DestinationInfo
{
    public string? Role { get; set; }

    public string? Label { get; set; }

    public string? PrinterName { get; set; }

    public int Copies { get; set; } = 1;

    public bool Enabled { get; set; } = true;
}

/// <summary>Respuesta de GET /health (Doc/04 §5.1). Sin autenticación.</summary>
public sealed class HealthInfo
{
    public bool? Ok { get; set; }

    public string? PluginVersion { get; set; }

    public int? ApiVersion { get; set; }

    public string? Woocommerce { get; set; }

    public bool? Hpos { get; set; }

    public DateTimeOffset? ServerTime { get; set; }
}

/// <summary>Cuerpo de POST /pair (Doc/04 §5.2).</summary>
public sealed class PairRequest
{
    public string? Code { get; set; }

    public string? AgentName { get; set; }

    public string? Platform { get; set; }

    public string? AgentVersion { get; set; }

    public List<DestinationInfo> Destinations { get; set; } = [];
}

/// <summary>Local en la respuesta de /pair (Doc/04 §5.2).</summary>
public sealed class LocationInfo
{
    public long Id { get; set; }

    public string? Code { get; set; }

    public string? Name { get; set; }
}

/// <summary>Respuesta de POST /pair (Doc/04 §5.2). El token se muestra una sola vez.</summary>
public sealed class PairResponse
{
    public string? Token { get; set; }

    public LocationInfo? Location { get; set; }

    [JsonPropertyName("poll_interval")]
    public int? PollIntervalSeconds { get; set; }
}

/// <summary>Cuerpo de POST /heartbeat (Doc/04 §5.3). Frecuencia: 60 s.</summary>
public sealed class HeartbeatRequest
{
    public string? AgentVersion { get; set; }

    public int PendingLocal { get; set; }

    public string? LastError { get; set; }

    public List<DestinationInfo> Destinations { get; set; } = [];
}

/// <summary>Respuesta de POST /heartbeat (Doc/04 §5.3). Si config_version cambia, el agente re-pide GET /config.</summary>
public sealed class HeartbeatResponse
{
    public bool? Ok { get; set; }

    public DateTimeOffset? ServerTime { get; set; }

    public int? PendingCount { get; set; }

    public long? ConfigVersion { get; set; }
}

/// <summary>Configuración remota del documento (Doc/04 §5.8). La sirve el plugin; el agente re-sincroniza cuando cambia config_version.</summary>
public sealed class AgentConfig
{
    public long? ConfigVersion { get; set; }

    public int? PaperWidthMm { get; set; }

    /// <summary>CP850 | CP858 | CP437 (tal cual, en mayúsculas).</summary>
    public string? Encoding { get; set; }

    public CutMode? CutMode { get; set; }

    public bool? OpenDrawer { get; set; }

    [JsonPropertyName("poll_interval")]
    public int? PollIntervalSeconds { get; set; }

    public bool? SoundAlert { get; set; }
}
