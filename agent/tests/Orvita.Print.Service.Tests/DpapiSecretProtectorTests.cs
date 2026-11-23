using Orvita.Print.Core;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// DPAPI del token (S-01): roundtrip real (Windows: ProtectedData disponible), el texto
/// cifrado NO es el token, y lo corrupto/ilegible devuelve null sin lanzar. El token en claro
/// JAMÁS aparece en lo guardado (S-05 se verifica en los tests del handler/pipe).
/// </summary>
public sealed class DpapiSecretProtectorTests
{
    private readonly DpapiSecretProtector _protector = new();

    [Fact]
    public void Roundtrip_protege_y_recupera_el_token()
    {
        const string token = "orp_7f3a9c2e1b4d5f6a8b0c1d2e3f4a5b6c";
        var cipher = _protector.Protect(token);

        Assert.NotEqual(token, cipher); // el texto almacenado NO es el token (S-01)
        Assert.DoesNotContain("orp_", cipher);
        Assert.Equal(token, _protector.Unprotect(cipher));
    }

    [Fact]
    public void Dos_protecciones_del_mismo_token_son_legibles_aunque_difieran()
    {
        var cipherA = _protector.Protect("orp_token_de_prueba");
        var cipherB = _protector.Protect("orp_token_de_prueba");

        // DPAPI puede ser no determinista (aleatorio interno); lo que importa: ambos se leen.
        Assert.Equal("orp_token_de_prueba", _protector.Unprotect(cipherA));
        Assert.Equal("orp_token_de_prueba", _protector.Unprotect(cipherB));
    }

    [Fact]
    public void Unprotect_devuelve_null_ante_datos_corruptos_o_en_claro()
    {
        Assert.Null(_protector.Unprotect("orp_token_en_claro")); // no es base64 utilizable
        Assert.Null(_protector.Unprotect("AAAA")); // base64 válido pero no es un blob DPAPI
        Assert.Null(_protector.Unprotect(""));
    }

    [Fact]
    public void Protect_rechaza_vacio()
    {
        Assert.Throws<ArgumentException>(() => _protector.Protect(""));
        Assert.Throws<ArgumentException>(() => _protector.Protect("  "));
    }

    /// <summary>La interfaz vive en Core (ADR-12): el protector del Service la implementa.</summary>
    [Fact]
    public void Implementa_la_interfaz_portable_de_Core()
    {
        ISecretProtector protector = new DpapiSecretProtector();
        Assert.Equal("xyz", protector.Unprotect(protector.Protect("xyz")));
    }
}
