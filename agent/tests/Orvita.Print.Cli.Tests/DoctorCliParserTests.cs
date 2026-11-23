using Orvita.Print.Cli;
using Xunit;

namespace Orvita.Print.Cli.Tests;

/// <summary>F2-12: parseo de <c>orvitaprint doctor</c> con <c>--zip</c> y <c>--out &lt;ruta&gt;</c>.</summary>
public sealed class DoctorCliParserTests
{
    [Fact]
    public void Doctor_sin_flags_es_solo_resumen()
    {
        var outcome = CliParser.Parse(["doctor"]);

        var command = Assert.IsType<DoctorCommand>(outcome.Command);
        Assert.False(command.Zip);
        Assert.Null(command.OutPath);
    }

    [Fact]
    public void Doctor_zip_pide_el_informe_completo()
    {
        var outcome = CliParser.Parse(["doctor", "--zip"]);

        var command = Assert.IsType<DoctorCommand>(outcome.Command);
        Assert.True(command.Zip);
        Assert.Null(command.OutPath);
    }

    [Fact]
    public void Doctor_out_implica_zip()
    {
        var outcome = CliParser.Parse(["doctor", "--out", "C:\\diag\\orvitaprint.zip"]);

        var command = Assert.IsType<DoctorCommand>(outcome.Command);
        Assert.True(command.Zip);
        Assert.Equal("C:\\diag\\orvitaprint.zip", command.OutPath);
    }

    [Fact]
    public void Doctor_out_sin_ruta_es_error_de_uso()
    {
        var outcome = CliParser.Parse(["doctor", "--out"]);

        Assert.Null(outcome.Command);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public void Doctor_con_argumento_inesperado_es_error_de_uso()
    {
        Assert.NotNull(CliParser.Parse(["doctor", "extra"]).Error);
        Assert.NotNull(CliParser.Parse(["doctor", "--algo"]).Error);
    }
}
