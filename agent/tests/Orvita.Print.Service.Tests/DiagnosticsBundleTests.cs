using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Printing.Windows;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// F2-12: el ZIP de diagnóstico generado de verdad (en %TEMP%) — contenido, ventana de 7 días
/// de logs, últimas 50 entradas de cola, errores con código y AUDITORÍA S-05: el token en claro
/// y el blob DPAPI NO aparecen en ningún fichero del ZIP, y el informe no contiene datos de los
/// tickets (solo ids). </summary>
public sealed class DiagnosticsBundleTests : IDisposable
{
    private const string TokenPlaintext = "orp_tok_SECRETO-no-debe-salir-7f3d";
    private const string TokenProtectedBlob = "DPAPI:BLOB-SECRETO-no-debe-salir-9a2c";

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"orp-diag-{Guid.NewGuid():N}");
    private readonly LocalConfigStore _store;
    private readonly OutboxRepository _outbox;
    private readonly ServerConnection _connection;
    private readonly DiagnosticsBundleBuilder _builder;

    public DiagnosticsBundleTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        var logDirectory = Path.Combine(_dataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        _store = new LocalConfigStore(Path.Combine(_dataDirectory, "outbox.db"));
        _outbox = new OutboxRepository(Path.Combine(_dataDirectory, "outbox.db"));
        _connection = new ServerConnection(new AgentOptions(), NullLogger<ServerConnection>.Instance);
        var runtime = new AgentRuntimeStatus();
        var configState = new RemoteConfigState();
        var updateStatus = new UpdateStatus();

        // Logs: dos recientes y uno de hace 8 días (fuera de la ventana de 7).
        File.WriteAllText(Path.Combine(logDirectory, "orvitaprint-20261122.log"), "log de ayer\n");
        File.WriteAllText(Path.Combine(logDirectory, "orvitaprint-20261123.log"), "log de hoy\n");
        var oldLog = Path.Combine(logDirectory, "orvitaprint-20261110.log");
        File.WriteAllText(oldLog, "log viejo\n");
        File.SetLastWriteTimeUtc(oldLog, DateTimeOffset.UtcNow.Subtract(TimeSpan.FromDays(8)).UtcDateTime);

        // Emparejamiento con token cifrado + transporte con token en claro (S-05: nada sale).
        _store.SetPairing(new PairingInfo
        {
            ServerUrl = "https://tienda.test/wp-json/orp-print/v1/",
            TokenProtected = TokenProtectedBlob,
            LocationId = 3,
            LocationCode = "PIZZA03",
            LocationName = "Pizzería Río Rossó",
            AgentName = "TPV cocina",
            PairedAtUtc = DateTimeOffset.UtcNow,
        });
        _connection.SetTransport(new Uri("https://tienda.test/wp-json/orp-print/v1/"), TokenPlaintext, allowInsecureHttp: false);

        // Cola: 55 trabajos (los últimos 50 deben entrar), dos con error y uno impreso.
        for (var id = 1; id <= 55; id++)
        {
            _outbox.Enqueue(id, "default", 1, """{"version":1}""");
        }

        _outbox.TryClaim(54);
        _outbox.MarkError(54, "orp_printer_not_found", "La impresora «EPSON» no aparece instalada.");
        _outbox.TryClaim(53);
        _outbox.MarkError(53, "orp_print_transient_exhausted", "Spooler ocupado en todos los intentos.");
        _outbox.TryClaim(55);
        _outbox.MarkDone(55);

        var poller = new PollerController(
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

        _store.UpsertDestination(new DestinationOptions { Role = "default", Label = "Mostrador", PrinterName = "EPSON TM-T20III", Copies = 2 });
        _store.UpsertDestination(new DestinationOptions { Role = "kitchen", Label = "Cocina", PrinterName = "Xprinter XP-N160II" });

        var options = new AgentOptions { DataDirectory = _dataDirectory };
        _builder = new DiagnosticsBundleBuilder(
            options,
            _store,
            _outbox,
            new FakeEnumeratedPrinters("EPSON TM-T20III", "Xprinter XP-N160II"),
            poller,
            configState,
            _connection,
            runtime,
            updateStatus,
            NullLogger.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        _outbox.Dispose();
        _connection.Dispose();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Genera_zip_con_logs_de_7_dias_e_informe()
    {
        var zipPath = _builder.CreateZip(DateTimeOffset.UtcNow);

        Assert.True(File.Exists(zipPath));
        Assert.StartsWith(Path.Combine(_dataDirectory, "diagnostics", "orvitaprint-"), zipPath);
        Assert.EndsWith(".zip", zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var entryNames = archive.Entries.Select(e => e.FullName).Order().ToArray();

        Assert.Equal(
            ["diagnostics.json", "logs/orvitaprint-20261122.log", "logs/orvitaprint-20261123.log"],
            entryNames); // el log de hace 8 días NO entra (ventana de 7 días, Doc/03 §11)
    }

    [Fact]
    public void Informe_con_versiones_colas_errores_y_destinos()
    {
        var zipPath = _builder.CreateZip(DateTimeOffset.UtcNow);
        using var archive = ZipFile.OpenRead(zipPath);
        var report = JsonDocument.Parse(ReadEntry(archive, "diagnostics.json")).RootElement;

        Assert.Equal(AgentVersion.Current, report.GetProperty("agent").GetProperty("version").GetString());
        Assert.True(report.GetProperty("pairing").GetProperty("paired").GetBoolean());
        Assert.Equal("https://tienda.test/wp-json/orp-print/v1/", report.GetProperty("pairing").GetProperty("server_url").GetString());
        Assert.Equal("Pizzería Río Rossó", report.GetProperty("pairing").GetProperty("location").GetProperty("name").GetString());

        var queue = report.GetProperty("queue");
        Assert.Equal(50, queue.GetProperty("latest").GetArrayLength()); // 55 trabajos → 50 últimos
        Assert.Equal(52, queue.GetProperty("received").GetInt32());    // 55 - 2 en error - 1 impreso
        Assert.Equal(1, queue.GetProperty("done").GetInt32());
        Assert.Equal(2, queue.GetProperty("error").GetInt32());

        var errors = report.GetProperty("last_errors");
        Assert.Equal(2, errors.GetArrayLength());
        Assert.Contains(
            errors.EnumerateArray(),
            e => e.GetProperty("error_code").GetString() == "orp_printer_not_found" && e.GetProperty("job_id").GetInt64() == 54);

        var destinations = report.GetProperty("destinations");
        Assert.Equal(2, destinations.GetArrayLength());
        Assert.Contains(
            destinations.EnumerateArray(),
            d => d.GetProperty("role").GetString() == "default" && d.GetProperty("copies").GetInt32() == 2);

        Assert.Equal(2, report.GetProperty("printers").GetArrayLength());
        Assert.Equal("windows", report.GetProperty("agent").GetProperty("platform").GetString());
    }

    [Fact]
    public void Solo_ids_de_trabajo_en_el_informe_nunca_el_documento()
    {
        var zipPath = _builder.CreateZip(DateTimeOffset.UtcNow);
        using var archive = ZipFile.OpenRead(zipPath);
        var reportText = ReadEntry(archive, "diagnostics.json");

        Assert.DoesNotContain("document_json", reportText);
        Assert.DoesNotContain("documentJson", reportText);
        Assert.DoesNotContain("Pizza", reportText);
    }

    /// <summary>AUDITORÍA S-05: el token en claro y el blob DPAPI no aparecen en NINGÚN fichero del ZIP.</summary>
    [Fact]
    public void Auditoria_s05_el_token_y_el_blob_no_aparecen_en_ningun_fichero()
    {
        var zipPath = _builder.CreateZip(DateTimeOffset.UtcNow);
        using var archive = ZipFile.OpenRead(zipPath);

        Assert.NotEmpty(archive.Entries);
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            Assert.DoesNotContain(TokenPlaintext, content);
            Assert.DoesNotContain(TokenProtectedBlob, content);
            if (entry.FullName == "diagnostics.json")
            {
                Assert.DoesNotContain("token", content, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Nombre_unico_ante_segundos_en_la_misma_marca_de_tiempo()
    {
        var now = DateTimeOffset.UtcNow;
        var first = _builder.CreateZip(now);
        var second = _builder.CreateZip(now);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task Doctor_zip_por_pipe_devuelve_la_ruta_y_el_fichero_existe()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(new ManagementRequest
        {
            Id = 5,
            Op = ManagementProtocol.Operations.Doctor,
            Args = JsonSerializer.SerializeToElement(new { zip = true }, ManagementProtocol.Options),
        }, CancellationToken.None);

        Assert.True(response.Ok);
        var zipPath = response.Data!.Value.GetProperty("zip_path").GetString();
        Assert.False(string.IsNullOrWhiteSpace(zipPath));
        Assert.True(File.Exists(zipPath));
        Assert.True(response.Data.Value.GetProperty("zip_bytes").GetInt64() > 0);
        File.Delete(zipPath!);
    }

    [Fact]
    public async Task Doctor_sin_zip_no_genera_fichero()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(new ManagementRequest
        {
            Id = 5,
            Op = ManagementProtocol.Operations.Doctor,
        }, CancellationToken.None);

        Assert.True(response.Ok);
        // Con DefaultIgnoreCondition.WhenWritingNull, sin ZIP el campo ni siquiera aparece.
        Assert.False(response.Data!.Value.TryGetProperty("zip_path", out _));
    }

    /// <summary>Fake local: igual que el anidado de ManagementRequestHandlerTests, propio de esta clase.</summary>
    private sealed class FakeEnumeratedPrinters(params string[] names) : IPrinterEnumerator
    {
        public IReadOnlyList<PrinterInfo> List() =>
            [.. names.Select((name, index) => new PrinterInfo { Name = name, IsDefault = index == 0, StatusHint = index == 0 ? "Lista" : null })];

        public PrinterInfo? Find(string printerName) =>
            List().FirstOrDefault(p => string.Equals(p.Name, printerName, StringComparison.OrdinalIgnoreCase));

        public string? DefaultPrinterName() => names.Length > 0 ? names[0] : null;
    }

    private ManagementRequestHandler CreateHandler()
    {
        var connection = _connection;
        var pairingService = new PairingService(
            connection,
            _store,
            new DpapiSecretProtector(),
            new PairingWaiter(),
            new AgentRuntimeStatus(),
            new StoreDestinationProvider(_store),
            NullLogger<PairingService>.Instance);
        var poller = new PollerController(
            () => connection.Transport!,
            new JobProcessor(
                () => connection.Transport!,
                _outbox,
                new StoreDestinationProvider(_store),
                new FakePrintTransportFactory(new FakePrintTransport()),
                rendererOptions: () => new EscPosRendererOptions(),
                pngDecoder: new FakePngDecoder(),
                options: new JobProcessorOptions(),
                logger: NullLogger<JobProcessor>.Instance),
            intervalSeconds: () => 10,
            logger: NullLogger<PollerController>.Instance);

        return new ManagementRequestHandler(
            connection,
            _store,
            _outbox,
            pairingService,
            poller,
            new TestPrintService(
                new StoreDestinationProvider(_store),
                new FakePrintTransportFactory(new FakePrintTransport()),
                new RemoteConfigState(),
                NullLogger.Instance),
            new FakeEnumeratedPrinters("EPSON TM-T20III"),
            new RemoteConfigState(),
            new AgentRuntimeStatus(),
            new AgentOptions { DataDirectory = _dataDirectory },
            NullLogger<ManagementRequestHandler>.Instance,
            _builder);
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"Falta «{name}» en el ZIP.");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
