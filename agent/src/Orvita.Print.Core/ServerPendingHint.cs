namespace Orvita.Print.Core;

/// <summary>
/// Puente entre el heartbeat y el poller (GUI-15, decisión C): el heartbeat reporta al
/// servidor y conoce el <c>pending_count</c> real de este local; el poller solo ve el
/// cursor. Un trabajo reencolado desde el panel («Reimprimir ticket») conserva su id, y
/// como el cursor solo avanza, el <c>GET /jobs?after=cursor</c> NO lo devuelve jamás.
/// Cuando el heartbeat informa de pendientes que el cursor no ve, el poller re-escanea
/// UNA vez desde <c>after=0</c> (seguro: el claim es atómico y los trabajos terminales
/// no aparecen en el listado de pendientes).
/// </summary>
public sealed class ServerPendingHint
{
    private int _pendingCount;
    private int _generation;

    /// <summary>El heartbeat (o una prueba) reporta los pendientes que ve el servidor.</summary>
    public void Report(int pendingCount)
    {
        Interlocked.Exchange(ref _pendingCount, Math.Max(0, pendingCount));
        Interlocked.Increment(ref _generation);
    }

    /// <summary>Instantánea (pendientes, generación). La generación crece en cada reporte.</summary>
    public (int PendingCount, int Generation) Snapshot() =>
        (Volatile.Read(ref _pendingCount), Volatile.Read(ref _generation));
}
