using System.Net;
using System.Text;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>
/// DirectTransport contra un HttpMessageHandler falso (sin servidor real): los códigos
/// del contrato 04 §6 en sus dos formas de cuerpo, Retry-After, y la validación HTTPS
/// obligatoria (S-03).
/// </summary>
public sealed class DirectTransportTests
{
    private static readonly Uri Server = new("https://shop.example/wp-json/orp-print/v1");

    // ---------------------------------------------------------------- helpers

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Responder { get; set; } =
            _ => Task.FromResult(Json(HttpStatusCode.OK, "{}"));

        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];
        public List<string?> AuthHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            AuthHeaders.Add(request.Headers.Authorization?.ToString());
            return await Responder(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
        return response;
    }

    private static DirectTransport Create(FakeHandler handler) =>
        new(Server, "orp_test_token", new HttpClient(handler), allowInsecureHttp: false);

    private const string JobJson = """
        {
          "id": 1844,
          "order_id": 5678,
          "printer_role": "default",
          "created_at": "2026-11-19T20:14:58+01:00",
          "expires_at": "2026-11-19T20:44:58+01:00",
          "attempt": 1,
          "document": { "version": 1, "blocks": [ { "type": "text", "value": "María López" } ] }
        }
        """;

    // ---------------------------------------------------------------- fetch

    [Fact]
    public async Task Fetch_200_devuelve_lote_y_envia_bearer_y_query()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Task.FromResult(Json(HttpStatusCode.OK, $$"""
                {"jobs":[{{JobJson}}],"cursor":1844}
                """)),
        };
        var transport = Create(handler);

        var batch = await transport.FetchAsync(1843, CancellationToken.None);

        Assert.Equal(1844, batch.Cursor);
        var job = Assert.Single(batch.Jobs);
        Assert.Equal(1844, job.Id);
        Assert.Equal(5678, job.OrderId);
        Assert.Equal("default", job.PrinterRole);
        Assert.Equal(1, job.Attempt);
        var block = Assert.IsType<TextBlock>(Assert.Single(job.Document!.Blocks));
        Assert.Equal("María López", block.Value);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith("/jobs?after=1843&limit=20", request.Url);
        Assert.Equal("Bearer orp_test_token", Assert.Single(handler.AuthHeaders));
    }

    [Fact]
    public async Task Fetch_sin_trabajos_devuelve_lote_vacio_sin_error()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Task.FromResult(Json(HttpStatusCode.OK, """{"jobs":[],"cursor":0}""")),
        };

        var batch = await Create(handler).FetchAsync(0, CancellationToken.None);

        Assert.Empty(batch.Jobs);
        Assert.Equal(0, batch.Cursor);
    }

    [Fact]
    public async Task Fetch_limit_superior_a_20_se_recorta_al_maximo_del_contrato()
    {
        var handler = new FakeHandler { Responder = _ => Task.FromResult(Json(HttpStatusCode.OK, """{"jobs":[],"cursor":0}""")) };
        var transport = new DirectTransport(Server, "t", new HttpClient(handler), fetchLimit: 500);

        await transport.FetchAsync(0, CancellationToken.None);

        Assert.EndsWith("/jobs?after=0&limit=20", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task Fetch_401_con_envoltorio_WP_para_el_poller()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Task.FromResult(Json(HttpStatusCode.Unauthorized,
                """{"code":"orp_token_revoked","message":"Token revocado","data":{"status":401}}""")),
        };

        var ex = await Assert.ThrowsAsync<TransportUnauthorizedException>(
            () => Create(handler).FetchAsync(0, CancellationToken.None));

        Assert.Equal(ErrorCode.OrpTokenRevoked, ex.ErrorCode);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("Token revocado", ex.Message);
    }

    [Fact]
    public async Task Fetch_429_expone_retry_after()
    {
        var handler = new FakeHandler
        {
            Responder = request =>
            {
                var response = Json(HttpStatusCode.TooManyRequests,
                    """{"code":"orp_rate_limited","message":"Demasiadas peticiones","data":{"status":429}}""");
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return Task.FromResult(response);
            },
        };

        var ex = await Assert.ThrowsAsync<TransportRateLimitedException>(
            () => Create(handler).FetchAsync(0, CancellationToken.None));

        Assert.Equal(ErrorCode.OrpRateLimited, ex.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
    }

    [Fact]
    public async Task Fetch_429_sin_cabecera_retry_after_devuelve_null()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Task.FromResult(Json(HttpStatusCode.TooManyRequests,
                """{"code":"orp_rate_limited","message":"…","data":{"status":429}}""")),
        };

        var ex = await Assert.ThrowsAsync<TransportRateLimitedException>(
            () => Create(handler).FetchAsync(0, CancellationToken.None));

        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public async Task Error_de_red_se_traduce_a_TransportNetworkException()
    {
        var handler = new FakeHandler
        {
            Responder = _ => throw new HttpRequestException("connection refused"),
        };

        var ex = await Assert.ThrowsAsync<TransportNetworkException>(
            () => Create(handler).FetchAsync(0, CancellationToken.None));

        Assert.Null(ex.StatusCode);
    }

    [Fact]
    public async Task Fetch_500_es_error_de_protocolo_y_no_para_el_poller()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Task.FromResult(Json(HttpStatusCode.InternalServerError, "<html>boom</html>")),
        };

        var ex = await Assert.ThrowsAsync<TransportProtocolException>(
            () => Create(handler).FetchAsync(0, CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Equal(ErrorCode.Unknown, ex.ErrorCode);
    }

    // ---------------------------------------------------------------- claim

    [Fact]
    public async Task Claim_200_devuelve_el_trabajo_completo()
    {
        var handler = new FakeHandler { Responder = _ => Task.FromResult(Json(HttpStatusCode.OK, JobJson)) };

        var job = await Create(handler).ClaimAsync(1844, CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal(1844, job!.Id);
        var block = Assert.IsType<TextBlock>(Assert.Single(job.Document!.Blocks));
        Assert.Equal("María López", block.Value);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/jobs/1844/claim", request.Url);
        Assert.Equal(string.Empty, request.Body); // cuerpo vacío: el agente se identifica por el token
        Assert.Equal("Bearer orp_test_token", Assert.Single(handler.AuthHeaders));
    }

    [Fact]
    public async Task Claim_409_otro_lo_cogio_devuelve_null_y_continua()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Task.FromResult(Json(HttpStatusCode.Conflict,
                """{"error":"orp_already_claimed","status":"claimed"}""")),
        };

        var job = await Create(handler).ClaimAsync(1844, CancellationToken.None);

        Assert.Null(job); // «otro lo cogió»: continuar
    }

    [Fact]
    public async Task Claim_410_caducado_devuelve_null_para_descartar()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Task.FromResult(Json(HttpStatusCode.Gone, """{"error":"orp_job_expired"}""")),
        };

        Assert.Null(await Create(handler).ClaimAsync(1844, CancellationToken.None));
    }

    [Fact]
    public async Task Claim_404_ajeno_devuelve_null()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Task.FromResult(Json(HttpStatusCode.NotFound,
                """{"code":"orp_not_found","message":"…","data":{"status":404}}""")),
        };

        Assert.Null(await Create(handler).ClaimAsync(1844, CancellationToken.None));
    }

    // ---------------------------------------------------------------- ack

    [Fact]
    public async Task Ack_delivered_envia_el_resultado_y_lee_la_respuesta()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Task.FromResult(Json(HttpStatusCode.OK, """{"ok":true,"job_status":"delivered"}""")),
        };

        var result = new JobResult { Status = JobResultStatus.Delivered, Attempt = 1, Printer = "EPSON TM-T20III", ElapsedMs = 412 };
        await Create(handler).AckAsync(1844, result, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/jobs/1844/ack", request.Url);
        Assert.Contains("\"status\":\"delivered\"", request.Body);
        Assert.Contains("\"printer\":\"EPSON TM-T20III\"", request.Body);
        Assert.Contains("\"elapsed_ms\":412", request.Body);
    }

    [Fact]
    public async Task Ack_failed_envia_codigo_y_mensaje_de_error()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Task.FromResult(Json(HttpStatusCode.OK, """{"ok":true,"job_status":"failed"}""")),
        };

        var result = new JobResult
        {
            Status = JobResultStatus.Failed,
            Attempt = 1,
            ErrorCode = "orp_printer_not_found",
            Error = "No existe la impresora «EPSON TM-T20III» en este equipo",
        };
        await Create(handler).AckAsync(1844, result, CancellationToken.None);

        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("\"status\":\"failed\"", body);
        Assert.Contains("\"error_code\":\"orp_printer_not_found\"", body);
        Assert.Contains("«EPSON TM-T20III»", body);
    }

    [Fact]
    public async Task Ack_sobre_trabajo_terminal_409_410_404_no_levanta_excepcion()
    {
        foreach (var status in new[] { HttpStatusCode.Conflict, HttpStatusCode.Gone, HttpStatusCode.NotFound })
        {
            var handler = new FakeHandler
            {
                Responder = _ => Task.FromResult(Json(status, """{"error":"orp_job_expired"}""")),
            };

            await Create(handler).AckAsync(1844, new JobResult(), CancellationToken.None);
        }
    }

    // ---------------------------------------------------------------- health / heartbeat / config

    [Fact]
    public async Task Health_se_parsea_sin_token_necesario()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Task.FromResult(Json(HttpStatusCode.OK, """
                {"ok":true,"plugin_version":"1.0.0","api_version":1,"woocommerce":"11.3.1","hpos":true,"server_time":"2026-11-19T20:15:00+01:00"}
                """)),
        };

        var health = await Create(handler).GetHealthAsync(CancellationToken.None);

        Assert.True(health.Ok);
        Assert.Equal(1, health.ApiVersion);
        Assert.Equal("11.3.1", health.Woocommerce);
        Assert.True(health.Hpos);
        Assert.EndsWith("/health", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task Heartbeat_envia_version_pendientes_y_salidas()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Task.FromResult(Json(HttpStatusCode.OK,
                """{"ok":true,"server_time":"2026-11-19T20:15:10+01:00","pending_count":2,"config_version":14}""")),
        };

        var response = await Create(handler).SendHeartbeatAsync(new HeartbeatRequest
        {
            AgentVersion = "1.0.0",
            PendingLocal = 0,
            Destinations = { new DestinationInfo { Role = "default", Label = "Mostrador", PrinterName = "EPSON TM-T20III", Copies = 2, Enabled = true } },
        }, CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal(2, response.PendingCount);
        Assert.Equal(14, response.ConfigVersion);

        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("\"agent_version\":\"1.0.0\"", body);
        Assert.Contains("\"pending_local\":0", body);
        Assert.Contains("\"role\":\"default\"", body);
        Assert.Contains("\"printer_name\":\"EPSON TM-T20III\"", body);
        Assert.Contains("\"copies\":2", body);
    }

    [Fact]
    public async Task Config_se_parsea_con_snake_case()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Task.FromResult(Json(HttpStatusCode.OK, """
                {"config_version":14,"paper_width_mm":80,"encoding":"CP850","cut_mode":"partial","open_drawer":false,"poll_interval":10,"sound_alert":false}
                """)),
        };

        var config = await Create(handler).GetConfigAsync(CancellationToken.None);

        Assert.Equal(14, config.ConfigVersion);
        Assert.Equal(80, config.PaperWidthMm);
        Assert.Equal("CP850", config.Encoding);
        Assert.Equal(CutMode.Partial, config.CutMode);
        Assert.False(config.OpenDrawer);
        Assert.Equal(10, config.PollIntervalSeconds);
        Assert.False(config.SoundAlert);
        Assert.EndsWith("/config", Assert.Single(handler.Requests).Url);
    }

    // ---------------------------------------------------------------- seguridad (S-03)

    [Fact]
    public void Url_http_se_rechaza_sin_flag_de_desarrollo()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new DirectTransport(new Uri("http://localhost:8888/wp-json/orp-print/v1"), "t"));

        Assert.Contains("HTTPS", ex.Message);
    }

    [Fact]
    public void Url_http_con_flag_de_desarrollo_se_acepta_para_wp_env()
    {
        var transport = new DirectTransport(
            new Uri("http://localhost:8888/wp-json/orp-print/v1"), "t", allowInsecureHttp: true);

        Assert.Equal("http://localhost:8888/wp-json/orp-print/v1/", transport.BaseUrl.ToString());
    }

    [Fact]
    public async Task La_base_url_normaliza_la_barra_final()
    {
        var handler = new FakeHandler
        {
            Responder = _ => Task.FromResult(Json(HttpStatusCode.OK, """{"jobs":[],"cursor":0}""")),
        };
        var transport = new DirectTransport(new Uri("https://shop.example/wp-json/orp-print/v1"), "t", new HttpClient(handler));

        await transport.FetchAsync(0, CancellationToken.None);

        Assert.Contains("/wp-json/orp-print/v1/jobs?", Assert.Single(handler.Requests).Url);
    }
}
