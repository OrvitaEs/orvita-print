using Microsoft.Extensions.Logging.Abstractions;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// Configuración remota (Doc/04 §5.8): por defecto CP850, encoding y papel aplicados al
/// renderizador, y detección de cambios para el poll interval.
/// </summary>
public sealed class RemoteConfigStateTests
{
    [Fact]
    public void Sin_config_valores_por_defecto_cp850()
    {
        var state = new RemoteConfigState();

        Assert.Null(state.ConfigVersion);
        Assert.Null(state.PollIntervalSeconds);
        Assert.Equal(EscPosEncoding.Cp850, state.ToRendererOptions().TextEncoding);
    }

    [Fact]
    public void Update_aplica_encoding_y_papel_y_detecta_cambios()
    {
        var state = new RemoteConfigState();

        var first = state.Update(
            new AgentConfig { ConfigVersion = 3, Encoding = "CP858", PaperWidthMm = 58, PollIntervalSeconds = 20 },
            NullLogger.Instance);
        Assert.True(first);

        var options = state.ToRendererOptions();
        Assert.Equal(EscPosEncoding.Cp858, options.TextEncoding);
        Assert.Null(options.Columns); // 58 mm: lo deduce el renderizador por documento
        Assert.Equal(20, state.PollIntervalSeconds);
        Assert.Equal(3, state.ConfigVersion);

        // Misma versión y mismos valores: sin cambio.
        var second = state.Update(
            new AgentConfig { ConfigVersion = 3, Encoding = "CP858", PaperWidthMm = 58, PollIntervalSeconds = 20 },
            NullLogger.Instance);
        Assert.False(second);
    }

    [Fact]
    public void Encoding_desconocida_cae_en_cp850()
    {
        var state = new RemoteConfigState();
        state.Update(new AgentConfig { ConfigVersion = 1, Encoding = "CP999" }, NullLogger.Instance);

        Assert.Equal(EscPosEncoding.Cp850, state.ToRendererOptions().TextEncoding);
    }
}

/// <summary>
/// Impresión de prueba (tarea F2-05/F2-07): usa el renderizador real sobre el ticket fijo y
/// el transporte de la salida; falla con mensaje claro si la salida no existe.
/// </summary>
public sealed class TestPrintServiceTests
{
    [Fact]
    public async Task Imprime_el_ticket_de_prueba_por_el_transporte_de_la_salida()
    {
        var print = new FakePrintTransport();
        var service = new TestPrintService(
            new StaticDestinationProvider(new DestinationOptions
            {
                Role = "default",
                Label = "Mostrador",
                PrinterName = "EPSON TM-T20III",
                Copies = 2,
            }),
            new FakePrintTransportFactory(print),
            new RemoteConfigState());

        var result = await service.PrintAsync("default", CancellationToken.None);

        Assert.Equal("EPSON TM-T20III", result.Destination);
        Assert.Equal(2, result.Copies);
        Assert.True(result.BytesPrinted > 0);
        // El documento impreso es el ticket de prueba (cadena obligatoria en él).
        Assert.Contains("Impresión de prueba", print.LastRequest!.Document.Blocks.OfType<TextBlock>().Select(b => b.Value));
        var cp850 = System.Text.Encoding.GetEncoding(850);
        Assert.True(print.LastRequest.EscPos.AsSpan().IndexOf(
            (ReadOnlySpan<byte>)cp850.GetBytes("Pizza Margarita · jamón, piña, ñ, E, áéíóú")) >= 0,
            "El ticket de prueba lleva la cadena de Doc/05 §4.3 (con €→E en CP850).");
    }

    [Fact]
    public async Task Salida_inexistente_print_not_found_con_mensaje_explcito()
    {
        var service = new TestPrintService(
            new StaticDestinationProvider(),
            new FakePrintTransportFactory(new FakePrintTransport()),
            new RemoteConfigState());

        var ex = await Assert.ThrowsAsync<PrintTransportException>(() => service.PrintAsync("cocina", CancellationToken.None));

        Assert.Equal(PrintFailureKind.PrinterNotFound, ex.Kind);
        Assert.Contains("cocina", ex.Message);
    }
}
