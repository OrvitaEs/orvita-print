using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orvita.Print.Core;

/// <summary>
/// Licencia del agente (Doc/03 §10 y §8): fichero <c>license.dat</c> — JSON con
/// <c>{ "license_id", "sku", "max_locations", "updates_until", "customer", "signature" }</c> —
/// firmado con <b>ECDSA P-256 (SHA-256)</b>. Validación LOCAL con la clave pública EMBEBIDA
/// (<see cref="LicenseTrust"/>): sin conexión NUNCA.
///
/// <para><b>Convención de firma (la MISMA disciplina que <see cref="UpdateManifest"/>):</b></para>
/// <para>La firma cubre los CINCO campos firmados — <c>license_id</c>, <c>sku</c>,
/// <c>max_locations</c>, <c>updates_until</c> y <c>customer</c>, en ESE orden, excluyendo
/// <c>signature</c> y cualquier otro campo — serializados canónicamente: JSON compacto UTF-8 sin
/// BOM, escapado mínimo (<c>UnsafeRelaxedJsonEscaping</c>), <c>max_locations</c> como número y
/// propiedades siempre en el orden fijado por <see cref="CanonicalJsonBytes"/>. Ni el whitespace,
/// ni el orden de las propiedades del fichero entregado, ni campos adicionales forman parte de la
/// firma: el verificador re-serializa canónicamente los valores PARSEADOS antes de verificar.
/// <c>signature</c> es base64 estándar de los 64 bytes r||s (IEEE P1363, concatenación fija) de la firma ECDSA.</para>
///
/// <para>Reglas semánticas (Doc/03 §10): si <c>updates_until</c> ya pasó, la licencia SIGUE siendo
/// válida — el agente funciona y solo deja de aceptar actualizaciones automáticas (el Service lo
/// consulta antes de comprobar el canal). <c>max_locations = 0</c> significa ilimitado (SKU 549 €);
/// su aplicación comercial vive en el servidor (el agente la informa en <c>status</c>/<c>doctor</c>).</para>
/// </summary>
public sealed record LicenseDocument
{
    public required string LicenseId { get; init; }

    /// <summary>SKU comercial: «1» (1 local), «10» (10 locales) o «unlimited» (ilimitado + white-label).</summary>
    public required string Sku { get; init; }

    /// <summary>Máximo de locales (0 = ilimitado). Lo aplica el servidor; el agente lo informa.</summary>
    public required int MaxLocations { get; init; }

    /// <summary>Fecha ISO <c>yyyy-MM-dd</c> hasta la que se aceptan actualizaciones (último día incluido).</summary>
    public required string UpdatesUntil { get; init; }

    public required string Customer { get; init; }

    /// <summary>Firma ECDSA P-256 (SHA-256, r||s 64 bytes) de los bytes canónicos, base64 estándar.</summary>
    public required string Signature { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public DateOnly ParsedUpdatesUntil { get; private init; }

    /// <summary>Serialización canónica de los campos firmados (lo que verifica la firma).</summary>
    public byte[] CanonicalJsonBytes() =>
        CanonicalJsonBytes(LicenseId, Sku, MaxLocations, UpdatesUntil, Customer);

    /// <summary>true si <paramref name="nowUtc"/> es posterior al último día con actualizaciones.</summary>
    public bool UpdatesExpired(DateTimeOffset nowUtc) => nowUtc.UtcDateTime.Date > ParsedUpdatesUntil.ToDateTime(TimeOnly.MinValue);

    // ------------------------------------------------------------------ parseo y verificación

    /// <summary>
    /// Parsea y verifica un <c>license.dat</c>. EN ORDER y en cortocircuito:
    /// 1) JSON bien formado y con los tipos correctos;
    /// 2) firma ECDSA P-256 sobre los bytes canónicos de los campos parseados;
    /// 3) semántica: campos no vacíos, <c>max_locations ≥ 0</c> y <c>updates_until</c> con formato
    /// <c>yyyy-MM-dd</c> estricto.
    /// Campos desconocidos en el objeto raíz: se IGNORAN (no se firman, no se usan).
    /// Lanza <see cref="LicenseException"/> con el código del primer fallo.
    /// </summary>
    public static LicenseDocument ParseAndVerify(string json, ECDsa publicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(publicKey);

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new LicenseException("license_invalid_format", $"La licencia no es JSON válido: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new LicenseException("license_invalid_format", "La licencia debe ser un objeto JSON.");
        }

        var licenseId = GetStringField(root, "license_id");
        var sku = GetStringField(root, "sku");
        var maxLocations = GetIntField(root, "max_locations");
        var updatesUntil = GetStringField(root, "updates_until");
        var customer = GetStringField(root, "customer");
        var signature = GetStringField(root, "signature");

        // 2. Firma (antes que la semántica: se verifica lo firmado, sean los valores válidos o no).
        byte[] signatureBytes;
        try
        {
            if (!Convert.TryFromBase64Chars(signature.ToCharArray(), signatureBytes = new byte[64], out var written) ||
                written != 64)
            {
                throw new FormatException($"La firma decodifica {written} bytes (se esperaban 64).");
            }
        }
        catch (FormatException ex)
        {
            throw new LicenseException("license_signature_invalid", $"La firma de la licencia no es base64 de 64 bytes: {ex.Message}");
        }

        var canonical = CanonicalJsonBytes(licenseId, sku, maxLocations, updatesUntil, customer);
        if (!publicKey.VerifyData(canonical, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new LicenseException("license_signature_invalid",
                "La firma ECDSA de la licencia NO es válida. Comprueba que el fichero license.dat es el entregado con la compra (Doc/03 §10).");
        }

        // 3. Semántica de los campos ya autenticados.
        if (!TryParseStrictDate(updatesUntil, out var parsedUntil))
        {
            throw new LicenseException("license_invalid_format",
                $"El campo updates_until «{updatesUntil}» no es una fecha ISO yyyy-MM-dd.");
        }

        if (maxLocations < 0)
        {
            throw new LicenseException("license_invalid_format", "El campo max_locations no puede ser negativo (0 = ilimitado).");
        }

        return new LicenseDocument
        {
            LicenseId = licenseId,
            Sku = sku,
            MaxLocations = maxLocations,
            UpdatesUntil = updatesUntil,
            Customer = customer,
            Signature = signature,
            ParsedUpdatesUntil = parsedUntil,
        };
    }

    /// <summary>
    /// Construye un <c>license.dat</c> FIRMADO (para las herramientas de release F3-02 y las pruebas).
    /// El fichero entregado lleva los campos firmados en orden canónico y <c>signature</c> al final.
    /// </summary>
    public static string BuildSignedJson(string licenseId, string sku, int maxLocations, string updatesUntil, string customer, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        if (maxLocations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLocations), "max_locations no puede ser negativo (0 = ilimitado).");
        }

        var canonical = CanonicalJsonBytes(licenseId, sku, maxLocations, updatesUntil, customer);
        var signature = privateKey.SignData(canonical, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            WriteStringField(writer, "license_id", licenseId);
            WriteStringField(writer, "sku", sku);
            writer.WriteNumber("max_locations", maxLocations);
            WriteStringField(writer, "updates_until", updatesUntil);
            WriteStringField(writer, "customer", customer);
            WriteStringField(writer, "signature", Convert.ToBase64String(signature));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Bytes canónicos de los campos firmados (definición de la convención, ver cabecera).</summary>
    internal static byte[] CanonicalJsonBytes(string licenseId, string sku, int maxLocations, string updatesUntil, string customer)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            WriteStringField(writer, "license_id", licenseId);
            WriteStringField(writer, "sku", sku);
            writer.WriteNumber("max_locations", maxLocations);
            WriteStringField(writer, "updates_until", updatesUntil);
            WriteStringField(writer, "customer", customer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // escapado mínimo y determinista
    };

    private static void WriteStringField(Utf8JsonWriter writer, string name, string value) =>
        writer.WriteString(name, value);

    private static string GetStringField(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new LicenseException("license_invalid_format", $"Falta el campo «{name}» (string) en la licencia.");
        }

        return value.GetString() ?? string.Empty;
    }

    private static int GetIntField(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
        {
            throw new LicenseException("license_invalid_format", $"Falta el campo «{name}» (número entero) en la licencia.");
        }

        return parsed;
    }

    /// <summary>Fecha estricta <c>yyyy-MM-dd</c> (invariante; sin hora ni zona).</summary>
    private static bool TryParseStrictDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
