namespace Orvita.Print.Core;

/// <summary>
/// Respaldo del endpoint <c>POST /jobs/{id}/release</c> (Doc/04 §5.7): devuelve un trabajo
/// reclamado a <c>pending</c> sin consumir intento. Lo implementan los transportes de servidor
/// que pueden liberar (<see cref="DirectTransport"/>); el procesador lo usa ante fallos
/// transitorios (spooler ocupado, red). Es opcional: el procesador degrada con elegancia.
/// </summary>
public interface IJobReleaseTransport
{
    /// <summary>Libera el trabajo con un motivo legible (p. ej. «spooler_timeout»).</summary>
    Task ReleaseAsync(long jobId, string reason, CancellationToken cancellationToken);
}
