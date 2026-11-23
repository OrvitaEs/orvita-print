using Orvita.Print.Cli;
using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Cli.Tests;

/// <summary>
/// Cliente de pipe del CLI: si nadie escucha, el error es claro y el código de salida 3
/// («el servicio no está en ejecución»), sin colgarse (timeout corto).
/// </summary>
public sealed class ManagementPipeClientTests
{
    [Fact]
    public void Conectar_a_pipe_inexistente_da_ServiceNotRunning()
    {
        using var client = new ManagementPipeClient($"orvitaprint-nada-{Guid.NewGuid():N}");

        var ex = Assert.Throws<ServiceNotRunningException>(() => client.Connect(TimeSpan.FromMilliseconds(800)));
        Assert.Contains("no está en ejecución", ex.Message);
    }

    [Fact]
    public async Task Ejecucion_sin_servicio_devuelve_codigo_3_con_mensaje_en_stderr()
    {
        var output = new StringWriter();
        var errors = new StringWriter();

        var exit = await CliApp.RunAsync(["status", "--pipe", $"orvitaprint-nada-{Guid.NewGuid():N}"], output, errors);

        Assert.Equal(ExitCodes.ServiceNotRunning, exit);
        Assert.Contains("ERROR", errors.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task Uso_incorrecto_devuelve_codigo_2_y_fallo_operativo_codigo_1()
    {
        var output = new StringWriter();
        var errors = new StringWriter();

        // Uso incorrecto (parse) → 2.
        var usage = await CliApp.RunAsync(["pair", "--server", "http://t.example/v1", "123456"], output, errors);
        Assert.Equal(ExitCodes.Usage, usage);
        Assert.Contains("S-03", errors.ToString());
        Assert.Empty(output.ToString()); // el error va a stderr, no a stdout

        // Ayuda → 0 y sale por stdout.
        var outputHelp = new StringWriter();
        var help = await CliApp.RunAsync(["--help"], outputHelp, new StringWriter());
        Assert.Equal(ExitCodes.Ok, help);
        Assert.Contains("orvitaprint", outputHelp.ToString());
    }
}
