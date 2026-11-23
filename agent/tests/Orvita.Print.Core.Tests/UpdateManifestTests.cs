using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>
/// Convención de firma del manifiesto de actualización (F2-11, Doc/03 §10): Ed25519 sobre los
/// CUATRO campos firmados en orden canónico; el whitespace/orden del fichero publicado y los
/// campos extra quedan FUERA de la firma; toda alteración de un valor firmado la invalida.
/// Esta MISMA convención la seguirá el generador de F3-02/F3-03.
/// </summary>
public sealed class UpdateManifestTests
{
    /// <summary>Semilla de TEST 1 del RFC 8032: par de firmas SOLO para pruebas.</summary>
    private static readonly byte[] TestSeed = Ed25519Tests.HexToBytes(
        "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");

    private static readonly byte[] TestPublicKey = Ed25519.PublicKeyFromSeed(TestSeed);

    private const string Version = "1.2.0";
    private const string Url = "https://updates.orvita.example/agent/OrvitaPrint-1.2.0.msi";
    private const string Sha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string MinSupported = "1.0.0";

    private static UpdateManifest Verify(string json) =>
        UpdateManifest.ParseAndVerify(json, new Ed25519Verifier(), TestPublicKey);

    [Fact]
    public void Roundtrip_firmado_verificado()
    {
        var json = UpdateManifest.BuildSignedJson(Version, Url, Sha256, MinSupported, TestSeed);
        var manifest = Verify(json);

        Assert.Equal(Version, manifest.Version);
        Assert.Equal(Url, manifest.Url);
        Assert.Equal(Sha256, manifest.Sha256);
        Assert.Equal(MinSupported, manifest.MinSupported);
        Assert.Equal(new Version(1, 2, 0), manifest.ParsedVersion);
        Assert.Equal(new Version(1, 0, 0), manifest.ParsedMinSupported);
        Assert.Equal(new Uri(Url), manifest.ParsedUrl);
    }

    [Fact]
    public void Los_bytes_canonicos_son_deterministas_y_en_orden_fijo()
    {
        var first = UpdateManifest.BuildSignedJson(Version, Url, Sha256, MinSupported, TestSeed);
        var second = UpdateManifest.BuildSignedJson(Version, Url, Sha256, MinSupported, TestSeed);

        Assert.Equal(first, second); // firma determinista ⇒ json idéntico
        // Orden canónico: los cuatro campos firmados, signature al final.
        var position = 0;
        Assert.True((position = first.IndexOf("\"version\"", position, StringComparison.Ordinal)) >= 0);
        Assert.True((position = first.IndexOf("\"url\"", position, StringComparison.Ordinal)) >= 0);
        Assert.True((position = first.IndexOf("\"sha256\"", position, StringComparison.Ordinal)) >= 0);
        Assert.True((position = first.IndexOf("\"min_supported\"", position, StringComparison.Ordinal)) >= 0);
        Assert.True((position = first.IndexOf("\"signature\"", position, StringComparison.Ordinal)) >= 0);
    }

    [Fact]
    public void Verifica_acepta_whitespace_orden_y_campos_extra_del_fichero_publicado()
    {
        // El publicador reordena, mete saltos de línea y un campo de más: la firma sigue valiendo
        // porque se verifica la re-serialización CANÓNICA de los valores parseados.
        var signed = UpdateManifest.BuildSignedJson(Version, Url, Sha256, MinSupported, TestSeed);
        var signature = System.Text.Json.JsonDocument.Parse(signed).RootElement.GetProperty("signature").GetString();
        var messy = $$"""{"nota":"x","min_supported":"{{MinSupported}}","signature":"{{signature}}","sha256":"{{Sha256}}","url":"{{Url}}" , "version":"{{Version}}"}""";

        Assert.NotNull(Verify(messy));
    }

    [Fact]
    public void Alterar_un_campo_firmado_invalida_la_firma()
    {
        var signed = UpdateManifest.BuildSignedJson(Version, Url, Sha256, MinSupported, TestSeed);

        foreach (var (field, original, replacement) in new[]
                 {
                     ("version", Version, "9.9.9"),
                     ("url", Url, "https://updates.orvita.example/agent/OrvitaPrint-9.9.9.msi"),
                     ("sha256", Sha256, new string('a', 64)),
                     ("min_supported", MinSupported, "0.5.0"),
                 })
        {
            var edited = signed.Replace(
                $"\"{field}\":\"{original}\"",
                $"\"{field}\":\"{replacement}\"");
            Assert.NotEqual(signed, edited);

            var ex = Assert.Throws<UpdateException>(() => Verify(edited));
            Assert.Equal("update_signature_invalid", ex.Code);
        }
    }

    [Fact]
    public void Falta_un_campo_o_tipos_incorrectos_manifest_invalido()
    {
        Assert.Throws<UpdateException>(() => Verify("{}"));
        Assert.Equal("update_manifest_invalid", Assert.Throws<UpdateException>(() => Verify("{}")).Code);
        Assert.Equal("update_manifest_invalid", Assert.Throws<UpdateException>(() => Verify("no es json")).Code);
        Assert.Equal("update_manifest_invalid", Assert.Throws<UpdateException>(() => Verify("[1,2,3]")).Code);

        // version numérica (no string): rechazado antes de la firma.
        var ex = Assert.Throws<UpdateException>(() =>
            Verify("""{"version":12,"url":"https://x.test/a.msi","sha256":"x","min_supported":"1.0.0","signature":"AAAA"}"""));
        Assert.Equal("update_manifest_invalid", ex.Code);
    }

    [Theory]
    [InlineData("https://updates.orvita.example/agent/OrvitaPrint-1.2.0.msi", "update_signature_invalid", false)] // con firma buena vale
    [InlineData("http://updates.orvita.example/agent/OrvitaPrint-1.2.0.msi", "update_https_required", true)]      // S-03: sin excepción
    public void La_url_del_instalador_debe_ser_https(string url, string expectedCode, bool expectFailure)
    {
        var json = UpdateManifest.BuildSignedJson(Version, url, Sha256, MinSupported, TestSeed);

        if (expectFailure)
        {
            Assert.Equal(expectedCode, Assert.Throws<UpdateException>(() => Verify(json)).Code);
        }
        else
        {
            Assert.NotNull(Verify(json));
        }
    }

    [Theory]
    [InlineData("1", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("1.2.0-dev", false)]
    [InlineData("1.2", true)]
    [InlineData("1.2.0.1", true)]
    public void Las_versiones_se_parsean_estritamente(string candidate, bool valid)
    {
        var json = UpdateManifest.BuildSignedJson(candidate, Url, Sha256, candidate, TestSeed);

        if (valid)
        {
            Assert.NotNull(Verify(json));
        }
        else
        {
            Assert.Equal("update_manifest_invalid", Assert.Throws<UpdateException>(() => Verify(json)).Code);
        }
    }

    [Theory]
    [InlineData(63, false)]
    [InlineData(65, false)]
    [InlineData(64, true)]
    public void El_sha256_debe_ser_hex_de_64(int length, bool valid)
    {
        var sha = new string('a', length);
        var json = UpdateManifest.BuildSignedJson(Version, Url, sha, MinSupported, TestSeed);

        if (valid)
        {
            Assert.NotNull(Verify(json));
        }
        else
        {
            Assert.Equal("update_manifest_invalid", Assert.Throws<UpdateException>(() => Verify(json)).Code);
        }
    }

    [Fact]
    public void Firma_no_base64_o_de_otra_talla_rechazada()
    {
        var json = UpdateManifest.BuildSignedJson(Version, Url, Sha256, MinSupported, TestSeed);
        var badSignature = Convert.ToBase64String(new byte[63]);
        var edited = json[..json.LastIndexOf("\"signature\"", StringComparison.Ordinal)] +
                     $"\"signature\":\"{badSignature}\"}}";

        Assert.Equal("update_signature_invalid", Assert.Throws<UpdateException>(() => Verify(edited)).Code);
    }

    [Fact]
    public void El_sha256_del_manifiesto_se_normaliza_a_minusculas()
    {
        var upper = Sha256.ToUpperInvariant();
        var manifest = Verify(UpdateManifest.BuildSignedJson(Version, Url, upper, MinSupported, TestSeed));

        Assert.Equal(Sha256, manifest.Sha256);
    }
}
