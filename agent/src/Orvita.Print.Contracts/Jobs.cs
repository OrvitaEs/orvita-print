using System.Text.Json.Serialization;

namespace Orvita.Print.Contracts;

/// <summary>Trabajo tal como lo entrega el servidor (Doc/04 §5.4: GET /jobs y §5.5: claim).</summary>
public sealed class Job
{
    public long Id { get; set; }

    public long? OrderId { get; set; }

    public string? PrinterRole { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Intento en curso (0 en el listado, ≥ 1 tras el claim).</summary>
    public int Attempt { get; set; }

    public Document? Document { get; set; }
}

/// <summary>Lote de trabajos de GET /jobs (Doc/04 §5.4). Sin trabajos: lista vacía y cursor = after.</summary>
public sealed class JobBatch
{
    public List<Job> Jobs { get; set; } = [];

    /// <summary>Nuevo cursor para la siguiente petición; avanza aunque un trabajo falle.</summary>
    public long Cursor { get; set; }
}

/// <summary>Resultado final de un trabajo, cuerpo del POST /jobs/{id}/ack (Doc/04 §5.6).</summary>
public sealed class JobResult
{
    public JobResultStatus Status { get; set; } = JobResultStatus.Delivered;

    public int Attempt { get; set; }

    /// <summary>Nombre exacto de la impresora usada.</summary>
    public string? Printer { get; set; }

    public long? ElapsedMs { get; set; }

    /// <summary>Código del agente en caso de fallo (p. ej. orp_printer_not_found). No es un código HTTP.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>Mensaje legible del fallo.</summary>
    public string? Error { get; set; }
}

/// <summary>Estado que reporta el agente en el ack (Doc/04 §5.6).</summary>
public enum JobResultStatus
{
    [JsonStringEnumMemberName("delivered")]
    Delivered,

    [JsonStringEnumMemberName("failed")]
    Failed,
}

/// <summary>Respuesta del ack: { "ok": true, "job_status": "delivered" } (Doc/04 §5.6).</summary>
public sealed class DocumentAck
{
    public bool? Ok { get; set; }

    public string? JobStatus { get; set; }
}
