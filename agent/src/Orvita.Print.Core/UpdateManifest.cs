using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Orvita.Print.Core;

/// <summary>
/// Manifiesto de actualización del agente (Doc/03 §10, <c>latest.json</c>):
/// <c>{ "version", "url", "sha256", "min_supported", "signature" }</c>.
///
/// <para><b>Convención de firma (F2-11; la MISMA seguirá el generador F3-02/F3-03):</b></para>
/// <para>La firma Ed25519 cubre los CUATRO campos firmados — <c>version</c>, <c>url</c>,
/// <c>sha256</c> y <c>min_supported</c>, en ESE orden, excluyendo <c>signature</c> y cualquier
/// otro campo — serializados canónicamente: JSON compacto UTF-8 sin BOM, escapado mínimo
/// (<c>UnsafeRelaxedJsonEscaping</c>), propiedades siempre en el orden fijado por
/// <see cref="CanonicalJsonBytes"/>. Ni el whitespace, ni el orden de las propiedades del
/// fichero publicado, ni campos adicionales forman parte de la firma: el verificador
/// re-serializa canónicamente los valores PARSEADOS antes de verificar, de modo que la
/// convención no depende de cómo se sirva el fichero. <c>signature</c> es base64 estándar de
/// los 64 bytes de Ed25519 y va SIEMPRE en el objeto raíz. La clave pública (32 bytes en
/// crudo, base64) va EMBEBIDA en el agente, nunca en el manifiesto.</para>
/// </summary>
public sealed record UpdateManifest
{
    public required string Version { get; init; }

    public required string Url { get; init; }

    /// <summary>SHA-256 del MSI en hexadecimal minúsculas (64 caracteres).</summary>
    public required string Sha256 { get; init; }

    public required string MinSupported { get; init; }

    /// <summary>Firma Ed25519 de los bytes canónicos, base64 estándar (64 bytes decodificados).</summary>
    public required string Signature { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Version ParsedVersion { get; private init; } = new(0, 0, 0);

    [System.Text.Json.Serialization.JsonIgnore]
    public Version ParsedMinSupported { get; private init; } = new(0, 0, 0);

    public Uri ParsedUrl { get; private init; } = new("https://invalid.invalid/", UriKind.Absolute);

    /// <summary>Serialización canónica de los campos firmados (lo que verifica la firma).</summary>
    public byte[] CanonicalJsonBytes() => CanonicalJsonBytes(Version, Url, Sha256, MinSupported);

    // ------------------------------------------------------------------ parseo y verificación

    /// <summary>
    /// Parsea y verifica un <c>latest.json</c>. EN ORDEN y en cortocircuito:
    /// 1) JSON bien formado y con los tipos correctos (los cuatro campos firmados string);
    /// 2) firma Ed25519 sobre los bytes canónicos de los campos parseados;
    /// 3) semántica: URL HTTPS absoluta (S-03 — también para la URL del MSI), sha256 hex de 64,
    /// versiones parseables y <c>min_supported ≤ version</c>.
    /// Campos desconocidos en el objeto raíz: se IGNORAN (no se firman, no se usan).
    /// Lanza <see cref="UpdateException"/> con el código del primer fallo.
    /// </summary>
    public static UpdateManifest ParseAndVerify(string json, ISignatureVerifier verifier, ReadOnlySpan<byte> publicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(verifier);

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new UpdateException("update_manifest_invalid", $"El manifiesto de actualización no es JSON válido: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new UpdateException("update_manifest_invalid", "El manifiesto de actualización debe ser un objeto JSON.");
        }

        var version = GetStringField(root, "version");
        var url = GetStringField(root, "url");
        var sha256 = GetStringField(root, "sha256");
        var minSupported = GetStringField(root, "min_supported");
        var signature = GetStringField(root, "signature");

        // 2. Firma (antes que la semántica: se verifica lo firmado, sean los valores válidos o no).
        byte[] signatureBytes;
        try
        {
            if (!Convert.TryFromBase64Chars(signature.ToCharArray(), signatureBytes = new byte[Ed25519.SignatureSize], out var written) ||
                written != Ed25519.SignatureSize)
            {
                throw new FormatException($"La firma decodifica {written} bytes (se esperaban {Ed25519.SignatureSize}).");
            }
        }
        catch (FormatException ex)
        {
            throw new UpdateException("update_signature_invalid", $"La firma del manifiesto no es base64 de {Ed25519.SignatureSize} bytes: {ex.Message}");
        }

        var canonical = CanonicalJsonBytes(version, url, sha256, minSupported);
        if (!verifier.Verify(publicKey, canonical, signatureBytes))
        {
            throw new UpdateException("update_signature_invalid", "La firma Ed25519 del manifiesto de actualización NO es válida. Se conserva la versión actual (Doc/03 §10).");
        }

        // 3. Semántica de los campos ya autenticados.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) || parsedUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new UpdateException("update_https_required", $"La URL del instalador «{url}» no es HTTPS (S-03, Doc/03 §8): se rechaza la actualización.");
        }

        if (!TryParseStrictVersion(version, out var parsedVersion))
        {
            throw new UpdateException("update_manifest_invalid", $"La versión del manifiesto «{version}» no es válida (se esperan 2–4 partes numéricas).");
        }

        if (!TryParseStrictVersion(minSupported, out var parsedMinSupported))
        {
            throw new UpdateException("update_manifest_invalid", $"La versión mínima soportada «{minSupported}» no es válida (se esperan 2–4 partes numéricas).");
        }

        if (parsedMinSupported > parsedVersion)
        {
            throw new UpdateException("update_manifest_invalid", "El manifiesto es incoherente: min_supported es mayor que la versión que anuncia.");
        }

        if (sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new UpdateException("update_manifest_invalid", "El campo sha256 debe ser el hash hexadecimal (64 caracteres) del instalador.");
        }

        return new UpdateManifest
        {
            Version = version,
            Url = url,
            Sha256 = sha256.ToLowerInvariant(),
            MinSupported = minSupported,
            Signature = signature,
            ParsedVersion = parsedVersion,
            ParsedMinSupported = parsedMinSupported,
            ParsedUrl = parsedUrl,
        };
    }

    /// <summary>
    /// Construye un <c>latest.json</c> FIRMADO (para las pruebas y para el generador de F3-03).
    /// El fichero publicado lleva los campos firmados en orden canónico y <c>signature</c> al final.
    /// </summary>
    public static string BuildSignedJson(string version, string url, string sha256, string minSupported, byte[] privateKeySeed)
    {
        ArgumentNullException.ThrowIfNull(privateKeySeed);
        var canonical = CanonicalJsonBytes(version, url, sha256, minSupported);
        var signature = Ed25519.Sign(privateKeySeed, canonical);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            WriteStringField(writer, "version", version);
            WriteStringField(writer, "url", url);
            WriteStringField(writer, "sha256", sha256);
            WriteStringField(writer, "min_supported", minSupported);
            WriteStringField(writer, "signature", Convert.ToBase64String(signature));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Bytes canónicos de los campos firmados (definición de la convención, ver cabecera).</summary>
    internal static byte[] CanonicalJsonBytes(string version, string url, string sha256, string minSupported)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            WriteStringField(writer, "version", version);
            WriteStringField(writer, "url", url);
            WriteStringField(writer, "sha256", sha256);
            WriteStringField(writer, "min_supported", minSupported);
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
            throw new UpdateException("update_manifest_invalid", $"Falta el campo «{name}» (string) en el manifiesto de actualización.");
        }

        return value.GetString() ?? string.Empty;
    }

    /// <summary>Versión estricta: 2–4 partes numéricas («1.2», «1.2.0», «1.2.0.1»); sin prerelease.</summary>
    private static bool TryParseStrictVersion(string value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value) || !System.Version.TryParse(value, out var parsed) || parsed.Major < 0)
        {
            return false;
        }

        var parts = value.Split('.');
        if (parts.Length is < 2 or > 4 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)))
        {
            return false;
        }

        version = parsed;
        return true;
    }
}
