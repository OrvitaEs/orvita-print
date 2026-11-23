using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Orvita.Print.Core;

/// <summary>Opciones del <see cref="UpdateChecker"/> (F2-11).</summary>
public sealed class UpdateCheckerOptions
{
    /// <summary>URL del <c>latest.json</c> (canal de actualización). Vacía/null → canal desactivado.</summary>
    public string? ManifestUrl { get; set; }

    /// <summary>Clave pública de firma del manifiesto, 32 bytes en crudo codificados en base64.
    /// Va EMBEBIDA en el agente (nunca en el manifiesto ni en configuración editable), como la
    /// de licencia (Doc/03 §8).</summary>
    public string PublicKeyBase64 { get; set; } = string.Empty;

    /// <summary>Límite del tamaño del manifiesto (defensa ante un canal roto o malicioso).</summary>
    public int MaxManifestBytes { get; set; } = 1024 * 1024;

    /// <summary>Límite del tamaño del instalador (MSI self-contained x64: decenas de MB; margen alto).</summary>
    public long MaxInstallerBytes { get; set; } = 768L * 1024 * 1024;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PublicKeyBase64))
        {
            throw new UpdateException("update_manifest_invalid", "Falta la clave pública de verificación de actualizaciones (UpdateCheckerOptions.PublicKeyBase64).");
        }

        var key = Convert.FromBase64String(PublicKeyBase64);
        if (key.Length != Ed25519.KeySize)
        {
            throw new UpdateException("update_manifest_invalid", $"La clave pública Ed25519 debe tener {Ed25519.KeySize} bytes (llegan {key.Length}).");
        }
    }

    /// <summary>Clave pública decodificada (32 bytes).</summary>
    public byte[] PublicKey() => Convert.FromBase64String(PublicKeyBase64);
}

/// <summary>Actualización descargada y VERIFICADA (firma del manifiesto + hash del MSI): lista para aplicar.</summary>
public sealed record PreparedUpdate
{
    public required UpdateManifest Manifest { get; init; }

    /// <summary>Ruta local del instalador descargado (temporal; la limpia el aplicador ante fallo).</summary>
    public required string InstallerPath { get; init; }
}

/// <summary>Verificador del canal de actualización (abstracción para el orquestador del Service).</summary>
public interface IUpdateChecker
{
    /// <summary>
    /// Comprueba el canal. Devuelve la actualización verificada (instalador ya descargado) o
    /// null si no hay novedad (canal desactivado o versión igual/inferior a la actual).
    /// Lanza <see cref="UpdateException"/> ante cualquier validación fallida.
    /// </summary>
    Task<PreparedUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken);
}

/// <summary>
/// Comprobador de actualizaciones (F2-11, Core portable). Secuencia con validaciones EN ORDEN
/// y en cortocircuito — y SIEMPRE: si algo falla, no se toca la instalación actual:
/// 1. canal configurado (si no, null sin tocar la red);
/// 2. URL del manifiesto absoluta y HTTPS (S-03; para actualizaciones NO hay excepción de
///    desarrollo: un canal HTTP ejecutaría un instalador);
/// 3. descarga del manifiesto (con límite de tamaño);
/// 4. firma Ed25519 del manifiesto sobre los bytes canónicos (Doc/03 §10: nunca se acepta una
///    actualización sin firma válida);
/// 5. versiones: igual o inferior a la actual → null (sin downgrade); <c>min_supported</c>
///    mayor que la versión actual → rechazo explícito (el canal no da saltos desde versiones
///    tan viejas: reinstalación manual);
/// 6. descarga del MSI a un fichero temporal con hash SHA-256 EN STREAMING y límite de tamaño;
/// 7. comparación del hash con el del manifiesto (solo si coincide se devuelve el resultado).
/// </summary>
public sealed class UpdateChecker : IUpdateChecker, IDisposable
{
    private readonly UpdateCheckerOptions _options;
    private readonly ISignatureVerifier _verifier;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Action<string>? _log;

    /// <param name="httpClient">Opcional (pruebas): el HttpClient a usar; null → uno propio con timeout 100 s.</param>
    public UpdateChecker(UpdateCheckerOptions options, ISignatureVerifier? verifier = null, HttpClient? httpClient = null, Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _verifier = verifier ?? new Ed25519Verifier();
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        _log = log;
    }

    /// <summary>Libera el HttpClient solo si lo creó este checker (el inyectado es del llamador).</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public async Task<PreparedUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        var manifestUrl = _options.ManifestUrl?.Trim();
        if (string.IsNullOrEmpty(manifestUrl))
        {
            _log?.Invoke("Canal de actualización desactivado: no hay URL de latest.json configurada.");
            return null;
        }

        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var manifestUri) ||
            (manifestUri.Scheme != Uri.UriSchemeHttps && manifestUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new UpdateException("update_manifest_invalid", $"La URL del canal de actualización «{manifestUrl}» no es válida.");
        }

        if (manifestUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new UpdateException("update_https_required", "La URL del canal de actualización debe ser HTTPS (S-03): sin excepción, porque de ahí baja un ejecutable.");
        }

        // --- manifiesto: descargar y verificar firma + semántica -----------------------------
        var json = await DownloadTextAsync(manifestUri, cancellationToken).ConfigureAwait(false);
        var manifest = UpdateManifest.ParseAndVerify(json, _verifier, _options.PublicKey());
        _log?.Invoke($"Manifiesto de actualización verificado: versión {manifest.Version} (mínimo soportado {manifest.MinSupported}).");

        // --- versiones: sin downgrade, sin saltos imposibles ---------------------------------
        if (manifest.ParsedVersion <= currentVersion)
        {
            _log?.Invoke($"Sin actualización: la versión del canal ({manifest.Version}) no supera a la actual ({currentVersion}).");
            return null;
        }

        if (manifest.ParsedMinSupported > currentVersion)
        {
            throw new UpdateException(
                "update_min_supported",
                $"La versión actual ({currentVersion}) es anterior al mínimo que el canal puede actualizar ({manifest.MinSupported}): reinstala manualmente con el MSI completo.");
        }

        // --- instalador: descarga con hash en streaming + verificación -----------------------
        if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var installerUri) || installerUri.Scheme != Uri.UriSchemeHttps)
        {
            // Ya validado en ParseAndVerify; defensa en profundidad por si el llamador salta el paso.
            throw new UpdateException("update_https_required", $"La URL del instalador «{manifest.Url}» no es HTTPS (S-03).");
        }

        var (installerPath, sha256) = await DownloadInstallerAsync(installerUri, manifest, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(installerPath);
            throw new UpdateException(
                "update_hash_mismatch",
                $"El SHA-256 del instalador descargado ({sha256}) no coincide con el del manifiesto firmado ({manifest.Sha256}). Se descarta.");
        }

        _log?.Invoke($"Instalador verificado ({manifest.Version}): SHA-256 correcto, guardado temporalmente en {installerPath}.");
        return new PreparedUpdate { Manifest = manifest, InstallerPath = installerPath };
    }

    // ------------------------------------------------------------------------------- helpers

    private async Task<string> DownloadTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > _options.MaxManifestBytes)
                {
                    throw new UpdateException("update_too_large", $"El manifiesto de actualización excede {_options.MaxManifestBytes} bytes; se descarta.");
                }

                buffer.Write(chunk, 0, read);
            }

            return new UTF8Encoding(false).GetString(buffer.ToArray()).TrimStart('\uFEFF');
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("update_download_failed", $"No se pudo descargar el manifiesto de actualización: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("update_download_failed", "Se agotó el tiempo de espera descargando el manifiesto de actualización.");
        }
    }

    private async Task<(string Path, string Sha256)> DownloadInstallerAsync(Uri uri, UpdateManifest manifest, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orvitaprint-update");
        Directory.CreateDirectory(directory);
        var installerPath = System.IO.Path.Combine(
            directory,
            $"orvitaprint-{SanitizeFileName(manifest.Version)}-{Guid.NewGuid():N}.msi");

        try
        {
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = File.Create(installerPath))
            {
                var chunk = new byte[1024 * 64];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > _options.MaxInstallerBytes)
                    {
                        throw new UpdateException("update_too_large", $"El instalador excede el límite de {_options.MaxInstallerBytes} bytes; se descarta.");
                    }

                    hash.AppendData(chunk.AsSpan(0, read));
                    await target.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return (installerPath, sha256);
        }
        catch (UpdateException)
        {
            TryDelete(installerPath);
            throw;
        }
        catch (HttpRequestException ex)
        {
            TryDelete(installerPath);
            throw new UpdateException("update_download_failed", $"No se pudo descargar el instalador {manifest.Version}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(installerPath);
            throw new UpdateException("update_download_failed", "Se agotó el tiempo de espera descargando el instalador.");
        }
        catch (IOException)
        {
            TryDelete(installerPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Temporal huérfano: el propio directorio temporal se limpia con el sistema.
        }
    }

    private static string SanitizeFileName(string version) =>
        string.Join(string.Empty, version.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
}
