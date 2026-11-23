using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// Flujo completo de pair (F2-08 + F2-10) contra HTTP falso: 200 → almacén escrito con el
/// token CIFRADO (≠ token claro, S-01), transporte aplicado y worker despertado; 400/410/429
/// → error contractual y almacén intacto. S-03: http:// rechazado sin la bandera explícita.
/// </summary>
public sealed class PairingServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"orp-pair-{Guid.NewGuid():N}.db");
    private readonly FakeHttpHandler _httpHandler;
    private readonly List<IDisposable> _disposables = [];

    public PairingServiceTests()
    {
        _httpHandler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{ "token": "orp_token_secreto_123", "location": { "id": 2, "code": "pueblo-b", "name": "Pizzería Pueblo B" }, "poll_interval": 10 }""",
                Encoding.UTF8,
                "application/json"),
        });
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // El pool puede tardar un instante en soltar el fichero; limpieza de buena fe.
        }
    }

    private (PairingService Service, LocalConfigStore Store, ServerConnection Connection, PairingWaiter Waiter, AgentRuntimeStatus Runtime) Create()
    {
        var store = new LocalConfigStore(_databasePath);
        var connection = new ServerConnection(new AgentOptions(), NullLogger<ServerConnection>.Instance);
        _disposables.Add(store);
        _disposables.Add(connection);
        var waiter = new PairingWaiter();
        var runtime = new AgentRuntimeStatus();
        var service = new PairingService(
            connection,
            store,
            new DpapiSecretProtector(),
            waiter,
            runtime,
            new StaticDestinationProvider(),
            NullLogger<PairingService>.Instance,
            new HttpClient(_httpHandler));
        return (service, store, connection, waiter, runtime);
    }

    [Fact]
    public async Task Pair_200_guarda_token_cifrado_aplica_transporte_y_despierta_al_worker()
    {
        string? pairPath = null;
        string? pairBody = null;
        _httpHandler.Responder = request =>
        {
            pairPath = request.RequestUri!.AbsolutePath;
            // El contenido se dispone al responder: el cuerpo se lee DENTRO del handler.
            pairBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{ "token": "orp_token_secreto_123", "location": { "id": 2, "code": "pueblo-b", "name": "Pizzería Pueblo B" }, "poll_interval": 10 }""",
                    Encoding.UTF8,
                    "application/json"),
            };
        };

        var (service, store, connection, waiter, runtime) = Create();
        var workerDespierto = false;
        waiter.PairApplied += () => workerDespierto = true;

        var outcome = await service.ApplyPairingAsync(
            "https://tienda.example/wp-json/orp-print/v1", "482913", "TPV Mostrador", allowInsecureHttp: false, CancellationToken.None);

        // POST /pair con el cuerpo del contrato (Doc/04 §5.2).
        Assert.EndsWith("/pair", pairPath);
        Assert.Contains("\"code\":\"482913\"", pairBody);
        Assert.Contains("\"agent_name\":\"TPV Mostrador\"", pairBody);
        Assert.Contains("\"platform\":\"windows\"", pairBody);

        // Resultado legible para el CLI (sin token, S-05).
        Assert.Equal("https://tienda.example/wp-json/orp-print/v1/", outcome.ServerUrl);
        Assert.Equal(2, outcome.LocationId);
        Assert.Equal("Pizzería Pueblo B", outcome.LocationName);
        Assert.Equal(10, outcome.PollIntervalSeconds);

        // Almacén: URL + token CIFRADO (S-01); el claro no está en disco.
        var pairing = store.GetPairing();
        Assert.True(pairing.IsPaired);
        Assert.Equal("https://tienda.example/wp-json/orp-print/v1/", pairing.ServerUrl);
        Assert.NotEqual("orp_token_secreto_123", pairing.TokenProtected); // cifrado, nunca plano
        Assert.DoesNotContain("orp_token_secreto_123", pairing.TokenProtected);
        Assert.Equal(2, pairing.LocationId);
        Assert.Equal("TPV Mostrador", pairing.AgentName);

        // Transporte en memoria (el poller puede arrancar sin reiniciar) + señal al worker.
        Assert.NotNull(connection.Transport);
        Assert.Equal("https://tienda.example/wp-json/orp-print/v1/", connection.Transport!.BaseUrl.ToString());
        Assert.True(workerDespierto);
        Assert.Equal(10, runtime.PairPollIntervalSeconds);
    }

    [Fact]
    public async Task Pair_con_url_relacionada_sin_barra_final_la_normaliza()
    {
        var (service, store, _, _, _) = Create();
        var outcome = await service.ApplyPairingAsync(
            "https://tienda.example/wp-json/orp-print/v1", "482913", null, false, CancellationToken.None);

        Assert.EndsWith("/", outcome.ServerUrl);
        Assert.Equal("https://tienda.example/wp-json/orp-print/v1/", store.GetPairing().ServerUrl);
        Assert.Equal($"TPV {Environment.MachineName}", store.GetPairing().AgentName); // nombre por defecto
    }

    [Theory]
    [InlineData(400, """{ "error": "orp_pairing_invalid" }""", "orp_pairing_invalid")]
    [InlineData(410, """{ "error": "orp_pairing_expired" }""", "orp_pairing_expired")]
    [InlineData(429, """{ "error": "orp_pairing_locked" }""", "orp_pairing_locked")]
    public async Task Errores_contractuales_propagan_codigo_y_dejan_el_almacen_intacto(int status, string body, string code)
    {
        _httpHandler.Responder = _ => new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        var (service, store, connection, _, _) = Create();

        var ex = await Assert.ThrowsAsync<PairingException>(() => service.ApplyPairingAsync(
            "https://tienda.example/wp-json/orp-print/v1", "000000", null, false, CancellationToken.None));

        Assert.Equal(code, ex.Code);
        Assert.Equal(status, ex.StatusCode);
        Assert.False(store.GetPairing().IsPaired, "un pair fallido no debe escribir nada en el almacén");
        Assert.Null(connection.Transport);
    }

    [Fact]
    public async Task S03_http_rechazado_sin_bandera_y_permitido_con_ella()
    {
        var (service, store, connection, _, _) = Create();

        // Sin flag: rechazo antes de tocar la red ni el almacén.
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyPairingAsync(
            "http://tienda.example/wp-json/orp-print/v1", "482913", null, allowInsecureHttp: false, CancellationToken.None));
        Assert.Contains("S-03", ex.Message);
        Assert.False(store.GetPairing().IsPaired);
        Assert.Null(connection.Transport);

        // Con flag (modo desarrollo explícito): pasa.
        var outcome = await service.ApplyPairingAsync(
            "http://tienda.local/wp-json/orp-print/v1", "482913", null, allowInsecureHttp: true, CancellationToken.None);
        Assert.Equal("http://tienda.local/wp-json/orp-print/v1/", outcome.ServerUrl);
        Assert.NotNull(connection.Transport);
    }

    [Fact]
    public async Task Re_pair_sustituye_transporte_y_token()
    {
        var (service, store, connection, _, _) = Create();
        await service.ApplyPairingAsync("https://a.example/v1", "111111", null, false, CancellationToken.None);
        var primerTransporte = connection.Transport;

        await service.ApplyPairingAsync("https://b.example/v1", "222222", null, false, CancellationToken.None);

        var pairing = store.GetPairing();
        Assert.Equal("https://b.example/v1/", pairing.ServerUrl);
        Assert.NotEqual(primerTransporte, connection.Transport);
        Assert.Equal("https://b.example/v1/", connection.Transport!.BaseUrl.ToString());
    }

    [Fact]
    public void ValidateServerUrl_rechaza_absurdos_y_esquemas_raros()
    {
        Assert.Throws<ArgumentException>(() => PairingService.ValidateServerUrl("no es una url", false));
        Assert.Throws<ArgumentException>(() => PairingService.ValidateServerUrl("ftp://tienda.example", false));
        Assert.Equal("https://a.example/v1/", PairingService.ValidateServerUrl("https://a.example/v1", false).ToString());
        Assert.Equal("https://a.example/v1/", PairingService.ValidateServerUrl("HTTPS://a.example/v1", false).ToString());
    }

    // -------------------------------------------------- recarga del emparejamiento al arrancar

    [Fact]
    public async Task TryLoadPairingFromStore_recupera_transporte_tras_reinicio()
    {
        var (service, store, connection, _, _) = Create();
        await service.ApplyPairingAsync("https://tienda.example/wp-json/orp-print/v1", "482913", null, false, CancellationToken.None);

        // Reinicio del proceso: otra instancia del servicio sobre la MISMA BD (el transporte no sobrevive).
        connection.ClearTransport();

        // Crea el servicio de nuevo con un protector limpio (máquina): descifra el token (S-01).
        var reiniciado = new PairingService(
            connection,
            store,
            new DpapiSecretProtector(),
            new PairingWaiter(),
            new AgentRuntimeStatus(),
            new StaticDestinationProvider(),
            NullLogger<PairingService>.Instance,
            new HttpClient(_httpHandler));

        Assert.True(reiniciado.TryLoadPairingFromStore());
        Assert.NotNull(connection.Transport);
        Assert.Equal("https://tienda.example/wp-json/orp-print/v1/", connection.Transport!.BaseUrl.ToString());
        // Idempotente: ya cargado → true sin doble transporte.
        Assert.True(reiniciado.TryLoadPairingFromStore());
    }

    [Fact]
    public async Task TryLoadPairingFromStore_con_token_ilegible_falla_visible_sin_transporte()
    {
        var (service, store, connection, _, _) = Create();
        store.SetPairing(new PairingInfo
        {
            ServerUrl = "https://tienda.example/v1/",
            TokenProtected = "AAAA", // base64 válido pero NO un blob DPAPI de esta máquina
        });

        Assert.False(service.TryLoadPairingFromStore());
        Assert.Null(connection.Transport);
        Assert.False(store.GetPairing().IsPaired is false); // el almacén no se borra: el usuario decide
    }

    [Fact]
    public void TryLoadPairingFromStore_sin_emparejamiento_devuelve_false()
    {
        var (service, _, _, _, _) = Create();
        Assert.False(service.TryLoadPairingFromStore());
    }
}
