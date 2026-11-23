namespace Orvita.Print.Core;

/// <summary>
/// Fallo de validación de la licencia (F3-02). El <see cref="Code"/> es una categoría estable
/// (snake_case, como los códigos de la pipe de gestión): license_invalid_format,
/// license_signature_invalid. Aparece en el log del servicio, en <c>status</c> y en el informe
/// del doctor. REGLA (Doc/03 §10): un fallo de licencia NO detiene al agente — el estado queda
/// visible y el emparejamiento/impresión siguen funcionando.
/// </summary>
public sealed class LicenseException(string code, string message) : Exception(message)
{
    /// <summary>Categoría del fallo: license_invalid_format, license_signature_invalid.</summary>
    public string Code { get; } = code;
}
