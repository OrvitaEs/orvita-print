using System.Net;
using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>
/// <see cref="UpdateChecker"/> con un HttpClient falso (sin red): validaciones EN ORDEN y en
/// cortocircuito — canal desactivado, URL HTTPS (S-03), firma del manifiesto, versiones
/// (sin downgrade, min_supported), hash SHA-256 del MSI descargado en streaming y límites de
/// tamaño. En cualquier fallo: el instalador temporal queda eliminado y no se aplica nada.
/// </summary>
public sealed class UpdateCheckerTests : IDisposable
{
    private static readonly byte[] TestSeed = Ed25519Tests.HexToBytes(
        "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");

    private static readonly byte[] TestPublicKey = Ed25519.PublicKeyFromSeed(TestSeed);

    private const string ManifestUrl = "https://updates.orvita.example/agent/latest.json";
    private const string InstallerUrl = "https://updates.orvita.example/agent/OrvitaPrint-1.2.0.msi";

    private readonly string _tempDirectory;

    public UpdateCheckerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"orp-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] InstallerBytes(int size = 2048)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private string SignedManifest(byte[] installer, string version = "1.2.0", string minSupported = "1.0.0", string? url = null) =>
        UpdateManifest.BuildSignedJson(
            version,
            url ?? InstallerUrl,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(installer)).ToLowerInvariant(),
            minSupported,
            TestSeed);

    /// <summary>Handler falso: sirve el manifiesto y el instalador y cuenta las peticiones.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public List<string> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            RequestedUris.Add(request.RequestUri!.ToString());
            return Task.FromResult(responder(request));
        }
    }

    private UpdateChecker CreateChecker(FakeHandler handler, Action<UpdateCheckerOptions>? configure = null)
    {
        var options = new UpdateCheckerOptions
        {
            ManifestUrl = ManifestUrl,
            PublicKeyBase64 = Convert.ToBase64String(TestPublicKey),
        };
        configure?.Invoke(options);
        return new UpdateChecker(options, verifier: new Ed25519Verifier(), httpClient: new HttpClient(handler));
    }

    [Fact]
    public async Task Camino_feliz_descarga_y_verifica_el_instalador()
    {
        var installer = InstallerBytes();
        var manifest = SignedManifest(installer);
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith("latest.json")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) });
        var checker = CreateChecker(handler);

        var result = await checker.CheckAsync(new Version(1, 1, 0), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("1.2.0", result.Manifest.Version);
        Assert.Equal(2, handler.Requests); // manifiesto + instalador
        Assert.True(File.Exists(result.InstallerPath));
        Assert.Equal(installer.Length, new FileInfo(result.InstallerPath).Length);
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(result.InstallerPath))).ToLowerInvariant(),
            result.Manifest.Sha256);
        File.Delete(result.InstallerPath);
    }

    [Fact]
    public async Task Version_igual_o_inferior_no_descarga_nada()
    {
        var installer = InstallerBytes();
        var manifest = SignedManifest(installer, version: "1.1.0");
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) });
        var checker = CreateChecker(handler);

        Assert.Null(await checker.CheckAsync(new Version(1, 1, 0), CancellationToken.None));
        Assert.Null(await checker.CheckAsync(new Version(2, 0, 0), CancellationToken.None));
        Assert.Equal(2, handler.Requests); // solo manifiestos, nunca el instalador
    }

    [Fact]
    public async Task Version_actual_inferior_al_minimo_soportado_rechazada()
    {
        var installer = InstallerBytes();
        // Coherente para el canal (2.0.0 ≥ 1.5.0) pero la versión ACTUAL (1.4.9) queda por debajo
        // del mínimo: el canal no da saltos desde tan viejo → reinstalación manual.
        var manifest = SignedManifest(installer, version: "2.0.0", minSupported: "1.5.0");
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) });
        var checker = CreateChecker(handler);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.CheckAsync(new Version(1, 4, 9), CancellationToken.None));
        Assert.Equal("update_min_supported", ex.Code);
        Assert.Equal(1, handler.Requests); // el manifiesto sí; el instalador nunca
    }

    [Fact]
    public async Task Firma_invalida_cortocircuita_antes_del_instalador()
    {
        var installer = InstallerBytes();
        var manifest = SignedManifest(installer);
        var tampered = manifest.Replace("\"version\":\"1.2.0\"", "\"version\":\"1.2.1\"");
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tampered) });
        var checker = CreateChecker(handler);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
        Assert.Equal("update_signature_invalid", ex.Code);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Hash_del_instalador_que_no_coincide_rechazado_y_temporal_eliminado()
    {
        var served = InstallerBytes();
        var manifest = SignedManifest(InstallerBytes()); // hash de OTRO contenido
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith("latest.json")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) });
        var checker = CreateChecker(handler);

        var updateDirectory = Path.Combine(Path.GetTempPath(), "orvitaprint-update");
        var filesBefore = Directory.Exists(updateDirectory) ? Directory.GetFiles(updateDirectory).Length : 0;

        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
        Assert.Equal("update_hash_mismatch", ex.Code);

        var filesAfter = Directory.GetFiles(updateDirectory).Length;
        Assert.Equal(filesBefore, filesAfter); // el temporal se eliminó
    }

    [Fact]
    public async Task Canal_http_rechazado_sin_ninguna_peticion_S03()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no debe haber red"));
        var options = new UpdateCheckerOptions
        {
            ManifestUrl = "http://updates.orvita.example/agent/latest.json",
            PublicKeyBase64 = Convert.ToBase64String(TestPublicKey),
        };
        var checker = new UpdateChecker(options, verifier: new Ed25519Verifier(), httpClient: new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
        Assert.Equal("update_https_required", ex.Code);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Instalador_http_rechazado_por_el_manifiesto_S03()
    {
        var installer = InstallerBytes();
        var manifest = SignedManifest(installer, url: "http://updates.orvita.example/agent/OrvitaPrint-1.2.0.msi");
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) });
        var checker = CreateChecker(handler);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
        Assert.Equal("update_https_required", ex.Code);
        Assert.Equal(1, handler.Requests); // el manifiesto baja; el instalador NO
    }

    [Fact]
    public async Task Canal_sin_configurar_devuelve_null_sin_red()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no debe haber red"));
        var checker = new UpdateChecker(
            new UpdateCheckerOptions { ManifestUrl = null, PublicKeyBase64 = Convert.ToBase64String(TestPublicKey) },
            verifier: new Ed25519Verifier(),
            httpClient: new HttpClient(handler));

        Assert.Null(await checker.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Manifiesto_demasiado_grande_rechazado()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 100)),
        });
        var checker = CreateChecker(handler, options => options.MaxManifestBytes = 10);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
        Assert.Equal("update_too_large", ex.Code);
    }

    [Fact]
    public async Task Instalador_que_excede_el_limite_rechazado()
    {
        var installer = InstallerBytes(size: 1000);
        var manifest = SignedManifest(installer);
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith("latest.json")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) });
        var checker = CreateChecker(handler, options => options.MaxInstallerBytes = 100);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
        Assert.Equal("update_too_large", ex.Code);
    }

    [Fact]
    public async Task Error_http_del_canal_mensaje_claro()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = CreateChecker(handler);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.CheckAsync(new Version(1, 0, 0), CancellationToken.None));
        Assert.Equal("update_download_failed", ex.Code);
    }
}
