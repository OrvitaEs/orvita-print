using System.Text.Json;
using Orvita.Print.Cli;
using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Cli.Tests;

/// <summary>
/// Parser de <c>orvitaprint</c> (F2-09): subcomandos y opciones, validación S-03 de la URL
/// ANTES de enviar, ayuda por comando y errores de uso con su mensaje en español. Sin pipe ni
/// servicio: es puro.
/// </summary>
public sealed class CliParserTests
{
    // ------------------------------------------------------------------------------- pair

    [Fact]
    public void Pair_posicional_con_server_se_parsea()
    {
        var outcome = CliParser.Parse(["pair", "--server", "https://tienda.example/wp-json/orp-print/v1", "482913"]);

        var command = Assert.IsType<PairCommand>(outcome.Command);
        Assert.Equal("https://tienda.example/wp-json/orp-print/v1", command.ServerUrl);
        Assert.Equal("482913", command.Code);
        Assert.Null(command.AgentName);
        Assert.False(command.AllowInsecureHttp);
        Assert.Equal(ManagementProtocol.PipeName, outcome.PipeName); // pipe estándar
    }

    [Fact]
    public void Pair_acepta_nombre_y_bandera_insegura_en_cualquier_orden()
    {
        var outcome = CliParser.Parse(["pair", "000111", "--name", "TPV Barra", "--allow-insecure-http", "--server", "http://localhost/wp-json/orp-print/v1"]);

        var command = Assert.IsType<PairCommand>(outcome.Command);
        Assert.Equal("http://localhost/wp-json/orp-print/v1", command.ServerUrl);
        Assert.Equal("000111", command.Code);
        Assert.Equal("TPV Barra", command.AgentName);
        Assert.True(command.AllowInsecureHttp);
    }

    [Fact]
    public void S03_http_sin_bandera_se_rechaza_antes_de_enviar()
    {
        var outcome = CliParser.Parse(["pair", "--server", "http://tienda.example/wp-json/orp-print/v1", "482913"]);

        Assert.Null(outcome.Command); // no llega a la pipe
        Assert.NotNull(outcome.Error);
        Assert.Contains("S-03", outcome.Error);
        Assert.Contains("--allow-insecure-http", outcome.Error);
    }

    [Fact]
    public void S03_https_pasa_y_esquemas_raros_fallan()
    {
        Assert.IsType<PairCommand>(CliParser.Parse(["pair", "--server", "https://t.example/v1", "123456"]).Command);
        Assert.Null(CliParser.Parse(["pair", "--server", "ftp://t.example", "123456"]).Command);
        Assert.Null(CliParser.Parse(["pair", "--server", "tienda.example/v1", "123456"]).Command);
    }

    [Fact]
    public void Pair_sin_server_o_sin_codigo_da_error_de_uso()
    {
        Assert.NotNull(CliParser.Parse(["pair", "482913"]).Error);
        Assert.NotNull(CliParser.Parse(["pair", "--server", "https://t.example/v1"]).Error);
        Assert.NotNull(CliParser.Parse(["pair", "--server", "https://t.example/v1", "123", "456"]).Error); // dos posicionales
        Assert.NotNull(CliParser.Parse(["pair", "--noexiste", "123456"]).Error);
    }

    // ------------------------------------------------------------------------- destination

    [Fact]
    public void Destination_add_con_valores_por_defecto()
    {
        var outcome = CliParser.Parse(["destination", "add", "--printer", "EPSON TM-T20III"]);

        var command = Assert.IsType<DestinationAddCommand>(outcome.Command);
        Assert.Equal("default", command.Role); // rol por defecto
        Assert.Equal("EPSON TM-T20III", command.PrinterName);
        Assert.Equal(1, command.Copies);
        Assert.True(command.Enabled);
        Assert.Equal("auto", command.Kind);
        Assert.Null(command.Label);
    }

    [Fact]
    public void Destination_add_completo_y_tcp_con_address()
    {
        var outcome = CliParser.Parse([
            "destination", "add", "--printer", "XPRINTER XP-N160II", "--role", "cocina",
            "--copies", "3", "--label", "Cocina", "--kind", "tcp", "--address", "192.168.1.50:9100", "--enabled", "false",
        ]);

        var command = Assert.IsType<DestinationAddCommand>(outcome.Command);
        Assert.Equal("cocina", command.Role);
        Assert.Equal(3, command.Copies);
        Assert.Equal("Cocina", command.Label);
        Assert.Equal("tcp", command.Kind);
        Assert.Equal("192.168.1.50:9100", command.Address);
        Assert.False(command.Enabled);
    }

    [Fact]
    public void Destination_add_errores_de_uso()
    {
        Assert.NotNull(CliParser.Parse(["destination", "add"]).Error); // sin printer ni address
        Assert.NotNull(CliParser.Parse(["destination", "add", "--printer", "X", "--copies", "0"]).Error);
        Assert.NotNull(CliParser.Parse(["destination", "add", "--printer", "X", "--copies", "100"]).Error);
        Assert.NotNull(CliParser.Parse(["destination", "add", "--printer", "X", "--kind", "holograma"]).Error);
        Assert.NotNull(CliParser.Parse(["destination", "add", "--printer", "X", "--enabled", "quizás"]).Error);
    }

    [Fact]
    public void Destination_list_y_remove()
    {
        Assert.IsType<DestinationListCommand>(CliParser.Parse(["destination", "list"]).Command);

        var remove = Assert.IsType<DestinationRemoveCommand>(CliParser.Parse(["destination", "remove", "--role", "cocina"]).Command);
        Assert.Equal("cocina", remove.Role);

        Assert.NotNull(CliParser.Parse(["destination", "remove"]).Error); // falta --role
        Assert.NotNull(CliParser.Parse(["destination", "churrasco"]).Error); // subcomando desconocido
        Assert.NotNull(CliParser.Parse(["destination"]).Error); // falta subcomando
    }

    // --------------------------------------------------------------------- resto de comandos

    [Fact]
    public void Status_test_doctor_y_restart_se_parsean()
    {
        Assert.IsType<StatusCommand>(CliParser.Parse(["status"]).Command);
        Assert.IsType<DoctorCommand>(CliParser.Parse(["doctor"]).Command);
        Assert.IsType<RestartPollerCommand>(CliParser.Parse(["restart-poller"]).Command);

        var test = Assert.IsType<TestCommand>(CliParser.Parse(["test", "--role", "cocina"]).Command);
        Assert.Equal("cocina", test.Role);
        Assert.Null(Assert.IsType<TestCommand>(CliParser.Parse(["test"]).Command).Role);

        Assert.NotNull(CliParser.Parse(["test", "--extra"]).Error);
    }

    [Fact]
    public void Comando_desconocido_y_sin_argumentos_muestran_ayuda_o_error()
    {
        var sinArgs = CliParser.Parse([]);
        Assert.Null(sinArgs.Command);
        Assert.NotNull(sinArgs.Help);

        var desconocido = CliParser.Parse(["volar"]);
        Assert.Null(desconocido.Command);
        Assert.Contains("desconocido", desconocido.Error);
    }

    [Fact]
    public void Ayuda_por_comando()
    {
        Assert.Contains("pair", CliParser.Parse(["pair", "--help"]).Help!);
        Assert.Contains("destination", CliParser.Parse(["destination", "add", "--help"]).Help!);
        Assert.Contains("doctor", CliParser.Parse(["doctor", "-h"]).Help!);
        Assert.Contains("Comandos", CliParser.Parse(["--help"]).Help!);
        Assert.Contains("pair", CliParser.Parse(["help", "pair"]).Help!);
    }

    [Fact]
    public void Pipe_alternativa_se_recoge_como_opcion_global()
    {
        var outcome = CliParser.Parse(["--pipe", "orvitaprint-test-xyz", "status"]);
        Assert.Equal("orvitaprint-test-xyz", outcome.PipeName);
        Assert.IsType<StatusCommand>(outcome.Command);
    }

    // ----------------------------------------------------------- request builder (mapeo pipe)

    [Fact]
    public void PairCommand_serializa_args_en_snake_case()
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            server = "https://a.example/v1",
            code = "482913",
            agent_name = "TPV",
            allow_insecure_http = true,
        }, ManagementProtocol.Options);

        Assert.Equal("TPV", args.GetProperty("agent_name").GetString());
        Assert.True(args.GetProperty("allow_insecure_http").GetBoolean());
    }
}
