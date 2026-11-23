using Microsoft.Extensions.Logging.Abstractions;
using Orvita.Print.Core;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// <see cref="UpdateApplier"/> (F2-11) con msiexec FALSO (inyectado): secuencia — poller y pipe
/// se detienen antes, códigos 0/3010 son éxito, cualquier otro código falla conservando la
/// versión actual. El msiexec REAL no se toca en pruebas (instalar de verdad → F3-04/F4).
/// </summary>
public sealed class UpdateApplierTests
{
    private static PreparedUpdate Prepare(string version = "1.2.0", string? installerPath = null) => new()
    {
        Manifest = new UpdateManifest
        {
            Version = version,
            Url = $"https://updates.orvita.example/agent/OrvitaPrint-{version}.msi",
            Sha256 = new string('a', 64),
            MinSupported = "1.0.0",
            Signature = "AAAA",
        },
        InstallerPath = installerPath ?? Path.Combine(Path.GetTempPath(), $"orp-applier-{Guid.NewGuid():N}.msi"),
    };

    private static PollerController CreateIdlePoller()
    {
        var outbox = new OutboxRepository(Path.Combine(Path.GetTempPath(), $"orp-applier-{Guid.NewGuid():N}.db"));
        return new PollerController(
            () => throw new InvalidOperationException("sin transporte en esta prueba"),
            new JobProcessor(
                () => throw new InvalidOperationException("sin transporte en esta prueba"),
                outbox,
                new StaticDestinationProvider(),
                new FakePrintTransportFactory(new FakePrintTransport()),
                rendererOptions: () => new EscPosRendererOptions(),
                pngDecoder: new FakePngDecoder(),
                options: new JobProcessorOptions(),
                logger: NullLogger<JobProcessor>.Instance),
            intervalSeconds: () => 10,
            logger: NullLogger<PollerController>.Instance);
    }

    private static (ManagementPipeServer Pipe, ManagementRequestHandler Handler) CreatePipe()
    {
        // El applier solo llama a StopAsync de la pipe, pero su constructor exige un handler:
        // se construye uno real con dependencias falsas sobre una BD temporal.
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"orp-applier-pipe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        var store = new LocalConfigStore(Path.Combine(dataDirectory, "outbox.db"));
        var outbox = new OutboxRepository(Path.Combine(dataDirectory, "outbox.db"));
        var connection = new ServerConnection(new AgentOptions(), NullLogger<ServerConnection>.Instance);
        var printFactory = new FakePrintTransportFactory(new FakePrintTransport());
        var handler = new ManagementRequestHandler(
            connection,
            store,
            outbox,
            new PairingService(connection, store, new DpapiSecretProtector(), new PairingWaiter(),
                new AgentRuntimeStatus(), new StoreDestinationProvider(store), NullLogger<PairingService>.Instance),
            new PollerController(
                () => connection.Transport!,
                new JobProcessor(() => connection.Transport!, outbox, new StoreDestinationProvider(store), printFactory,
                    rendererOptions: () => new EscPosRendererOptions(), pngDecoder: new FakePngDecoder(),
                    options: new JobProcessorOptions(), logger: NullLogger<JobProcessor>.Instance),
                intervalSeconds: () => 10,
                logger: NullLogger<PollerController>.Instance),
            new TestPrintService(new StoreDestinationProvider(store), printFactory, new RemoteConfigState(), NullLogger.Instance),
            new FakeEnumeratedPrinters(),
            new RemoteConfigState(),
            new AgentRuntimeStatus(),
            new AgentOptions { DataDirectory = dataDirectory },
            NullLogger<ManagementRequestHandler>.Instance);

        var pipe = new ManagementPipeServer(handler, NullLogger<ManagementPipeServer>.Instance, pipeName: $"orp-applier-{Guid.NewGuid():N}");
        return (pipe, handler);
    }

    /// <summary>Fake local de impresoras (sin impresoras: no se usa en estos tests).</summary>
    private sealed class FakeEnumeratedPrinters : IPrinterEnumerator
    {
        public IReadOnlyList<PrinterInfo> List() => [];

        public PrinterInfo? Find(string printerName) => null;

        public string? DefaultPrinterName() => null;
    }

    [Fact]
    public async Task Ejecuta_el_instalador_tras_parar_el_poller()
    {
        var poller = CreateIdlePoller();
        var (pipe, _) = CreatePipe();
        var status = new UpdateStatus();
        List<string> executed = [];
        var applier = new UpdateApplier(
            poller,
            pipe,
            status,
            logger: NullLogger.Instance,
            msiRunner: path =>
            {
                executed.Add(path);
                return Task.FromResult(0);
            });

        var update = Prepare();
        await applier.ApplyAsync(update, CancellationToken.None);

        var executedPath = Assert.Single(executed);
        Assert.Equal(update.InstallerPath, executedPath);
        Assert.Equal("applied", status.Snapshot().Result);
        Assert.Equal("1.2.0", status.Snapshot().Version);
        Assert.False(poller.IsRunning); // el poller quedó parado (no hay impresión en vuelo)
    }

    [Fact]
    public async Task Exit_code_3010_es_exito_con_aviso_de_reinicio()
    {
        var applier = CreateApplier(_ => 3010, out var status);
        await applier.ApplyAsync(Prepare(), CancellationToken.None);
        Assert.Equal("reboot_required", status.Snapshot().Result);
    }

    [Fact]
    public async Task Cualquier_otro_exit_code_falla_y_registra_el_estado()
    {
        var applier = CreateApplier(_ => 1603, out var status);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => applier.ApplyAsync(Prepare(), CancellationToken.None));
        Assert.Equal("update_msiexec_failed", ex.Code);
        var (_, result, version, error) = status.Snapshot();
        Assert.Equal("failed", result);
        Assert.Contains("update_msiexec_failed", error);
    }

    private static UpdateApplier CreateApplier(Func<string, int> runner, out UpdateStatus status)
    {
        var poller = CreateIdlePoller();
        var (pipe, _) = CreatePipe();
        status = new UpdateStatus();
        return new UpdateApplier(
            poller,
            pipe,
            status,
            logger: NullLogger.Instance,
            msiRunner: path => Task.FromResult(runner(path)));
    }
}
