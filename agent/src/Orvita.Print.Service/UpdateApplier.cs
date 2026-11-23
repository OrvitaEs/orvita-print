using Microsoft.Extensions.Logging;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Aplicador de actualizaciones (F2-11) — Windows-only por decisión documentada: <c>msiexec</c>
/// solo existe en Windows y el orquestado (parar poller + pipe de gestión) son componentes del
/// Service. ADR-12 se respeta porque Core/Contracts no conocen nada de esto: el Service ya es
/// el proyecto Windows de V1 (net10.0-windows), y <c>Printing.Windows</c> queda para transporte
/// de impresión.
///
/// Secuencia (Doc/03 §10): descargar y verificar YA lo hizo el <c>UpdateChecker</c>; aquí:
/// 1. parar el poller limpiamente (ninguna impresión en vuelo mientras el MSI sustituye binarios);
/// 2. parar la pipe de gestión (desde ese instante el CLI responde «servicio no disponible»);
/// 3. <c>msiexec /i &lt;msi&gt; /qn /norestart</c>: el MSI instala por máquina y el Windows
///    Installer DETIENE este servicio para reemplazar el binario y lo ARRANCA de nuevo — el
///    servicio «se reinicia solo» con la versión nueva. Si el proceso muere antes de terminar,
///    el proceso msiexec independiente continúa.
/// Si algo falla (msiexec distinto de 0/3010, excepción): se conserva la versión actual y el
/// ciclo diario reintenta; el fallo queda visible en el log y en el diagnóstico.
/// </summary>
public sealed class UpdateApplier : IUpdateApplier
{
    /// <summary>Código de salida de msiexec «éxito con reinicio requerido».</summary>
    public const int ExitCodeRebootRequired = 3010;

    private readonly PollerController _poller;
    private readonly ManagementPipeServer _pipeServer;
    private readonly UpdateStatus _status;
    private readonly ILogger _logger;
    private readonly Func<string, Task<int>>? _msiRunner;

    /// <param name="msiRunner">Opcional (pruebas): ejecuta el instalador y devuelve el exit code.
    /// null → msiexec real con <c>/i &lt;ruta&gt; /qn /norestart</c>.</param>
    public UpdateApplier(
        PollerController poller,
        ManagementPipeServer pipeServer,
        UpdateStatus status,
        ILogger? logger = null,
        Func<string, Task<int>>? msiRunner = null)
    {
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _pipeServer = pipeServer ?? throw new ArgumentNullException(nameof(pipeServer));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _msiRunner = msiRunner;
    }

    public async Task ApplyAsync(PreparedUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var installerPath = update.InstallerPath;
        _logger.LogInformation(
            "Aplicando actualización a {Version}: parando poller y pipe de gestión antes de msiexec.",
            update.Manifest.Version);

        try
        {
            // 1. Poller limpio: nada imprimiéndose cuando el MSI sustituye binarios (Doc/03 §10).
            await _poller.StopAsync().ConfigureAwait(false);

            // 2. Pipe de gestión: ya no se aceptan operaciones mientras se aplica.
            await _pipeServer.StopAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Ejecutando msiexec /i {Installer} /qn /norestart (actualización a {Version}).",
                installerPath,
                update.Manifest.Version);

            // 3. msiexec. /norestart: nunca un reinicio de la máquina sorpresivo en un TPV.
            var exitCode = _msiRunner is not null
                ? await _msiRunner(installerPath).ConfigureAwait(false)
                : await RunMsiExecAsync(installerPath, cancellationToken).ConfigureAwait(false);

            if (exitCode == ExitCodeRebootRequired)
            {
                _logger.LogWarning(
                    "El instalador de {Version} terminó con 3010 (éxito, reinicio requerido): el servicio se " +
                    "levantará con la versión nueva cuando la máquina reinicie.",
                    update.Manifest.Version);
                _status.RecordApplied(update.Manifest.Version, "reboot_required");
                return;
            }

            if (exitCode != 0)
            {
                throw new UpdateException(
                    "update_msiexec_failed",
                    $"msiexec devolvió el código {exitCode} instalando {update.Manifest.Version}: se conserva la versión actual.");
            }

            // Éxito: el Windows Installer detendrá este proceso para sustituir el binario y
            // arrancará el servicio con la versión nueva. Este hilo puede no volver a correr.
            _logger.LogInformation(
                "Instalación de {Version} en marcha: el servicio se reinicia solo (instalación por máquina, Doc/03 §10).",
                update.Manifest.Version);
            _status.RecordApplied(update.Manifest.Version, "applied");
        }
        catch (UpdateException)
        {
            _status.RecordFailure("update_msiexec_failed: el instalador no terminó bien; se conserva la versión actual.");
            throw;
        }
        finally
        {
            // El MSI temporal ya no hace falta ni con éxito ni con fallo (el ciclo diario
            // descarga de nuevo). Best effort: si msiexec lo retiene, queda un huérfano en %TEMP%.
            TryDeleteInstaller(installerPath);
        }
    }

    private static void TryDeleteInstaller(string installerPath)
    {
        try
        {
            if (File.Exists(installerPath))
            {
                File.Delete(installerPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<int> RunMsiExecAsync(string installerPath, CancellationToken cancellationToken)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = $"/i \"{installerPath}\" /qn /norestart",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new UpdateException("update_msiexec_failed", "msiexec no se pudo arrancar.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}

/// <summary>Contrato del aplicador (aislado para las pruebas del orquestador).</summary>
public interface IUpdateApplier
{
    /// <summary>Aplica la actualización verificada: parar carga + msiexec. Puede lanzar UpdateException.</summary>
    Task ApplyAsync(PreparedUpdate update, CancellationToken cancellationToken);
}
