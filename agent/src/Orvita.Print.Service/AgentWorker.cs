using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orvita.Print.Contracts;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Orquestador del agente (ADR-09: servicio headless autónomo). Al arrancar:
/// 1. recupera la cola (trabajos <c>printing</c> de una caída → reimpresión marcada
///    <c>duplicate_suspected</c>, Doc/03 §6.4) y drena lo pendiente;
/// 2. envía el heartbeat inicial (reporta destinos, Doc/03 §6.2.1);
/// 3. arranca el Poller (10 s ± jitter, Doc/03 §6.3).
/// Después: heartbeat cada 60 s (Doc/04 §5.3) con re-sincronización cuando cambia
/// <c>config_version</c>, reiniciando el poller si el servidor cambia el intervalo de poll.
/// La pipe de gestión llega en F2-08; sin ella el servicio ya imprime solo.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly ServerConnection _connection;
    private readonly Func<ITransport> _serverAccessor;
    private readonly OutboxRepository _outbox;
    private readonly JobProcessor _processor;
    private readonly HeartbeatSender _heartbeat;
    private readonly RemoteConfigState _configState;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentWorker> _logger;

    private CancellationTokenSource? _pollerCts;
    private Task? _pollerTask;

    public AgentWorker(
        ServerConnection connection,
        Func<ITransport> serverAccessor,
        OutboxRepository outbox,
        JobProcessor processor,
        HeartbeatSender heartbeat,
        RemoteConfigState configState,
        AgentOptions options,
        ILogger<AgentWorker> logger)
    {
        _connection = connection;
        _serverAccessor = serverAccessor;
        _outbox = outbox;
        _processor = processor;
        _heartbeat = heartbeat;
        _configState = configState;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Agente Orvita Print {Version} arrancando ({Platform}).",
            AgentVersion.Current,
            Environment.OSVersion.VersionString);

        try
        {
            // 1. Recuperación y drenaje de la cola local (Doc/03 §6.4).
            var recovered = _outbox.RecoverInterrupted();
            if (recovered.Count > 0)
            {
                _logger.LogWarning("{Count} trabajo(s) recuperado(s) tras una caída; se reimprimirán.", recovered.Count);
            }

            await _processor.ProcessStartupQueueAsync(stoppingToken).ConfigureAwait(false);

            // 2. Sin emparejar: estado visible y sin derribo (la pipe de gestión, F2-08, permitirá emparejar en caliente).
            if (_connection.Transport is null)
            {
                _logger.LogWarning(
                    "Agente sin emparejar: falta ServerUrl o token. En producción usa «orvitaprint pair --server https://… <código>» " +
                    "(la URL y el token viven cifrados en el almacén local, F2-10). El poller y el heartbeat no arrancan.");
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
                return;
            }

            // 3. Heartbeat inicial: reporta destinos y sincroniza la configuración remota.
            try
            {
                await _heartbeat.SendOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("El heartbeat inicial falló: {Message}. Se reintentará en el próximo latido.", ex.Message);
            }

            // 4. Poller (Doc/03 §6.3): fetch → claim → imprimir → ack, con jitter y backoff.
            StartPoller(stoppingToken);

            // 5. Latidos cada 60 s (Doc/04 §5.3).
            var interval = TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatIntervalSeconds));
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var previousPollInterval = _configState.PollIntervalSeconds ?? _options.PollIntervalSeconds;
                try
                {
                    await _heartbeat.SendOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (TransportUnauthorizedException ex)
                {
                    _logger.LogError(
                        "El token del agente es inválido o fue revocado ({Message}). Reempareja el agente: orvitaprint pair. " +
                        "El poller ya está detenido; el servicio sigue vivo para poder emparejar de nuevo.",
                        ex.Message);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("El heartbeat falló: {Message}", ex.Message);
                }

                var newPollInterval = _configState.PollIntervalSeconds ?? _options.PollIntervalSeconds;
                if (newPollInterval != previousPollInterval && newPollInterval > 0)
                {
                    _logger.LogInformation("El servidor cambió el intervalo de poll a {Seconds} s; se reinicia el poller.", newPollInterval);
                    await StopPollerAsync().ConfigureAwait(false);
                    StartPoller(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada del servicio (o del SCM): limpia y sale.
        }
        finally
        {
            await StopPollerAsync().ConfigureAwait(false);
            _logger.LogInformation("Agente Orvita Print detenido limpiamente.");
        }
    }

    private void StartPoller(CancellationToken stoppingToken)
    {
        var interval = _configState.PollIntervalSeconds ?? _options.PollIntervalSeconds;

        var poller = new Poller(
            _serverAccessor(), // el mismo servidor que el procesador (inyectable en pruebas)
            new PollerOptions { BaseInterval = TimeSpan.FromSeconds(Math.Max(1, interval)) },
            log: message => _logger.LogInformation("{Message}", message));

        _pollerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _pollerTask = poller.RunAsync(
            (job, ct) => _processor.ProcessJobAsync(job, ct),
            _pollerCts.Token).ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                {
                    _logger.LogError(t.Exception, "El poller murió de forma inesperada.");
                }
                else if (poller.State == PollerState.StoppedUnauthorized)
                {
                    _logger.LogError("El poller está detenido: token inválido o revocado (401). Reempareja el agente.");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task StopPollerAsync()
    {
        if (_pollerCts is null)
        {
            return;
        }

        await _pollerCts.CancelAsync().ConfigureAwait(false);
        if (_pollerTask is { } task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Parada esperada.
            }
        }

        _pollerCts.Dispose();
        _pollerCts = null;
        _pollerTask = null;
    }
}
