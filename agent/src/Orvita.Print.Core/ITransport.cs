using Orvita.Print.Contracts;

namespace Orvita.Print.Core;

/// <summary>
/// Transporte contra el servidor (Doc/03 §7). El agente siempre inicia la conexión (pull):
/// el poller consulta, reclama y confirma. Un cambio de modo (directo → relay) es un
/// cambio de implementación de esta interfaz, no del núcleo.
/// </summary>
public interface ITransport
{
    /// <summary>GET /jobs?after=&amp;limit= — lote de trabajos pendientes con cursor.</summary>
    Task<JobBatch> FetchAsync(Cursor cursor, CancellationToken ct);

    /// <summary>POST /jobs/{id}/claim — reclamo atómico. 200 → el trabajo; 409/410/404 → null («otro lo cogió», caducado o ajeno).</summary>
    Task<Job?> ClaimAsync(long jobId, CancellationToken ct);

    /// <summary>POST /jobs/{id}/ack — resultado final (delivered | failed).</summary>
    Task AckAsync(long jobId, JobResult result, CancellationToken ct);
}
