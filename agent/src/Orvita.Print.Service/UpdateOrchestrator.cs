using Microsoft.Extensions.Logging;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Un ciclo de comprobación de actualización (F2-11), aislado del temporizador para que las
/// pruebas lo llamen directamente. Regla de oro (Doc/03 §10): CUALQUIER fallo conserva la
/// versión actual — se registra en <see cref="UpdateStatus"/> (visible en el diagnóstico), se
/// escribe en el log y el siguiente ciclo diario reintenta. No lanza nunca hacia fuera.
/// </summary>
public sealed class UpdateOrchestrator
{
    private readonly IUpdateChecker _checker;
    private readonly IUpdateApplier _applier;
    private readonly UpdateStatus _status;
    private readonly ILogger _logger;

    public UpdateOrchestrator(
        IUpdateChecker checker,
        IUpdateApplier applier,
        UpdateStatus status,
        ILogger? logger = null)
    {
        _checker = checker ?? throw new ArgumentNullException(nameof(checker));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>true si se lanzó una instalación (el proceso morirá pronto: msiexec lo reemplaza).</summary>
    public async Task<bool> CheckAndApplyAsync(CancellationToken cancellationToken)
    {
        if (!AgentVersion.TryParseCurrentVersion(out var currentVersion))
        {
            _status.RecordResult("failed", null, "La versión del agente no es interpretable; el canal de actualización queda inactivo.");
            _logger.LogWarning("La versión del agente («{Version}») no se puede comparar; no se comprueba el canal de actualización.", AgentVersion.Current);
            return false;
        }

        try
        {
            var update = await _checker.CheckAsync(currentVersion, cancellationToken).ConfigureAwait(false);
            if (update is null)
            {
                _status.RecordUpToDate(null);
                return false;
            }

            _logger.LogInformation(
                "Actualización disponible: {Version} (actual {Current}). Aplicando con instalador verificado.",
                update.Manifest.Version,
                currentVersion);
            await _applier.ApplyAsync(update, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (UpdateException ex)
        {
            // Fallo de validación del canal: log claro, versión actual intacta, reintento diario.
            _status.RecordFailure($"{ex.Code}: {ex.Message}");
            _logger.LogWarning(
                "La comprobación de actualización falló ({Code}): {Message}. Se conserva la versión actual; " +
                "se reintentará en el siguiente ciclo diario (Doc/03 §10).",
                ex.Code,
                ex.Message);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // parada del servicio: que la gestione el host
        }
        catch (Exception ex)
        {
            _status.RecordFailure($"update_internal_error: {ex.Message}");
            _logger.LogWarning(ex, "La actualización falló de forma inesperada; se conserva la versión actual.");
            return false;
        }
    }
}
