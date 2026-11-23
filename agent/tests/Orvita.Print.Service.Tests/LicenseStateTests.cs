using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// Licencia del agente (F3-02, Doc/03 §10): carga y verificación LOCAL de license.dat con la
/// clave pública inyectada (en producción la embebida de LicenseTrust), estados
/// missing/valid/expired_updates/invalid, cable en status y doctor SIN secretos (S-05) y el
/// bloqueo del canal de actualización cuando updates_until pasó (el agente sigue funcionando).
/// </summary>
public sealed class LicenseStateTests : IDisposable
{
    private static readonly ECDsa TestKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static string SignedLicense(string sku, int maxLocations, string updatesUntil, string customer = "Pizzería Río Rosso") =>
        LicenseDocument.BuildSignedJson($"orp_{Guid.NewGuid():N}", sku, maxLocations, updatesUntil, customer, TestKey);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"orp-license-{Guid.NewGuid():N}");
    private readonly string _licensePath;

    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    public LicenseStateTests()
    {
        Directory.CreateDirectory(_directory);
        _licensePath = Path.Combine(_directory, "license.dat");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Handler real con los fakes de la suite; <paramref name="license"/> va cableado o no.</summary>
    private static ManagementRequestHandler CreateHandler(LicenseState? license)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"orp-license-db-{Guid.NewGuid():N}.db");
        var store = new LocalConfigStore(databasePath);
        var outbox = new OutboxRepository(databasePath);
        var connection = new ServerConnection(new AgentOptions(), NullLogger<ServerConnection>.Instance);
        var printTransportFactory = new FakePrintTransportFactory(new FakePrintTransport());
        var poller = new PollerController(
            () => connection.Transport!,
            new JobProcessor(
                () => connection.Transport!,
                outbox,
                new StoreDestinationProvider(store),
                printTransportFactory,
                rendererOptions: () => new EscPosRendererOptions(),
                pngDecoder: new FakePngDecoder(),
                options: new JobProcessorOptions(),
                logger: NullLogger<JobProcessor>.Instance),
            intervalSeconds: () => 10,
            logger: NullLogger<PollerController>.Instance);
        return new ManagementRequestHandler(
            connection,
            store,
            outbox,
            new PairingService(
                connection,
                store,
                new DpapiSecretProtector(),
                new PairingWaiter(),
                new AgentRuntimeStatus(),
                new StoreDestinationProvider(store),
                NullLogger<PairingService>.Instance),
            poller,
            new TestPrintService(
                new StoreDestinationProvider(store),
                printTransportFactory,
                new RemoteConfigState(),
                NullLogger.Instance),
            new FakePrinterEnumerator("EPSON TM-T20III"),
            new RemoteConfigState(),
            new AgentRuntimeStatus(),
            new AgentOptions { DataDirectory = Path.GetTempPath() },
            NullLogger<ManagementRequestHandler>.Instance,
            diagnostics: null,
            license: license);
    }

    // ----------------------------------------------------------------------------- carga

    [Fact]
    public void Sin_fichero_estado_missing_y_max_efectivo_1()
    {
        var state = LicenseState.Load(_licensePath, Now, publicKey: TestKey);

        Assert.Equal("missing", state.State);
        Assert.Null(state.Error);
        Assert.Equal(1, state.EffectiveMaxLocations);
        Assert.False(state.UpdatesExpired);
    }

    [Fact]
    public void Licencia_valida_carga_campos_y_estado()
    {
        File.WriteAllText(_licensePath, SignedLicense("10", 10, "2027-11-19"));

        var state = LicenseState.Load(_licensePath, Now, publicKey: TestKey);

        Assert.Equal("valid", state.State);
        Assert.Equal("10", state.Sku);
        Assert.Equal(10, state.MaxLocations);
        Assert.Equal("2027-11-19", state.UpdatesUntil);
        Assert.Equal("Pizzería Río Rosso", state.Customer);
        Assert.StartsWith("orp_", state.LicenseId, StringComparison.Ordinal);
        Assert.False(state.UpdatesExpired);
        Assert.Null(state.Error);
    }

    [Fact]
    public void Updates_until_pasado_estado_expired_updates_y_la_licencia_sigue_valiendo()
    {
        File.WriteAllText(_licensePath, SignedLicense("1", 1, "2026-11-22"));

        var state = LicenseState.Load(_licensePath, Now, publicKey: TestKey);

        Assert.Equal("expired_updates", state.State); // NO es invalid: la licencia vale
        Assert.True(state.UpdatesExpired);
        Assert.Equal("Pizzería Río Rosso", state.Customer);
    }

    [Fact]
    public void Fichero_manipulado_estado_invalid_con_codigo()
    {
        File.WriteAllText(_licensePath, SignedLicense("10", 10, "2027-11-19").Replace("Pizzería", "Pizzsería", StringComparison.Ordinal));

        var state = LicenseState.Load(_licensePath, Now, publicKey: TestKey);

        Assert.Equal("invalid", state.State);
        Assert.StartsWith("license_signature_invalid", state.Error, StringComparison.Ordinal);
        Assert.Equal(1, state.EffectiveMaxLocations); // el agente sigue funcionando con 1 local
    }

    [Fact]
    public void Fichero_basura_estado_invalid_sin_lanzar()
    {
        File.WriteAllText(_licensePath, "esto no es un license.dat");

        var state = LicenseState.Load(_licensePath, Now, publicKey: TestKey);

        Assert.Equal("invalid", state.State);
        Assert.NotNull(state.Error);
    }

    // ------------------------------------------------------------- cable en status/doctor

    [Fact]
    public async Task Status_incluye_la_licencia()
    {
        var license = new LicenseState(_licensePath, "valid", licenseId: "orp_x", sku: "10", maxLocations: 10,
            updatesUntil: "2027-11-19", customer: "Pizzería Río Rosso");
        var handler = CreateHandler(license);

        var response = await handler.HandleAsync(new ManagementRequest { Id = 1, Op = ManagementProtocol.Operations.Status }, CancellationToken.None);

        Assert.True(response.Ok);
        var licenseWire = response.Data!.Value.GetProperty("license");
        Assert.Equal(JsonValueKind.Object, licenseWire.ValueKind);
        Assert.Equal("valid", licenseWire.GetProperty("state").GetString());
        Assert.Equal("10", licenseWire.GetProperty("sku").GetString());
        Assert.Equal("Pizzería Río Rosso", licenseWire.GetProperty("customer").GetString());
        Assert.Equal(10, licenseWire.GetProperty("max_locations").GetInt32());
    }

    [Fact]
    public async Task Status_sin_licencia_cableada_omite_la_seccion()
    {
        var handler = CreateHandler(license: null);

        var response = await handler.HandleAsync(new ManagementRequest { Id = 1, Op = ManagementProtocol.Operations.Status }, CancellationToken.None);

        Assert.True(response.Ok);
        Assert.False(response.Data!.Value.TryGetProperty("license", out _), "Sin LicenseState cableado la sección no aparece.");
    }

    [Fact]
    public async Task Doctor_incluye_el_estado_de_licencia()
    {
        var license = new LicenseState(_licensePath, "expired_updates", licenseId: "orp_y", sku: "1", maxLocations: 1,
            updatesUntil: "2026-11-22", customer: "Cliente", updatesExpired: true);
        var handler = CreateHandler(license);

        var response = await handler.HandleAsync(new ManagementRequest { Id = 2, Op = ManagementProtocol.Operations.Doctor }, CancellationToken.None);

        Assert.True(response.Ok);
        var licenseWire = response.Data!.Value.GetProperty("license");
        Assert.Equal("expired_updates", licenseWire.GetProperty("state").GetString());
        Assert.True(licenseWire.GetProperty("updates_expired").GetBoolean());
    }

    [Fact]
    public void ToWire_del_estado_missing_explica_como_activarla()
    {
        var state = new LicenseState(_licensePath, "missing");
        var wire = JsonSerializer.Serialize(state.ToWire(), ManagementProtocol.Options);

        Assert.Contains("license.dat", wire, StringComparison.Ordinal);
        Assert.Contains("reinicia", wire, StringComparison.Ordinal);
    }

    // ------------------------------------------------------ canal de updates (Doc/03 §10)

    [Fact]
    public async Task UpdateService_con_licencia_expirada_no_comprueba_el_canal()
    {
        File.WriteAllText(_licensePath, SignedLicense("1", 1, "2026-11-22"));
        var state = LicenseState.Load(_licensePath, Now, publicKey: TestKey);
        Assert.True(state.UpdatesExpired);

        var checker = new CountingChecker();
        var service = BuildUpdateService(state, checker);
        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);
        await Task.Delay(300, CancellationToken.None); // el primer ciclo corre al instante (FirstCheckDelay = 0)
        cts.Cancel();
        try
        {
            await service.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        await run;

        Assert.Equal(0, checker.Calls); // NO se comprueba: el agente sigue funcionando, sin updates
    }

    [Fact]
    public async Task UpdateService_con_licencia_vigente_comprueba_el_canal()
    {
        File.WriteAllText(_licensePath, SignedLicense("10", 10, "2027-11-19"));
        var state = LicenseState.Load(_licensePath, Now, publicKey: TestKey);
        Assert.False(state.UpdatesExpired);

        var checker = new CountingChecker();
        var service = BuildUpdateService(state, checker);
        using var cts = new CancellationTokenSource();
        var run = service.StartAsync(cts.Token);
        await Task.Delay(300, CancellationToken.None);
        cts.Cancel();
        try
        {
            await service.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        await run;

        Assert.Equal(1, checker.Calls);
    }

    private static UpdateService BuildUpdateService(LicenseState license, CountingChecker checker) =>
        new(
            new UpdateOrchestrator(checker, new FakeApplier(), new UpdateStatus(), NullLogger.Instance),
            new UpdateCheckerOptions { ManifestUrl = "https://updates.orvita.example/agent/latest.json", PublicKeyBase64 = UpdateTrust.ReleasePublicKeyBase64 },
            new UpdateServiceOptions { FirstCheckDelayMinutes = 0, CheckIntervalHours = 24 },
            NullLogger<UpdateService>.Instance,
            license);

    /// <summary>Checker que CUENTA las llamadas y devuelve null (sin novedad) — suficiente aquí.</summary>
    private sealed class CountingChecker : IUpdateChecker
    {
        public int Calls { get; private set; }

        public Task<PreparedUpdate?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<PreparedUpdate?>(null);
        }
    }

    /// <summary>Applier que nunca debería llegar a usarse en estas pruebas.</summary>
    private sealed class FakeApplier : IUpdateApplier
    {
        public Task ApplyAsync(PreparedUpdate update, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakePrinterEnumerator(params string[] names) : IPrinterEnumerator
    {
        public IReadOnlyList<PrinterInfo> List() => [.. names.Select(n => new PrinterInfo { Name = n })];

        public PrinterInfo? Find(string exactName) => List().FirstOrDefault(p => p.Name == exactName);

        public string? DefaultPrinterName() => names.FirstOrDefault();
    }
}
