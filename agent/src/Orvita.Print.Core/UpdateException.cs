namespace Orvita.Print.Core;

/// <summary>
/// Fallo del canal de actualización (F2-11). El <see cref="Code"/> es una categoría estable
/// (snake_case, como los códigos de la pipe de gestión): aparece en el log del servicio y en
/// el informe de diagnóstico. En CUALQUIER caso la regla es la de Doc/03 §10: si algo falla,
/// se conserva la versión actual — nunca se acepta una actualización sin firma válida.
/// </summary>
public sealed class UpdateException(string code, string message) : Exception(message)
{
    /// <summary>Categoría del fallo: update_https_required, update_signature_invalid,
    /// update_hash_mismatch, update_downgrade, update_min_supported, update_too_large,
    /// update_download_failed, update_manifest_invalid, update_msiexec_failed…</summary>
    public string Code { get; } = code;
}
