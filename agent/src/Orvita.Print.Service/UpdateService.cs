using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Servicio hospedado del canal de actualización (F2-11): tarea en SEGUNDO PLANO con su propio
/// CancellationToken — no bloquea NUNCA al poller ni al worker (Doc/03 §10: 1 comprobación/día
/// configurable). Arranca con un retardo inicial (por defecto 5 min: deja respirar al arranque
/// del servicio) y repite cada <c>Update.CheckIntervalHours</c>. Si el canal no está configurado
/// o está desactivado, queda inactivo con un aviso.
/// </summary>
public sealed class UpdateService : BackgroundService
{
    private readonly UpdateOrchestrator _orchestrator;
    private readonly UpdateCheckerOptions _checkerOptions;
    private readonly UpdateServiceOptions _options;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(
        UpdateOrchestrator orchestrator,
        UpdateCheckerOptions checkerOptions,
        UpdateServiceOptions options,
        ILogger<UpdateService> logger)
    {
        _orchestrator = orchestrator;
        _checkerOptions = checkerOptions;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var manifestUrl = _checkerOptions.ManifestUrl?.Trim();
        if (!_options.Enabled || string.IsNullOrEmpty(manifestUrl))
        {
            _logger.LogInformation(
                "Canal de actualización desactivado ({Reason}): el agente seguirá en su versión hasta " +
                "instalación manual. Se activa con Orvita:Update:ManifestUrl en appsettings.json.",
                !_options.Enabled ? "Update:Enabled=false" : "sin Update:ManifestUrl");
            return;
        }

        _logger.LogInformation(
            "Canal de actualización activo: {Url} · primer chequeo en {Minutes} min · luego cada {Hours} h.",
            manifestUrl,
            Math.Max(0, _options.FirstCheckDelayMinutes),
            Math.Max(1, _options.CheckIntervalHours));

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(0, _options.FirstCheckDelayMinutes)), stoppingToken)
                .ConfigureAwait(false);

            using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, _options.CheckIntervalHours)));
            do
            {
                await _orchestrator.CheckAndApplyAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada del servicio.
        }
    }
}
