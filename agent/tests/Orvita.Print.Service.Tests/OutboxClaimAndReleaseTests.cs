using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// Complementos de la cola local para el pipeline (F2-07): claim por id, reset a received
/// tras fallo transitorio, y release HTTP (Doc/04 §5.7) de DirectTransport.
/// </summary>
public sealed class OutboxClaimAndReleaseTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"orp-outbox-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        File.Delete(_databasePath);
        if (File.Exists(_databasePath + "-wal"))
        {
            File.Delete(_databasePath + "-wal");
        }

        if (File.Exists(_databasePath + "-shm"))
        {
            File.Delete(_databasePath + "-shm");
        }
    }

    [Fact]
    public void TryClaim_por_id_transiciona_received_a_printing_y_cuenta_intento()
    {
        using var outbox = new OutboxRepository(_databasePath);
        outbox.Enqueue(1, "default", 1, "{}");
        outbox.Enqueue(2, "default", 1, "{}");

        Assert.True(outbox.TryClaim(2));
        var row = outbox.Get(2);
        Assert.Equal(OutboxJobState.Printing, row!.State);
        Assert.Equal(1, row.Attempts);

        Assert.False(outbox.TryClaim(2)); // ya no está en received
        Assert.False(outbox.TryClaim(3)); // inexistente
        Assert.Equal(1, outbox.Get(2)!.Attempts); // el intento no se duplica
    }

    [Fact]
    public void ResetToReceived_devuelve_a_cola_conservando_documento()
    {
        using var outbox = new OutboxRepository(_databasePath);
        outbox.Enqueue(5, "cocina", 1, "{\"version\":1}");
        outbox.TryClaim(5);

        Assert.True(outbox.ResetToReceived(5));
        var row = outbox.Get(5);
        Assert.Equal(OutboxJobState.Received, row!.State);
        Assert.Equal("{\"version\":1}", row.DocumentJson);
        Assert.False(outbox.ResetToReceived(5)); // ya no está en printing
    }

    // ------------------------------------------------------------ release HTTP

    [Fact]
    public async Task DirectTransport_release_envia_el_motivo()
    {
        string? body = null;
        string? path = null;
        var handler = new FakeHttpHandler(request =>
        {
            path = request.RequestUri!.AbsolutePath;
            body = request.Content is null ? null : request.Content.ReadAsStringAsync().Result;
            return Response(200, """{ "ok": true }""");
        });
        using var transport = new DirectTransport(
            new Uri("https://tienda.test/wp-json/orp-print/v1"),
            "orp_token_prueba",
            new HttpClient(handler));

        await transport.ReleaseAsync(55, "spooler_timeout", CancellationToken.None);

        Assert.EndsWith("/jobs/55/release", path);
        Assert.Contains("\"reason\":\"spooler_timeout\"", body);
    }

    [Fact]
    public async Task DirectTransport_release_409_o_410_no_es_error()
    {
        var handler = new FakeHttpHandler(_ => Response(409, """{ "error": "orp_already_claimed" }"""));
        using var transport = new DirectTransport(
            new Uri("https://tienda.test/wp-json/orp-print/v1"),
            "orp_token_prueba",
            new HttpClient(handler));

        await transport.ReleaseAsync(56, "spooler_timeout", CancellationToken.None); // no lanza

        handler.Responder = _ => Response(410, """{ "error": "orp_job_expired" }""");
        await transport.ReleaseAsync(56, "spooler_timeout", CancellationToken.None); // tampoco
    }

    internal static HttpResponseMessage Response(int status, string json) =>
        new((HttpStatusCode)status)
        {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
}

/// <summary>Handler HTTP falso para probar DirectTransport sin red.</summary>
public sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Responder(request));
    }
}
