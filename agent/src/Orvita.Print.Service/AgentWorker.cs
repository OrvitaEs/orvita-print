using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orvita.Print.Contracts;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Orquestador del agente (ADR-09: servicio headless autónomo). Al arrancar:
/// 1. recupera la cola (trabajos <c>printing</c> de una caída → reimpresión marcada
///    <c>duplicate_suspected</c>, Doc/03 §6.4) y drena lo pendiente;
/// 2. si está emparejado, envía el heartbeat inicial y arranca el poller;
/// 3. si NO está emparejado, avisa y queda VIVO esperando a la pipe de gestión (F2-08):
///    <c>orvitaprint pair</c> lo despierta y arranca heartbeat + poller sin reiniciar.
/// Después: heartbeat cada 60 s (Doc/04 §5.3) con re-sincronización cuando cambia
/// <c>config_version</c>, reiniciando el poller si el intervalo cambia. Un re-pair por la
/// pipe reinicia el poller con el transporte nuevo y lanza un latido inmediato.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly ServerConnection _connection;
    private readonly PairingWaiter _pairing;
    private readonly PairingService _pairingService;
    private readonly PollerController _poller;
    private readonly OutboxRepository _outbox;
    private readonly JobProcessor _processor;
    private readonly HeartbeatSender _heartbeat;
    private readonly RemoteConfigState _configState;
    private readonly AgentRuntimeStatus _runtime;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentWorker> _logger;

    public AgentWorker(
        ServerConnection connection,
        PairingWaiter pairing,
        PairingService pairingService,
        PollerController poller,
        OutboxRepository outbox,
        JobProcessor processor,
        HeartbeatSender heartbeat,
        RemoteConfigState configState,
        AgentRuntimeStatus runtime,
        AgentOptions options,
        ILogger<AgentWorker> logger)
    {
        _connection = connection;
        _pairing = pairing;
        _pairingService = pairingService;
        _poller = poller;
        _outbox = outbox;
        _processor = processor;
        _heartbeat = heartbeat;
        _configState = configState;
        _runtime = runtime;
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

            // 2. Sin emparejar por args: se intenta cargar el emparejamiento del almacén local
            //    (F2-10: reinicios y cortes de luz no obligan a reemparejar). Si no hay o el token
            //    no descifra, aviso visible y servicio vivo hasta que la pipe aplique un pair.
            //    Bucle con verificación: una señal residual del waiter no abre la puerta sin pair real.
            while (_connection.Transport is null)
            {
                if (_pairingService.TryLoadPairingFromStore())
                {
                    break;
                }

                _logger.LogWarning(
                    "Agente sin emparejar: falta ServerUrl o token. Empareja con «orvitaprint pair --server https://… <código>» " +
                    "desde otro terminal: el servicio arrancará el poller sin reiniciar (la URL y el token viven cifrados " +
                    "en el almacén local, F2-10/S-01). El poller y el heartbeat no arrancan hasta entonces.");
                await _pairing.WaitForPairAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Emparejamiento recibido por la pipe de gestión; arrancando heartbeat y poller.");
            }

            // 3. Heartbeat inicial: reporta destinos y sincroniza la configuración remota.
            await SendHeartbeatSafelyAsync(stoppingToken).ConfigureAwait(false);

            // 4. Poller (Doc/03 §6.3): fetch → claim → imprimir → ack, con jitter y backoff.
            _poller.Start(stoppingToken);

            // 5. Re-pair por la pipe (worker en marcha): poller nuevo con el transporte nuevo + latido ya.
            _pairing.PairApplied += OnPairApplied;

            // 6. Latidos cada 60 s (Doc/04 §5.3).
            var interval = TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatIntervalSeconds));
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var previousPollInterval = ResolvePollInterval();
                await SendHeartbeatSafelyAsync(stoppingToken).ConfigureAwait(false);
                if (ResolvePollInterval() != previousPollInterval)
                {
                    _logger.LogInformation("El intervalo de poll cambió a {Seconds} s; se reinicia el poller.", ResolvePollInterval());
                    await _poller.RestartAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada del servicio (o del SCM): limpia y sale.
        }
        finally
        {
            _pairing.PairApplied -= OnPairApplied;
            await _poller.StopAsync().ConfigureAwait(false);
            _logger.LogInformation("Agente Orvita Print detenido limpiamente.");
        }
    }

    private int ResolvePollInterval() =>
        _configState.PollIntervalSeconds ?? _runtime.PairPollIntervalSeconds ?? _options.PollIntervalSeconds;

    /// <summary>Re-pair con el worker en marcha: reinicia el poller (transporte nuevo) y lanza un latido inmediato.</summary>
    private void OnPairApplied()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Re-emparejamiento aplicado: reiniciando poller con el transporte nuevo.");
                await _poller.RestartAsync(CancellationToken.None).ConfigureAwait(false);
                await SendHeartbeatSafelyAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("El reinicio tras re-emparejamiento falló: {Message}", ex.Message);
            }
        });
    }

    private async Task SendHeartbeatSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _heartbeat.SendOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (TransportUnauthorizedException ex)
        {
            _logger.LogError(
                "El token del agente es inválido o fue revocado ({Message}). Reempareja el agente: orvitaprint pair. " +
                "El poller queda detenido; el servicio sigue vivo para poder emparejar de nuevo.",
                ex.Message);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw; // parada real: que la vea el bucle principal
        }
        catch (Exception ex)
        {
            _logger.LogWarning("El heartbeat falló: {Message}", ex.Message);
        }
    }
}
