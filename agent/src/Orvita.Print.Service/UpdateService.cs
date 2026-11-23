using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Servicio hospedado del canal de actualización (F2-11): tarea en SEGUNDO PLANO con su propio
/// CancellationToken — no bloquea NUNCA al poller ni al worker (Doc/03 §10: 1 comprobación/día
/// configurable). Arranca con un retardo inicial (por defecto 5 min: deja respirar al arranque
/// del servicio) y repite cada <c>Update.CheckIntervalHours</c>. Si el canal no está configurado
/// o está desactivado, queda inactivo con un aviso. F3-02: si la licencia pasó su
/// <c>updates_until</c>, el agente SIGUE funcionando pero NO acepta actualizaciones (Doc/03 §10).
/// </summary>
public sealed class UpdateService : BackgroundService
{
    private readonly UpdateOrchestrator _orchestrator;
    private readonly UpdateCheckerOptions _checkerOptions;
    private readonly UpdateServiceOptions _options;
    private readonly LicenseState? _license;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(
        UpdateOrchestrator orchestrator,
        UpdateCheckerOptions checkerOptions,
        UpdateServiceOptions options,
        ILogger<UpdateService> logger,
        LicenseState? license = null)
    {
        _orchestrator = orchestrator;
        _checkerOptions = checkerOptions;
        _options = options;
        _logger = logger;
        _license = license;
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
                // F3-02 (Doc/03 §10): updates_until pasado → el agente sigue funcionando y solo
                // deja de aceptar actualizaciones. Se consulta en CADA ciclo: renovar la licencia
                // (y reiniciar el servicio) reactiva el canal sin tocar appsettings.
                if (_license is { UpdatesExpired: true })
                {
                    _logger.LogWarning(
                        "Licencia {LicenseId} con updates_until {Until} ya pasado: NO se comprueba el canal de " +
                        "actualización (el agente sigue funcionando con normalidad, Doc/03 §10). Renueva la " +
                        "licencia con el proveedor y reinicia el servicio para reactivar las actualizaciones.",
                        _license.LicenseId, _license.UpdatesUntil);
                }
                else
                {
                    await _orchestrator.CheckAndApplyAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada del servicio.
        }
    }
}
