using Orvita.Print.Core;
using Orvita.Print.Gui.Preferences;
using Orvita.Print.Gui.ViewModels;
using Xunit;

namespace Orvita.Print.Gui.Tests;

/// <summary>
/// Pestañas «Salidas» y «Emparejamiento» contra un doble de la pipe (GUI-05): el contrato es
/// el MISMO que el CLI; la impresora duplicada avisa; S-03 valida la URL; desemparejar pasa
/// por la pipe con confirmación.
/// </summary>
public sealed class DestinationsAndPairingViewModelTests
{
    private readonly FakeManagementClient _client = new();

    // --------------------------------------------------------------------- salidas

    [Fact]
    public void Salidas_e_impresoras_se_cargan_de_destination_list()
    {
        _client.RespondOk(ManagementProtocol.Operations.DestinationList, new
        {
            destinations = new object[]
            {
                new { role = "default", label = "Mostrador", printer_name = "EPSON TM-T20III", copies = 2, enabled = true, kind = "auto" },
            },
            printers = new object[]
            {
                new { name = "EPSON TM-T20III", @default = true, hint = (string?)"Lista" },
                new { name = "Xprinter XP-N160II", @default = false, hint = (string?)null },
            },
            default_printer = "EPSON TM-T20III",
        });

        var vm = new DestinationsViewModel(_client);
        vm.Load();

        var salida = Assert.Single(vm.Destinations);
        Assert.Equal("default", salida.Role);
        Assert.Equal(2, salida.Copies);
        Assert.True(salida.Enabled);
        Assert.False(salida.IsDemo);
        Assert.Equal(2, vm.Printers.Count);
        Assert.True(vm.Printers[0].IsDefault);
        Assert.Equal("EPSON TM-T20III (predeterminada)", vm.Printers[0].Display);
        Assert.Null(vm.DuplicateNote); // una sola salida: sin gemelas
    }

    [Fact]
    public void Guardar_salida_manda_destination_add_y_muestra_los_avisos_del_servicio()
    {
        _client.RespondOk(ManagementProtocol.Operations.DestinationAdd, new
        {
            role = "default",
            action = "created",
            printer_found = true,
            warnings = new[] { "La salida «cocina» ya apunta a la misma impresora: para dos copias usa --copies (Doc/03 §6.7)." },
        });

        var vm = new DestinationsViewModel(_client)
        {
            FormRole = "default",
            FormKind = "auto",
            FormPrinter = new PrinterOption { Name = "EPSON TM-T20III" },
        };
        var saved = vm.Save();

        Assert.True(saved);
        // Save encadenó una recarga (destination.add + destination.list): el add es el primero.
        var (op, args) = _client.Requests[0];
        Assert.Equal(ManagementProtocol.Operations.DestinationAdd, op);
        Assert.Equal("default", args!.Value.GetProperty("role").GetString());
        Assert.Equal("EPSON TM-T20III", args.Value.GetProperty("printer").GetString());
        Assert.Contains("--copies", vm.WarningsText); // aviso del servicio VISIBLE en la GUI
    }

    [Fact]
    public void Guardar_salida_sin_impresora_elegida_no_manda_nada()
    {
        var vm = new DestinationsViewModel(_client) { FormKind = "auto", FormPrinter = null };

        Assert.False(vm.Save());
        Assert.Empty(_client.Requests); // ni se intenta: la GUI lo valida antes
    }

    [Fact]
    public void Salida_pdf_va_marcada_como_demo_y_sin_impresora_del_selector()
    {
        _client.RespondOk(ManagementProtocol.Operations.DestinationAdd, new
        {
            role = "demo",
            action = "created",
            printer_found = true,
            warnings = Array.Empty<string>(),
        });

        var vm = new DestinationsViewModel(_client) { FormRole = "demo", FormKind = "pdf", FormAddress = @"C:\demos" };
        vm.Save();

        var args = _client.Requests[0].Args!.Value; // el destination.add es el primero (luego Load recarga)
        Assert.Equal("pdf", args.GetProperty("kind").GetString());
        Assert.False(args.TryGetProperty("printer", out _)); // la impresora la fija el servicio
        Assert.Equal(@"C:\demos", args.GetProperty("address").GetString());

        // Y la FILA de una salida pdf se muestra como demo.
        var demo = new Orvita.Print.Gui.ViewModels.DestinationRow { Role = "demo", Kind = "pdf", Address = @"C:\demos" };
        Assert.True(demo.IsDemo);
        Assert.Contains("solo demos", demo.KindDisplay);
    }

    [Fact]
    public void Dos_salidas_a_la_misma_impresora_producen_aviso_en_la_lista()
    {
        var destinations = new[]
        {
            new Orvita.Print.Gui.ViewModels.DestinationRow { Role = "default", PrinterName = "EPSON TM-T20III" },
            new Orvita.Print.Gui.ViewModels.DestinationRow { Role = "cocina", PrinterName = "EPSON TM-T20III" },
        };

        var note = DestinationsViewModel.DuplicatePrinterNote(destinations);

        Assert.NotNull(note);
        Assert.Contains("default", note);
        Assert.Contains("cocina", note);
        Assert.Contains("misma impresora", note);
    }

    [Fact]
    public void Dos_salidas_pdf_comparten_impresora_por_diseno_sin_aviso()
    {
        var destinations = new[]
        {
            new Orvita.Print.Gui.ViewModels.DestinationRow { Role = "demo1", Kind = "pdf", PrinterName = "Microsoft Print to PDF" },
            new Orvita.Print.Gui.ViewModels.DestinationRow { Role = "demo2", Kind = "pdf", PrinterName = "Microsoft Print to PDF" },
        };

        Assert.Null(DestinationsViewModel.DuplicatePrinterNote(destinations));
    }

    [Fact]
    public async Task Quitar_salida_pide_confirmacion_y_manda_destination_remove()
    {
        _client.RespondOk(ManagementProtocol.Operations.DestinationList, new
        {
            destinations = new object[] { new { role = "default", kind = "auto", copies = 1, enabled = true } },
            printers = Array.Empty<object>(),
        });
        _client.RespondOk(ManagementProtocol.Operations.DestinationRemove, new { role = "default", removed = true });
        var vm = new DestinationsViewModel(_client)
        {
            ConfirmRemove = message =>
            {
                Assert.Contains("«default»", message);
                return Task.FromResult(true);
            },
        };
        vm.Load();
        var before = _client.Requests.Count;

        await vm.RemoveAsync(vm.Destinations[0]);

        var remove = _client.Requests.Skip(before).First();
        Assert.Equal(ManagementProtocol.Operations.DestinationRemove, remove.Op);
        Assert.Equal("default", remove.Args!.Value.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Quitar_salida_sin_confirmacion_no_toca_la_pipe()
    {
        var vm = new DestinationsViewModel(_client) { ConfirmRemove = _ => Task.FromResult(false) };
        vm.Destinations.Add(new Orvita.Print.Gui.ViewModels.DestinationRow { Role = "default" });

        await vm.RemoveAsync(vm.Destinations[0]);

        Assert.Empty(_client.Requests);
    }

    // ---------------------------------------------------------------- emparejamiento

    [Fact]
    public async Task Pair_con_url_no_https_y_sin_modo_dev_no_sale_por_la_pipe_S03()
    {
        var vm = new PairingViewModel(_client) { ServerUrlInput = "http://tienda.example/v1", CodeInput = "482913" };

        await vm.PairAsync();

        Assert.Empty(_client.Requests); // validación S-03 en el cliente
        Assert.True(vm.IsError);
        Assert.Contains("HTTPS", vm.Message);
    }

    [Fact]
    public async Task Pair_con_codigo_que_no_son_6_digitos_no_sale_por_la_pipe()
    {
        var vm = new PairingViewModel(_client) { ServerUrlInput = "https://tienda.example/v1", CodeInput = "48a1" };

        await vm.PairAsync();

        Assert.Empty(_client.Requests);
        Assert.Contains("6 dígitos", vm.Message);
    }

    [Fact]
    public async Task Pair_valido_manda_server_y_code_y_confirma_con_el_nombre_del_local()
    {
        _client.RespondOk(ManagementProtocol.Operations.Pair, new
        {
            server_url = "https://tienda.example/v1/",
            location = new { id = 3L, code = "CENTRO", name = "Cafetería Centro" },
            poll_interval = 10,
        });
        var vm = new PairingViewModel(_client)
        {
            ServerUrlInput = "https://tienda.example/v1",
            CodeInput = " 482913 ",
        };

        await vm.PairAsync();

        var (op, args) = Assert.Single(_client.Requests);
        Assert.Equal(ManagementProtocol.Operations.Pair, op);
        Assert.Equal("https://tienda.example/v1/", args!.Value.GetProperty("server").GetString());
        Assert.Equal("482913", args.Value.GetProperty("code").GetString());
        Assert.False(vm.IsError);
        Assert.Contains("Cafetería Centro", vm.Message);
        Assert.True(vm.Paired);
        Assert.Null(vm.CodeInput); // el código no queda pegado en el formulario
    }

    [Fact]
    public async Task Pair_rechazado_por_el_servidor_muestra_el_error_contractual()
    {
        _client.RespondError(ManagementProtocol.Operations.Pair, "El código no existe o caducó.", "orp_pairing_code_invalid");
        var vm = new PairingViewModel(_client) { ServerUrlInput = "https://tienda.example/v1", CodeInput = "482913" };

        await vm.PairAsync();

        Assert.True(vm.IsError);
        Assert.Contains("caducó", vm.Message);
        Assert.False(vm.Paired);
    }

    [Fact]
    public void El_estado_del_emparejamiento_se_pinta_desde_status_S05()
    {
        var vm = new PairingViewModel(_client);
        vm.ApplyStatus(new Orvita.Print.Gui.Models.StatusSnapshot
        {
            Paired = true,
            ServerUrl = "https://tienda.example/v1/",
            LocationName = "Cafetería Centro",
            LastSeenByServer = new DateTimeOffset(2026, 11, 24, 11, 55, 0, TimeSpan.Zero),
        });

        Assert.True(vm.Paired);
        Assert.Contains("Cafetería Centro", vm.StatusText);
        Assert.Contains("https://tienda.example/v1/", vm.StatusText);   // la URL NO es secreta
        Assert.DoesNotContain("token", vm.StatusText);                  // S-05
        Assert.Contains("Última conexión", vm.StatusText);
    }

    [Fact]
    public async Task Desemparejar_pide_confirmacion_y_manda_pair_remove()
    {
        _client.RespondOk(ManagementProtocol.Operations.PairRemove, new { removed = true });
        var vm = new PairingViewModel(_client)
        {
            Paired = true,
            ConfirmUnpair = message =>
            {
                Assert.Contains("Desemparejar", message);
                return Task.FromResult(true);
            },
        };

        await vm.UnpairAsync();

        var (op, _) = Assert.Single(_client.Requests);
        Assert.Equal(ManagementProtocol.Operations.PairRemove, op);
        Assert.False(vm.Paired);
        Assert.False(vm.IsError);
        Assert.Contains("desemparejado", vm.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Desemparejar_cancelado_no_toca_la_pipe()
    {
        var vm = new PairingViewModel(_client) { Paired = true, ConfirmUnpair = _ => Task.FromResult(false) };

        await vm.UnpairAsync();

        Assert.Empty(_client.Requests);
    }

    // ------------------------------------------------------- validación de URL (S-03)

    [Theory]
    [InlineData("https://tienda.example/wp-json/orp-print/v1", true)]
    [InlineData("https://tienda.example/wp-json/orp-print/v1/", true)]
    [InlineData("http://tienda.example/v1", true)]      // con modo dev explícito se pasa
    [InlineData("ftp://tienda.example", false)]
    [InlineData("tienda.example", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Validacion_de_url_del_cliente_coincide_con_la_del_servicio(string? url, bool expected)
    {
        var valid = ServerUrlValidator.Validate(url, allowInsecureHttp: true) is not null;
        Assert.Equal(expected, valid);

        // Y el servicio aplica la MISMA regla (comprobación cruzada con el código real):
        if (valid && url is not null)
        {
            var serviceUri = Orvita.Print.Service.PairingService.ValidateServerUrl(url, allowInsecureHttp: true);
            Assert.Equal(ServerUrlValidator.Validate(url, true)!.ToString(), serviceUri.ToString());
        }
    }

    [Fact]
    public void Http_sin_modo_dev_falla_en_el_cliente_y_en_el_servicio_igual()
    {
        Assert.Null(ServerUrlValidator.Validate("http://tienda.example/v1", allowInsecureHttp: false));
        Assert.Throws<ArgumentException>(() =>
            Orvita.Print.Service.PairingService.ValidateServerUrl("http://tienda.example/v1", allowInsecureHttp: false));
    }
}
