using Microsoft.Extensions.Logging;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Dueño del poller (F2-08): lo arranca el worker al emparejar y lo reinicia la pipe de
/// gestión (<c>restart-poller</c>) o un re-pair sin reiniciar el proceso. El transporte se
/// resuelve en CADA arranque vía el accessor (<see cref="ServerConnection"/>), así que un
/// reinicio tras re-pair usa el transporte nuevo. El estado visible (<c>orvitaprint status</c>)
/// es el del último poller construido (Doc/03 §6.3).
/// </summary>
public sealed class PollerController
{
    private readonly Func<ITransport> _serverAccessor;
    private readonly JobProcessor _processor;
    private readonly Func<int> _intervalSeconds;
    private readonly ServerPendingHint? _serverPending;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _task;
    private Poller? _poller;

    public PollerController(
        Func<ITransport> serverAccessor,
        JobProcessor processor,
        Func<int> intervalSeconds,
        ILogger<PollerController> logger,
        ServerPendingHint? serverPending = null)
    {
        _serverAccessor = serverAccessor ?? throw new ArgumentNullException(nameof(serverAccessor));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _intervalSeconds = intervalSeconds ?? throw new ArgumentNullException(nameof(intervalSeconds));
        _logger = logger;
        _serverPending = serverPending;
    }

    /// <summary>Estado visible del poller actual (Idle si nunca arrancó / está parado).</summary>
    public PollerState State
    {
        get
        {
            lock (_gate)
            {
                return _poller?.State ?? PollerState.Idle;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _task is { IsCompleted: false };
            }
        }
    }

    /// <summary>Arranca el poller (una sola vez: si ya corre no hace nada). Requiere emparejamiento.</summary>
    public void Start(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            if (_task is { IsCompleted: false })
            {
                return;
            }

            var poller = new Poller(
                _serverAccessor(), // el transporte ACTIVO en este instante (re-pair → nuevo transporte)
                new PollerOptions { BaseInterval = TimeSpan.FromSeconds(Math.Max(1, _intervalSeconds())) },
                log: message => _logger.LogInformation("{Message}", message),
                serverPending: _serverPending);

            _poller = poller;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var token = _cts.Token;
            _task = poller.RunAsync(
                (job, ct) => _processor.ProcessJobAsync(job, ct),
                token).ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.LogError(t.Exception, "El poller murió de forma inesperada.");
                    }
                    else if (poller.State == PollerState.StoppedUnauthorized)
                    {
                        _logger.LogError("El poller está detenido: token inválido o revocado (401). Reempareja el agente: orvitaprint pair.");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _logger.LogInformation("Poller arrancado (intervalo base {Seconds} s ± 20 %).", Math.Max(1, _intervalSeconds()));
        }
    }

    /// <summary>Para el poller y espera su finalización limpia. Idempotente.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _cts;
            task = _task;
            _cts = null;
            _task = null;
            _poller = null;
        }

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        if (task is { } running)
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Parada esperada.
            }
            catch (Exception ex)
            {
                // El poller VIEJO murió con un error no contractual: se registra y se sigue.
                // NUNCA se propaga a quien pidió la parada/reinicio (el bucle del heartbeat y
                // el re-pair por la pipe no pueden morir por un poller anterior accidentado).
                _logger.LogError(ex, "El poller anterior terminó con un error inesperado al pararlo.");
            }
        }

        cts.Dispose();
    }

    /// <summary>Reinicio completo (re-pair, restart-poller o cambio de intervalo): parar → arrancar.</summary>
    public async Task RestartAsync(CancellationToken stoppingToken)
    {
        await StopAsync().ConfigureAwait(false);
        Start(stoppingToken);
    }
}
