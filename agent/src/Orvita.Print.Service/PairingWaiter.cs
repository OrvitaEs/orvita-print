namespace Orvita.Print.Service;

/// <summary>
/// Espera del worker al emparejamiento por la pipe (F2-08): sin emparejar, el servicio queda
/// vivo y a la espera; cuando <see cref="PairingService"/> aplica un pair, lo despierta para
/// arrancar heartbeat + poller SIN reiniciar el proceso (ADR-13). Un <c>PairApplied</c> con
/// el worker ya en marcha (re-pair) llega por el evento y reinicia el poller.
/// </summary>
public sealed class PairingWaiter
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Señal para el worker en marcha: llega UNA vez por pair aplicado (re-pair incluido).</summary>
    public event Action? PairApplied;

    /// <summary>Completa cuando se aplica un pair (o cancela con la parada del servicio).</summary>
    public Task WaitForPairAsync(CancellationToken cancellationToken) => _signal.WaitAsync(cancellationToken);

    /// <summary>Lo llama PairingService cuando el pair quedó aplicado y guardado. Nunca lanza.</summary>
    public void NotifyPaired()
    {
        PairApplied?.Invoke();
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Dos pairs seguidos sin que el worker consumiera la señal: la nueva señal es la que vale.
        }
    }
}
