using System.Net;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>
/// Cliente de <c>POST /pair</c> (Doc/04 §5.2) contra un handler HTTP falso: 200 mapea la
/// respuesta (token mostrado una vez, local, poll_interval) y 400/410/429 se traducen a los
/// códigos contractuales orp_pairing_invalid/expired/locked (Doc/04 §6).
/// </summary>
public sealed class PairingClientTests : IDisposable
{
    private readonly FakePairHttpHandler _handler = new();
    private readonly HttpClient _http;

    public PairingClientTests()
    {
        _http = new HttpClient(_handler);
    }

    public void Dispose() => _http.Dispose();

    private PairingClient Create() => new(new Uri("https://tienda.example/wp-json/orp-print/v1"), _http);

    private static PairRequest ValidRequest() => new()
    {
        Code = "482913",
        AgentName = "TPV Mostrador",
        Platform = "windows",
        AgentVersion = "1.0.0-dev",
        Destinations = [new DestinationInfo { Role = "default", Label = "Mostrador", PrinterName = "EPSON TM-T20III", Copies = 2, Enabled = true }],
    };

    [Fact]
    public async Task Pair_200_envia_cuerpo_contractual_y_mapea_respuesta()
    {
        string? body = null;
        string? path = null;
        string? authorization = null;
        _handler.Responder = request =>
        {
            path = request.RequestUri!.AbsolutePath;
            authorization = request.Headers.Authorization?.ToString();
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Response(200, """
                { "token": "orp_7f3a9c2e1b", "location": { "id": 2, "code": "pueblo-b", "name": "Pizzería Pueblo B" }, "poll_interval": 10 }
                """);
        };

        using var client = Create();
        var response = await client.PairAsync(ValidRequest(), CancellationToken.None);

        Assert.EndsWith("/pair", path);
        Assert.Null(authorization); // sin token: es el único endpoint que no lo lleva (aún no existe)
        Assert.Contains("\"code\":\"482913\"", body);
        Assert.Contains("\"agent_name\":\"TPV Mostrador\"", body);
        Assert.Contains("\"platform\":\"windows\"", body);
        Assert.Contains("\"destinations\":", body);
        Assert.Contains("\"printer_name\":\"EPSON TM-T20III\"", body);

        Assert.Equal("orp_7f3a9c2e1b", response.Token);
        Assert.Equal(2, response.Location!.Id);
        Assert.Equal("Pizzería Pueblo B", response.Location.Name);
        Assert.Equal(10, response.PollIntervalSeconds);
    }

    [Theory]
    [InlineData(400, """{ "error": "orp_pairing_invalid" }""", "orp_pairing_invalid")]
    [InlineData(410, """{ "error": "orp_pairing_expired" }""", "orp_pairing_expired")]
    [InlineData(429, """{ "error": "orp_pairing_locked" }""", "orp_pairing_locked")]
    public async Task Errores_contractuales_se_mapean_al_codigo_del_contrato(int status, string body, string expectedCode)
    {
        _handler.Responder = _ => Response(status, body);

        using var client = Create();
        var ex = await Assert.ThrowsAsync<PairingException>(() => client.PairAsync(ValidRequest()));

        Assert.Equal(expectedCode, ex.Code);
        Assert.Equal(status, ex.StatusCode);
        Assert.NotEqual("server_error", ex.Code);
    }

    [Fact]
    public async Task Error_en_envoltorio_wordpress_tambien_se_entiende()
    {
        _handler.Responder = _ => Response(400, """
            { "code": "rest_no_route", "message": "No route", "data": { "status": 404 } }
            """);

        using var client = Create();
        var ex = await Assert.ThrowsAsync<PairingException>(() => client.PairAsync(ValidRequest()));

        Assert.Equal("rest_no_route", ex.Code);
        Assert.Equal("No route", ex.Message);
    }

    [Fact]
    public async Task Respuesta_no_json_da_server_error_con_mensaje_amable()
    {
        _handler.Responder = _ => Response(502, "<html>Bad Gateway</html>");

        using var client = Create();
        var ex = await Assert.ThrowsAsync<PairingException>(() => client.PairAsync(ValidRequest()));

        Assert.Equal("server_error", ex.Code);
        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task Respuesta_200_sin_token_falla_con_server_error()
    {
        _handler.Responder = _ => Response(200, """{ "location": { "id": 1 } }""");

        using var client = Create();
        var ex = await Assert.ThrowsAsync<PairingException>(() => client.PairAsync(ValidRequest()));

        Assert.Equal("server_error", ex.Code);
    }

    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };
}

/// <summary>Handler HTTP falso mínimo (una respuesta configurable por llamada).</summary>
public sealed class FakePairHttpHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Responder(request));
}
