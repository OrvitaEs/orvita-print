using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// Servidor de pipe REAL (F2-08) en proceso: NamedPipeServerStream con ACL + cliente real de
/// la CLI sobre una pipe con nombre aleatorio. Protocolo JSON de líneas, correlación por id,
/// varias solicitudes en una conexión y línea rota → respuesta de error sin tirar el servidor.
/// </summary>
public sealed class ManagementPipeServerTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"orp-pipe-{Guid.NewGuid():N}.db");
    private readonly string _pipeName = $"orvitaprint-test-{Guid.NewGuid():N}";
    private readonly FakeHttpHandler _httpHandler;
    private readonly LocalConfigStore _store;
    private readonly OutboxRepository _outbox;
    private readonly ServerConnection _connection;
    private readonly ManagementPipeServer _server;
    private readonly PollerController _poller;

    public ManagementPipeServerTests()
    {
        _httpHandler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{ "token": "orp_token_pipe_123", "location": { "id": 9, "code": "rio-rosso", "name": "Pizzería Río Rosso" }, "poll_interval": 10 }""",
                System.Text.Encoding.UTF8,
                "application/json"),
        });
        _store = new LocalConfigStore(_databasePath);
        _outbox = new OutboxRepository(_databasePath);
        _connection = new ServerConnection(new AgentOptions(), NullLogger<ServerConnection>.Instance);
        _poller = new PollerController(
            () => _connection.Transport!,
            new JobProcessor(
                () => _connection.Transport!,
                _outbox,
                new StoreDestinationProvider(_store),
                new FakePrintTransportFactory(new FakePrintTransport()),
                rendererOptions: () => new EscPosRendererOptions(),
                pngDecoder: new FakePngDecoder(),
                options: new JobProcessorOptions(),
                logger: NullLogger<JobProcessor>.Instance),
            intervalSeconds: () => 10,
            logger: NullLogger<PollerController>.Instance);
        var pairingService = new PairingService(
            _connection,
            _store,
            new DpapiSecretProtector(),
            new PairingWaiter(),
            new AgentRuntimeStatus(),
            new StoreDestinationProvider(_store),
            NullLogger<PairingService>.Instance,
            new HttpClient(_httpHandler));
        var handler = new ManagementRequestHandler(
            _connection,
            _store,
            _outbox,
            pairingService,
            _poller,
            new TestPrintService(
                new StoreDestinationProvider(_store),
                new FakePrintTransportFactory(new FakePrintTransport()),
                new RemoteConfigState(),
                NullLogger.Instance),
            new FakePipePrinterEnumerator(),
            new RemoteConfigState(),
            new AgentRuntimeStatus(),
            new AgentOptions { DataDirectory = Path.GetTempPath() },
            NullLogger<ManagementRequestHandler>.Instance);
        _server = new ManagementPipeServer(handler, NullLogger<ManagementPipeServer>.Instance, _pipeName);
    }

    public async Task InitializeAsync() => await _server.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        _store.Dispose();
        _outbox.Dispose();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task Status_y_ping_por_la_pipe_real_con_correlacion_de_id()
    {
        using var client = new TestPipeClient(_pipeName);
        client.Connect();

        var ping = client.Request(new ManagementRequest { Id = 42, Op = ManagementProtocol.Operations.Ping });
        Assert.Equal(42, ping.Id);
        Assert.True(ping.Ok);
        Assert.True(ping.Data!.Value.GetProperty("pong").GetBoolean());

        var status = client.Request(new ManagementRequest { Id = 43, Op = ManagementProtocol.Operations.Status });
        Assert.Equal(43, status.Id);
        Assert.True(status.Ok);
        Assert.False(status.Data!.Value.GetProperty("paired").GetBoolean());
    }

    [Fact]
    public async Task Pair_por_la_pipe_real_guarda_token_cifrado_y_responde_sin_eco_S05()
    {
        using var client = new TestPipeClient(_pipeName);
        client.Connect();

        var response = client.Request(new ManagementRequest
        {
            Id = 5,
            Op = ManagementProtocol.Operations.Pair,
            Args = JsonSerializer.SerializeToElement(new
            {
                server = "https://tienda.example/wp-json/orp-print/v1",
                code = "482913",
                agent_name = "TPV Pipe",
            }, ManagementProtocol.Options),
        });

        Assert.True(response.Ok, $"pair por pipe falló: {response.Error}");
        Assert.Equal("Pizzería Río Rosso", response.Data!.Value.GetProperty("location").GetProperty("name").GetString());

        // S-05: el token NUNCA vuelve por la pipe.
        var texto = JsonSerializer.Serialize(response, ManagementProtocol.Options);
        Assert.DoesNotContain("orp_token_pipe_123", texto);
        Assert.DoesNotContain("token", texto);

        // S-01: en el almacén el token está cifrado.
        var pairing = _store.GetPairing();
        Assert.True(pairing.IsPaired);
        Assert.NotEqual("orp_token_pipe_123", pairing.TokenProtected);
        Assert.NotNull(_connection.Transport);

        await _poller.StopAsync();
    }

    [Fact]
    public async Task Pair_http_con_allow_insecure_http_true_empareja_por_la_pipe_GUI14()
    {
        using var client = new TestPipeClient(_pipeName);
        client.Connect();

        var response = client.Request(new ManagementRequest
        {
            Id = 6,
            Op = ManagementProtocol.Operations.Pair,
            Args = JsonSerializer.SerializeToElement(new
            {
                server = "http://localhost:8888/wp-json/orp-print/v1",
                code = "482913",
                allow_insecure_http = true, // lo que la GUI manda con la casilla marcada
            }, ManagementProtocol.Options),
        });

        Assert.True(response.Ok, $"pair http por pipe falló: {response.Error}");
        Assert.Equal("Pizzería Río Rosso", response.Data!.Value.GetProperty("location").GetProperty("name").GetString());
        Assert.Equal("http://localhost:8888/wp-json/orp-print/v1/", response.Data.Value.GetProperty("server_url").GetString());
        Assert.True(_store.GetPairing().IsPaired);

        await _poller.StopAsync();
    }

    [Fact]
    public async Task Pair_http_sin_bandera_rechaza_con_el_error_S03_GUI14()
    {
        using var client = new TestPipeClient(_pipeName);
        client.Connect();

        var response = client.Request(new ManagementRequest
        {
            Id = 7,
            Op = ManagementProtocol.Operations.Pair,
            Args = JsonSerializer.SerializeToElement(new
            {
                server = "http://localhost:8888/wp-json/orp-print/v1",
                code = "482913",
                allow_insecure_http = false, // lo que la GUI mandaba ANTES del fix (campo ausente)
            }, ManagementProtocol.Options),
        });

        Assert.False(response.Ok);
        Assert.Contains("HTTPS (S-03", response.Error);
        Assert.False(_store.GetPairing().IsPaired); // nada se guardó
        Assert.Null(_connection.Transport);
    }

    [Fact]
    public async Task Varias_solicitudes_en_una_conexion_y_operacion_desconocida()
    {
        using var client = new TestPipeClient(_pipeName);
        client.Connect();

        for (var i = 1; i <= 5; i++)
        {
            var response = client.Request(new ManagementRequest { Id = i, Op = ManagementProtocol.Operations.Status });
            Assert.Equal(i, response.Id);
            Assert.True(response.Ok);
        }

        var desconocida = client.Request(new ManagementRequest { Id = 99, Op = "comer" });
        Assert.Equal(99, desconocida.Id);
        Assert.False(desconocida.Ok);
        Assert.Equal("unknown_operation", desconocida.Code);
    }

    [Fact]
    public async Task Linea_rota_devuelve_error_y_el_servidor_sigue_vivo()
    {
        using (var client = new TestPipeClient(_pipeName))
        {
            client.Connect();
            var respuesta = client.SendRaw("esto no es json");
            Assert.Equal(0, respuesta.Id);
            Assert.False(respuesta.Ok);
            Assert.Equal("invalid_request", respuesta.Code);
        }

        // El servidor acepta un cliente nuevo tras la línea rota (no cae).
        using var cliente2 = new TestPipeClient(_pipeName);
        cliente2.Connect();
        var ping = cliente2.Request(new ManagementRequest { Id = 1, Op = ManagementProtocol.Operations.Ping });
        Assert.True(ping.Ok);
    }

    [Fact]
    public async Task Doctores_y_destinations_por_la_pipe_real()
    {
        using var client = new TestPipeClient(_pipeName);
        client.Connect();

        var add = client.Request(new ManagementRequest
        {
            Id = 10,
            Op = ManagementProtocol.Operations.DestinationAdd,
            Args = JsonSerializer.SerializeToElement(new { printer = "EPSON TM-T20III", role = "default" }, ManagementProtocol.Options),
        });
        Assert.True(add.Ok);

        var doctor = client.Request(new ManagementRequest { Id = 11, Op = ManagementProtocol.Operations.Doctor });
        Assert.True(doctor.Ok);
        Assert.Equal(1, doctor.Data!.Value.GetProperty("printers").GetArrayLength());
        var texto = JsonSerializer.Serialize(doctor, ManagementProtocol.Options);
        Assert.DoesNotContain("token", texto); // S-05 en doctor
    }

    /// <summary>Cliente de prueba: named pipe real + JSON de líneas (lo que hace el CLI).</summary>
    private sealed class TestPipeClient : IDisposable
    {
        private readonly string _pipeName;
        private NamedPipeClientStream? _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        public TestPipeClient(string pipeName) => _pipeName = pipeName;

        public void Connect()
        {
            // 10 s: el servidor puede estar arrancando. Los readers van DESPUÉS de conectar:
            // acceder al stream de una pipe sin conectar lanza.
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            _pipe.Connect(10_000);
            _reader = new StreamReader(_pipe, System.Text.Encoding.UTF8, false, 1024, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new System.Text.UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        }

        public ManagementResponse Request(ManagementRequest request)
        {
            _writer!.WriteLine(JsonSerializer.Serialize(request, ManagementProtocol.Options));
            var line = _reader!.ReadLine() ?? throw new IOException("conexión cerrada");
            return JsonSerializer.Deserialize<ManagementResponse>(line, ManagementProtocol.Options)!;
        }

        public ManagementResponse SendRaw(string line)
        {
            _writer!.WriteLine(line);
            var responseLine = _reader!.ReadLine() ?? throw new IOException("conexión cerrada");
            return JsonSerializer.Deserialize<ManagementResponse>(responseLine, ManagementProtocol.Options)!;
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _pipe?.Dispose();
        }
    }

    private sealed class FakePipePrinterEnumerator : IPrinterEnumerator
    {
        private readonly List<PrinterInfo> _printers = [new() { Name = "EPSON TM-T20III", IsDefault = true }];

        public IReadOnlyList<PrinterInfo> List() => _printers;

        public string? DefaultPrinterName() => _printers.FirstOrDefault()?.Name;

        public PrinterInfo? Find(string name) => _printers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
    }
}
