using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Printing.Windows;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// Despachador de operaciones de la pipe (F2-08): destination add/list/remove con validaciones
/// y avisos, status OCULTANDO el token (S-05), test con salida inexistente, restart-poller sin
/// emparejar, doctor resumido y op desconocida. Sin pipe real: eso es ManagementPipeServerTests.
/// </summary>
public sealed class ManagementRequestHandlerTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"orp-handler-{Guid.NewGuid():N}.db");
    private readonly LocalConfigStore _store;
    private readonly OutboxRepository _outbox;
    private readonly ServerConnection _connection;
    private readonly PairingWaiter _waiter;
    private readonly AgentRuntimeStatus _runtime;
    private readonly FakePrinterEnumerator _printers;
    private readonly PollerController _poller;
    private readonly ManagementRequestHandler _handler;

    public ManagementRequestHandlerTests()
    {
        _store = new LocalConfigStore(_databasePath);
        _outbox = new OutboxRepository(_databasePath);
        _connection = new ServerConnection(new AgentOptions(), NullLogger<ServerConnection>.Instance);
        _waiter = new PairingWaiter();
        _runtime = new AgentRuntimeStatus();
        _printers = new FakePrinterEnumerator("EPSON TM-T20III", "Xprinter XP-N160II");
        var printTransportFactory = new FakePrintTransportFactory(new FakePrintTransport());
        _poller = new PollerController(
            () => _connection.Transport!,
            new JobProcessor(
                () => _connection.Transport!,
                _outbox,
                new StoreDestinationProvider(_store),
                printTransportFactory,
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
            _waiter,
            _runtime,
            new StoreDestinationProvider(_store),
            NullLogger<PairingService>.Instance);
        _handler = new ManagementRequestHandler(
            _connection,
            _store,
            _outbox,
            pairingService,
            _poller,
            new TestPrintService(
                new StoreDestinationProvider(_store),
                printTransportFactory,
                new RemoteConfigState(),
                NullLogger.Instance),
            _printers,
            new RemoteConfigState(),
            _runtime,
            new AgentOptions { DataDirectory = Path.GetTempPath() },
            NullLogger<ManagementRequestHandler>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        _outbox.Dispose();
        File.Delete(_databasePath);
    }

    private ManagementRequest Request(string op, object? args) => new()
    {
        Id = 7,
        Op = op,
        Args = args is null ? null : JsonSerializer.SerializeToElement(args, ManagementProtocol.Options),
    };

    private async Task PairOnceAsync()
    {
        // Emparejamiento directo por el almacén+transporte (lo contractual ya lo prueba PairingServiceTests).
        _store.SetPairing(new PairingInfo { ServerUrl = "https://tienda.example/v1/", TokenProtected = "CIFRADO" });
        _connection.SetTransport(new Uri("https://tienda.example/v1/"), "orp_token_test", allowInsecureHttp: false);
    }

    // ---------------------------------------------------------------------- destination add

    [Fact]
    public async Task Destination_add_crea_salida_con_nombre_exacto_sin_avisos()
    {
        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationAdd, new
        {
            role = "default",
            printer = "EPSON TM-T20III",
            copies = 2,
            label = "Mostrador",
        }), CancellationToken.None);

        Assert.True(response.Ok);
        var data = response.Data!.Value;
        Assert.Equal("created", data.GetProperty("action").GetString());
        Assert.Equal("default", data.GetProperty("role").GetString());
        Assert.True(data.GetProperty("printer_found").GetBoolean());
        Assert.False(data.GetProperty("warnings").GetArrayLength() > 0);

        var guardada = _store.ListDestinations().Single();
        Assert.Equal("EPSON TM-T20III", guardada.PrinterName);
        Assert.Equal(2, guardada.Copies);
    }

    [Fact]
    public async Task Destination_add_con_role_existente_reemplaza_y_avisa_de_impresora_repetida()
    {
        await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationAdd, new { role = "cocina", printer = "EPSON TM-T20III" }), CancellationToken.None);

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationAdd, new { role = "default", printer = "EPSON TM-T20III", copies = 1 }), CancellationToken.None);

        Assert.True(response.Ok);
        // «default» es nueva (creada); la repetición de impresora genera el aviso (Doc/03 §6.7).
        Assert.Equal("created", response.Data!.Value.GetProperty("action").GetString());
        var warnings = response.Data!.Value.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains("cocina", warning);
        Assert.Contains("--copies", warning);
        Assert.Equal(2, _store.ListDestinations().Count); // roles distintos: dos salidas

        // Y si se repite el role EXISTENTE, se reemplaza sin duplicar (upsert por role).
        var reemplazo = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationAdd, new { role = "cocina", printer = "EPSON TM-T20III", copies = 4 }), CancellationToken.None);
        Assert.True(reemplazo.Ok);
        Assert.Equal("updated", reemplazo.Data!.Value.GetProperty("action").GetString());
        Assert.Equal(2, _store.ListDestinations().Count);
        Assert.Equal(4, _store.ListDestinations().Single(d => d.Role == "cocina").Copies);
    }

    [Fact]
    public async Task Destination_add_con_impresora_desconocida_guarda_con_aviso_explicito()
    {
        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationAdd, new { printer = "NO EXISTE SL-500" }), CancellationToken.None);

        Assert.True(response.Ok);
        Assert.False(response.Data!.Value.GetProperty("printer_found").GetBoolean());
        var warning = Assert.Single(response.Data!.Value.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        Assert.Contains("no aparece entre las instaladas", warning);
        Assert.Contains("EXACTO", warning); // Doc/03 §6.6
    }

    [Fact]
    public async Task Destination_add_sin_impresoras_instaladas_da_el_texto_explicito()
    {
        _printers.Clear();

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationAdd, new { printer = "EPSON" }), CancellationToken.None);

        Assert.True(response.Ok);
        var warning = Assert.Single(response.Data!.Value.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        Assert.Contains("No hay impresoras instaladas en este equipo", warning); // Doc/03 §6.6
    }

    [Fact]
    public async Task Destination_add_tcp_exige_address_y_sin_printer_es_valido()
    {
        var fallido = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationAdd, new { printer = "", kind = "tcp" }), CancellationToken.None);
        Assert.False(fallido.Ok);
        Assert.Contains("--address", fallido.Error);

        var valido = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationAdd, new { role = "red", kind = "tcp", address = "192.168.1.50:9100" }), CancellationToken.None);
        Assert.True(valido.Ok);
        Assert.Empty(valido.Data!.Value.GetProperty("warnings").EnumerateArray()); // sin printer_name no se valida impresora
        Assert.Equal(PrintTransportKind.Tcp9100, _store.ListDestinations().Single().Kind);
    }

    [Fact]
    public async Task Destination_add_sin_printer_ni_address_falla_con_mensaje_amable()
    {
        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationAdd, null), CancellationToken.None);
        Assert.False(response.Ok);
        Assert.Contains("--printer", response.Error);
        Assert.Contains("destination list", response.Error);
    }

    // ------------------------------------------------------------------------ destination pdf

    [Fact]
    public async Task Destination_add_pdf_fija_la_impresora_de_pdf_y_guarda_el_directorio()
    {
        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationAdd,
            new { role = "demo", kind = "pdf", address = @"C:\demos\pdfs" }), CancellationToken.None);

        Assert.True(response.Ok);
        var destination = _store.ListDestinations().Single();
        Assert.Equal(PrintTransportKind.Pdf, destination.Kind);
        Assert.Equal(PdfTransport.PrinterName, destination.PrinterName); // SIEMPRE la de PDF
        Assert.Equal(@"C:\demos\pdfs", destination.Address);
        Assert.Equal("pdf", LocalConfigStore.ToKindName(destination.Kind));
    }

    [Fact]
    public async Task Destination_add_pdf_sin_address_guarda_el_directorio_por_defecto()
    {
        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationAdd,
            new { role = "demo", kind = "pdf" }), CancellationToken.None);

        Assert.True(response.Ok);
        var destination = _store.ListDestinations().Single();
        Assert.Equal(PdfTransport.DefaultDirectory, destination.Address);
    }

    [Fact]
    public async Task Destination_add_pdf_ignora_otra_impresora_con_aviso()
    {
        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationAdd,
            new { role = "demo", kind = "pdf", address = @"C:\demos\pdfs", printer = "EPSON TM-T20III" }), CancellationToken.None);

        Assert.True(response.Ok);
        var warnings = response.Data!.Value.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
        Assert.Contains(warnings, w => w!.Contains("se ignora", StringComparison.Ordinal) && w.Contains("EPSON", StringComparison.Ordinal));

        var destination = _store.ListDestinations().Single();
        Assert.Equal(PdfTransport.PrinterName, destination.PrinterName); // la petición no la cambia
    }

    [Fact]
    public async Task Destination_add_dos_pdf_no_avisa_de_impresora_repetida()
    {
        await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationAdd,
            new { role = "demo", kind = "pdf", address = @"C:\demos\a" }), CancellationToken.None);
        var segunda = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationAdd,
            new { role = "cocina", kind = "pdf", address = @"C:\demos\b" }), CancellationToken.None);

        Assert.True(segunda.Ok);
        // Dos salidas pdf comparten impresora por diseño (distinto directorio): sin aviso gemelo.
        Assert.DoesNotContain(segunda.Data!.Value.GetProperty("warnings").EnumerateArray(),
            w => w.GetString()!.Contains("misma impresora", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------- destination list/remove

    [Fact]
    public async Task Destination_list_devuelve_salidas_e_impresoras_instaladas()
    {
        _store.UpsertDestination(new DestinationOptions { Role = "default", Label = "Mostrador", PrinterName = "EPSON TM-T20III", Copies = 2, Enabled = true });

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationList, null), CancellationToken.None);

        Assert.True(response.Ok);
        var data = response.Data!.Value;
        Assert.Equal(1, data.GetProperty("destinations").GetArrayLength());
        Assert.Equal(2, data.GetProperty("printers").GetArrayLength());
        Assert.Equal("EPSON TM-T20III", data.GetProperty("default_printer").GetString());
    }

    [Fact]
    public async Task Destination_remove_elimina_y_da_error_si_no_existe()
    {
        _store.UpsertDestination(new DestinationOptions { Role = "cocina", PrinterName = "XPRINTER" });

        var bien = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationRemove, new { role = "cocina" }), CancellationToken.None);
        Assert.True(bien.Ok);
        Assert.False(_store.RemoveDestination("cocina")); // ya no está

        var mal = await _handler.HandleAsync(Request(ManagementProtocol.Operations.DestinationRemove, new { role = "cocina" }), CancellationToken.None);
        Assert.False(mal.Ok);
        Assert.Equal("destination_not_found", mal.Code);
    }

    // ------------------------------------------------------------------------------ status

    [Fact]
    public async Task Status_sin_emparejar_explica_como_emparejar()
    {
        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.Status, null), CancellationToken.None);

        Assert.True(response.Ok);
        var data = response.Data!.Value;
        Assert.False(data.GetProperty("paired").GetBoolean());
        // Sin emparejar, server_url se OMITE (WhenWritingNull): no viaja un campo vacío ni un token.
        Assert.False(data.TryGetProperty("server_url", out _));

        var texto = JsonSerializer.Serialize(response, ManagementProtocol.Options);
        Assert.DoesNotContain("token", texto); // S-05 en el estado sin emparejar
    }

    [Fact]
    public async Task Status_oculta_el_token_asiempre_S05()
    {
        await PairOnceAsync();
        _outbox.Enqueue(1844, "default", 1, "{}");
        _outbox.TryClaim(1844);

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.Status, null), CancellationToken.None);

        Assert.True(response.Ok);
        var texto = JsonSerializer.Serialize(response, ManagementProtocol.Options);
        Assert.DoesNotContain("orp_token_test", texto);   // el token en claro NUNCA sale (S-05)
        Assert.DoesNotContain("token", texto);            // ni siquiera se menciona un campo token
        Assert.True(response.Data!.Value.GetProperty("paired").GetBoolean());
        Assert.Equal("https://tienda.example/v1/", response.Data!.Value.GetProperty("server_url").GetString());
        Assert.Equal(1, response.Data!.Value.GetProperty("pending_local").GetInt32());
        Assert.Equal(1844, response.Data!.Value.GetProperty("last_job").GetProperty("id").GetInt64());
        Assert.Equal("printing", response.Data!.Value.GetProperty("last_job").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Status_con_salida_y_error_recientes_muestra_todo()
    {
        await PairOnceAsync();
        _store.UpsertDestination(new DestinationOptions { Role = "default", PrinterName = "EPSON TM-T20III" });
        _outbox.Enqueue(1844, "default", 1, "{}");
        _outbox.TryClaim(1844);
        _outbox.MarkError(1844, "orp_print_failed", "La impresora «EPSON TM-T20III» no responde.");

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.Status, null), CancellationToken.None);

        var data = response.Data!.Value;
        Assert.Equal(1, data.GetProperty("jobs_error").GetInt32());
        Assert.Contains("no responde", data.GetProperty("last_print_error").GetString());
        Assert.Equal("error", data.GetProperty("last_job").GetProperty("state").GetString());
        Assert.Equal(1, data.GetProperty("destinations").GetArrayLength());
    }

    // ------------------------------------------------------------------------ test / doctor

    [Fact]
    public async Task Test_con_salida_inexistente_da_error_claro()
    {
        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.Test, new { role = "noexiste" }), CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal("print_error", response.Code);
        Assert.Contains("noexiste", response.Error);
        Assert.Contains("destination add", response.Error);
    }

    [Fact]
    public async Task Test_imprime_por_la_salida_resuelta()
    {
        _store.UpsertDestination(new DestinationOptions { Role = "default", PrinterName = "EPSON TM-T20III", Copies = 2 });

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.Test, null), CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal("EPSON TM-T20III", response.Data!.Value.GetProperty("destination").GetString());
        Assert.Equal(2, response.Data!.Value.GetProperty("copies").GetInt32());
        Assert.True(response.Data!.Value.GetProperty("bytes_printed").GetInt32() > 0);
    }

    [Fact]
    public async Task Doctor_da_resumen_sin_secretos()
    {
        await PairOnceAsync();

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.Doctor, null), CancellationToken.None);

        Assert.True(response.Ok);
        var data = response.Data!.Value;
        Assert.NotEmpty(data.GetProperty("version").GetString()!);
        Assert.NotEmpty(data.GetProperty("data_directory").GetString()!);
        Assert.Contains(Path.GetFileName(_databasePath), data.GetProperty("database_path").GetString());
        Assert.True(data.GetProperty("paired").GetBoolean());

        var texto = JsonSerializer.Serialize(response, ManagementProtocol.Options);
        Assert.DoesNotContain("orp_token_test", texto); // S-05
    }

    // ----------------------------------------------------------------------- restart-poller

    [Fact]
    public async Task RestartPoller_sin_emparejar_da_not_paired()
    {
        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.RestartPoller, null), CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal("not_paired", response.Code);
        Assert.Contains("pair", response.Error);
    }

    // ------------------------------------------------------------------------ unpair (GUI-05)

    [Fact]
    public async Task Unpair_sin_emparejar_da_not_paired()
    {
        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.PairRemove, null), CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal("not_paired", response.Code);
        Assert.Contains("no está emparejado", response.Error);
    }

    [Fact]
    public async Task Unpair_para_el_poller_borra_el_emparejamiento_y_conserva_las_salidas()
    {
        await PairOnceAsync();
        _store.UpsertDestination(new DestinationOptions { Role = "default", PrinterName = "EPSON TM-T20III" });
        _outbox.Enqueue(1888, "default", 1, "{}");

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.PairRemove, null), CancellationToken.None);

        Assert.True(response.Ok);
        Assert.True(response.Data!.Value.GetProperty("removed").GetBoolean());

        // Emparejamiento fuera: estado visible claro, sin transporte y sin token (S-05).
        Assert.False(_store.GetPairing().IsPaired);
        Assert.Null(_connection.Transport);
        var texto = JsonSerializer.Serialize(response, ManagementProtocol.Options);
        Assert.DoesNotContain("token", texto);

        // El poller quedó parado (sin token no puede correr).
        Assert.False(_poller.IsRunning);

        // Las salidas se conservan (re-emparejar no las reconfigura) y la cola TAMBIÉN.
        Assert.Single(_store.ListDestinations());
        Assert.NotNull(_outbox.Get(1888));

        // Y un segundo unpair ya no tiene nada que borrar.
        var segundo = await _handler.HandleAsync(Request(ManagementProtocol.Operations.PairRemove, null), CancellationToken.None);
        Assert.False(segundo.Ok);
        Assert.Equal("not_paired", segundo.Code);
    }

    [Fact]
    public async Task RestartPoller_emparejado_reinicia_y_devuelve_estado()
    {
        await PairOnceAsync();

        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.RestartPoller, null), CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal("running", response.Data!.Value.GetProperty("poller_state").GetString());
        await _poller.StopAsync();
    }

    // ----------------------------------------------------------------------------- misceláneo

    [Fact]
    public async Task Operacion_desconocida_y_op_faltante_dan_unknown_operation()
    {
        var desconocida = await _handler.HandleAsync(Request("comer", null), CancellationToken.None);
        Assert.False(desconocida.Ok);
        Assert.Equal("unknown_operation", desconocida.Code);

        var vacia = await _handler.HandleAsync(new ManagementRequest { Id = 1, Op = null }, CancellationToken.None);
        Assert.False(vacia.Ok);
        Assert.Equal("unknown_operation", vacia.Code);
    }

    [Fact]
    public async Task Ping_responde_con_version()
    {
        var response = await _handler.HandleAsync(Request(ManagementProtocol.Operations.Ping, null), CancellationToken.None);
        Assert.True(response.Ok);
        Assert.True(response.Data!.Value.GetProperty("pong").GetBoolean());
        Assert.NotEmpty(response.Data!.Value.GetProperty("version").GetString()!);
    }

    /// <summary>Enumerador falso con impresoras fijas (sin depender del equipo real).</summary>
    private sealed class FakePrinterEnumerator(params string[] names) : IPrinterEnumerator
    {
        private List<PrinterInfo> _printers = [.. names.Select(n => new PrinterInfo { Name = n })];

        public IReadOnlyList<PrinterInfo> List() => _printers;

        public string? DefaultPrinterName() => _printers.FirstOrDefault()?.Name;

        public PrinterInfo? Find(string name) => _printers.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.Ordinal));

        public void Clear() => _printers = [];
    }
}
