using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>
/// Licencia ECDSA P-256 (F3-02, Doc/03 §10): firma sobre los CINCO campos firmados en orden
/// canónico; el whitespace/orden del fichero entregado y los campos extra quedan FUERA de la
/// firma; toda alteración de un valor firmado la invalida. <c>updates_until</c> pasado NO
/// invalida la licencia (solo desactiva actualizaciones). La MISMA convención la sigue la
/// herramienta de release (<c>orvita-release genlicense</c>).
/// </summary>
public sealed class LicenseDocumentTests
{
    /// <summary>Par de prueba EPHEMERO: se genera en memoria, nada de release en los tests.</summary>
    private static readonly ECDsa TestKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static readonly ECDsa OtherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private const string LicenseId = "orp_ab12cd34ef56ab12cd34ef56ab12cd34";
    private const string Sku = "10";
    private const int MaxLocations = 10;
    private const string UpdatesUntil = "2027-11-19";
    private const string Customer = "Pizzería Río Rosso";

    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    private static LicenseDocument Verify(string json, DateTimeOffset? now = null)
    {
        var license = LicenseDocument.ParseAndVerify(json, TestKey);
        Assert.False(license.UpdatesExpired(now ?? Now), "La licencia no debía estar expirada en este caso.");
        return license;
    }

    private static string Sign(string licenseId, string sku, int maxLocations, string updatesUntil, string customer, ECDsa? key = null)
    {
        key ??= TestKey;
        // Bytes canónicos REPLICANDO la convención documentada (JSON compacto UTF-8, escape mínimo,
        // orden fijo) — exactamente lo que hace LicenseDocument.CanonicalJsonBytes.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Indented = false,
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            writer.WriteStartObject();
            writer.WriteString("license_id", licenseId);
            writer.WriteString("sku", sku);
            writer.WriteNumber("max_locations", maxLocations);
            writer.WriteString("updates_until", updatesUntil);
            writer.WriteString("customer", customer);
            writer.WriteEndObject();
        }

        var signature = key.SignData(stream.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Encoding.UTF8.GetString(stream.ToArray())[..^1] +
               ",\"signature\":" + JsonSerializer.Serialize(Convert.ToBase64String(signature), CanonicalOptions) + "}";
    }

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ----------------------------------------------------------------------------- happy path

    [Fact]
    public void Roundtrip_firmado_verificado()
    {
        var json = LicenseDocument.BuildSignedJson(LicenseId, Sku, MaxLocations, UpdatesUntil, Customer, TestKey);
        var license = Verify(json);

        Assert.Equal(LicenseId, license.LicenseId);
        Assert.Equal(Sku, license.Sku);
        Assert.Equal(MaxLocations, license.MaxLocations);
        Assert.Equal(UpdatesUntil, license.UpdatesUntil);
        Assert.Equal(Customer, license.Customer);
        Assert.Equal(new DateOnly(2027, 11, 19), license.ParsedUpdatesUntil);
        Assert.False(license.UpdatesExpired(Now));
    }

    [Fact]
    public void Los_bytes_canonicos_son_deterministas_y_en_orden_fijo()
    {
        // OJO: ECDSA NO es determinista (k aleatorio): dos firmas del mismo contenido son distintas
        // PERO ambas verifican (al contrario que Ed25519, determinista por diseño).
        var first = LicenseDocument.BuildSignedJson(LicenseId, Sku, MaxLocations, UpdatesUntil, Customer, TestKey);
        var second = LicenseDocument.BuildSignedJson(LicenseId, Sku, MaxLocations, UpdatesUntil, Customer, TestKey);

        _ = Verify(first);
        _ = Verify(second);
        // El prefijo canónico (los cinco campos firmados en orden fijo) es idéntico en ambos;
        // solo la signature final puede variar.
        var canonicalOf = new Func<string, string>(json => json[..json.IndexOf(",\"signature\":", StringComparison.Ordinal)]);
        Assert.Equal(canonicalOf(first), canonicalOf(second));
        // Orden canónico: los cinco campos firmados, signature al final.
        var position = 0;
        Assert.True((position = first.IndexOf("\"license_id\"", position, StringComparison.Ordinal)) >= 0);
        Assert.True((position = first.IndexOf("\"sku\"", position, StringComparison.Ordinal)) >= 0);
        Assert.True((position = first.IndexOf("\"max_locations\"", position, StringComparison.Ordinal)) >= 0);
        Assert.True((position = first.IndexOf("\"updates_until\"", position, StringComparison.Ordinal)) >= 0);
        Assert.True((position = first.IndexOf("\"customer\"", position, StringComparison.Ordinal)) >= 0);
        Assert.True((position = first.IndexOf("\"signature\"", position, StringComparison.Ordinal)) >= 0);
    }

    [Fact]
    public void Verifica_acepta_whitespace_orden_y_campos_extra_del_fichero_entregado()
    {
        // El generador firma canónico; el fichero puede llegar reformateado. Re-serializado con
        // otro orden, indentación y un campo extra: los VALORES firmados son los mismos.
        var json = """
            {
              "nota": "entregado con la compra",
              "customer": "Pizzería Río Rosso",
              "updates_until": "2027-11-19",
              "sku": "10",
              "max_locations": 10,
              "extra": {"anidado": true},
              "license_id": "orp_ab12cd34ef56ab12cd34ef56ab12cd34",
              "signature": "<se-sustituye>"
            }
            """;
        // Reconstruimos el objeto con la firma correcta de los valores canónicos (los acentos van
        // literales en la convención de escape mínimo — el fichero entregado puede venir reformateado).
        var canonical = Sign(LicenseId, Sku, MaxLocations, UpdatesUntil, Customer); // firma + firma-json con los valores canónicos
        var signature = JsonDocument.Parse(canonical).RootElement.GetProperty("signature").GetString();
        json = json.Replace("<se-sustituye>", signature, StringComparison.Ordinal);

        var license = Verify(json);
        Assert.Equal(MaxLocations, license.MaxLocations);
    }

    [Fact]
    public void Los_acentos_viajan_como_utf8_real_y_verifican()
    {
        // Convención: escapado mínimo (UnsafeRelaxedJsonEscaping) — «Río» va literal, no \u00ed.
        var json = LicenseDocument.BuildSignedJson(LicenseId, "unlimited", 0, UpdatesUntil, "Pizzería Río Rosso — Ñ", TestKey);
        Assert.Contains("Pizzería Río Rosso — Ñ", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u00", json, StringComparison.Ordinal);

        var license = Verify(json);
        Assert.Equal("Pizzería Río Rosso — Ñ", license.Customer);
    }

    // ----------------------------------------------------------------- firmas que NO verifican

    [Fact]
    public void Un_campo_alterado_invalida_la_firma()
    {
        var json = LicenseDocument.BuildSignedJson(LicenseId, Sku, MaxLocations, UpdatesUntil, Customer, TestKey);
        var tampered = json.Replace("\"sku\":\"10\"", "\"sku\":\"unlimited\"", StringComparison.Ordinal);

        var exception = Assert.Throws<LicenseException>(() => LicenseDocument.ParseAndVerify(tampered, TestKey));
        Assert.Equal("license_signature_invalid", exception.Code);
    }

    [Fact]
    public void Una_firma_de_otra_clave_no_verifica()
    {
        var json = Sign(LicenseId, Sku, MaxLocations, UpdatesUntil, Customer, OtherKey);

        var exception = Assert.Throws<LicenseException>(() => LicenseDocument.ParseAndVerify(json, TestKey));
        Assert.Equal("license_signature_invalid", exception.Code);
    }

    [Theory]
    [InlineData("no-es-base64!!")]
    [InlineData("AAAA")]
    public void Una_firma_malformada_es_license_signature_invalid(string signature)
    {
        var json = "{\"license_id\":\"x\",\"sku\":\"1\",\"max_locations\":1,\"updates_until\":\"2027-01-01\"," +
                   $"\"customer\":\"C\",\"signature\":\"{signature}\"}}";

        var exception = Assert.Throws<LicenseException>(() => LicenseDocument.ParseAndVerify(json, TestKey));
        Assert.Equal("license_signature_invalid", exception.Code);
    }

    // ------------------------------------------------------------------ formato y semántica

    [Theory]
    [InlineData("license_id")]
    [InlineData("sku")]
    [InlineData("updates_until")]
    [InlineData("customer")]
    [InlineData("signature")]
    public void Falta_un_campo_string_es_license_invalid_format(string field)
    {
        var json = field switch
        {
            "license_id" => "{\"sku\":\"1\",\"max_locations\":1,\"updates_until\":\"2027-01-01\",\"customer\":\"C\",\"signature\":\"AAAA\"}",
            "sku" => "{\"license_id\":\"x\",\"max_locations\":1,\"updates_until\":\"2027-01-01\",\"customer\":\"C\",\"signature\":\"AAAA\"}",
            "updates_until" => "{\"license_id\":\"x\",\"sku\":\"1\",\"max_locations\":1,\"customer\":\"C\",\"signature\":\"AAAA\"}",
            "customer" => "{\"license_id\":\"x\",\"sku\":\"1\",\"max_locations\":1,\"updates_until\":\"2027-01-01\",\"signature\":\"AAAA\"}",
            _ => "{\"license_id\":\"x\",\"sku\":\"1\",\"max_locations\":1,\"updates_until\":\"2027-01-01\",\"customer\":\"C\"}",
        };

        var exception = Assert.Throws<LicenseException>(() => LicenseDocument.ParseAndVerify(json, TestKey));
        Assert.Equal("license_invalid_format", exception.Code);
        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Max_locations_no_entero_es_license_invalid_format()
    {
        var json = "{\"license_id\":\"x\",\"sku\":\"1\",\"max_locations\":\"1\",\"updates_until\":\"2027-01-01\",\"customer\":\"C\",\"signature\":\"AAAA\"}";

        var exception = Assert.Throws<LicenseException>(() => LicenseDocument.ParseAndVerify(json, TestKey));
        Assert.Equal("license_invalid_format", exception.Code);
    }

    [Fact]
    public void Updates_until_con_formato_incorrecto_es_license_invalid_format()
    {
        // Firmado correctamente pero con fecha no ISO: la semántica va DESPUÉS de la firma.
        var json = Sign(LicenseId, Sku, MaxLocations, "19/11/2027", Customer);

        var exception = Assert.Throws<LicenseException>(() => LicenseDocument.ParseAndVerify(json, TestKey));
        Assert.Equal("license_invalid_format", exception.Code);
        Assert.Contains("updates_until", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Max_locations_negativo_firmado_es_license_invalid_format()
    {
        var json = Sign(LicenseId, Sku, -1, UpdatesUntil, Customer);

        var exception = Assert.Throws<LicenseException>(() => LicenseDocument.ParseAndVerify(json, TestKey));
        Assert.Equal("license_invalid_format", exception.Code);
    }

    [Fact]
    public void Json_que_no_es_un_objeto_es_license_invalid_format()
    {
        var exception = Assert.Throws<LicenseException>(() => LicenseDocument.ParseAndVerify("[]", TestKey));
        Assert.Equal("license_invalid_format", exception.Code);
    }

    [Fact]
    public void Json_roto_es_license_invalid_format()
    {
        var exception = Assert.Throws<LicenseException>(() => LicenseDocument.ParseAndVerify("{no-soy-json", TestKey));
        Assert.Equal("license_invalid_format", exception.Code);
    }

    // ------------------------------------------------- updates_until pasado (Doc/03 §10)

    [Fact]
    public void Updates_until_pasado_sigue_siendo_licencia_valida_solo_expira_actualizaciones()
    {
        var json = LicenseDocument.BuildSignedJson(LicenseId, Sku, MaxLocations, "2026-11-22", Customer, TestKey);
        var license = LicenseDocument.ParseAndVerify(json, TestKey); // NO lanza: la licencia vale

        Assert.True(license.UpdatesExpired(Now));
        Assert.True(license.UpdatesExpired(new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Updates_until_es_el_ultimo_dia_incluido()
    {
        var json = LicenseDocument.BuildSignedJson(LicenseId, Sku, MaxLocations, "2026-11-23", Customer, TestKey);
        var license = LicenseDocument.ParseAndVerify(json, TestKey);

        // El propio día updates_until TODAVÍA acepta actualizaciones; al día siguiente ya no.
        Assert.False(license.UpdatesExpired(new DateTimeOffset(2026, 11, 23, 23, 59, 59, TimeSpan.Zero)));
        Assert.True(license.UpdatesExpired(new DateTimeOffset(2026, 11, 24, 0, 0, 1, TimeSpan.Zero)));
    }

    [Fact]
    public void Max_locations_cero_es_ilimitado_y_valido()
    {
        var json = LicenseDocument.BuildSignedJson(LicenseId, "unlimited", 0, UpdatesUntil, Customer, TestKey);
        var license = Verify(json);

        Assert.Equal(0, license.MaxLocations);
        Assert.False(license.UpdatesExpired(Now));
    }
}
