using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Printing.Windows;
using Xunit;

namespace Orvita.Print.Printing.Windows.Tests;

/// <summary>Transporte falso para probar la selección sin hardware.</summary>
internal sealed class FakePrintTransport(
    PrintFailureKind? failureKind = null,
    string failureMessage = "fallo simulado") : IPrintTransport
{
    public int Calls { get; private set; }

    public PrintRequest? LastRequest { get; private set; }

    public Task PrintAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        LastRequest = request;
        return failureKind is { } kind
            ? throw new PrintTransportException(kind, failureMessage)
            : Task.CompletedTask;
    }
}

/// <summary>
/// Selección de transporte (Doc/03 §6.5, tarea F2-05): RAW primero; error DEFINITIVO del
/// driver → GDI; transitorio y «impresora no encontrada» se propagan sin fallback; y la
/// fábrica resuelve el modo de cada salida.
/// </summary>
public sealed class PrintTransportSelectionTests
{
    private static DestinationOptions Destination(PrintTransportKind kind = PrintTransportKind.Auto) =>
        new() { Role = "default", Label = "Mostrador", PrinterName = "IMPRESORA FALSA", Kind = kind };

    private static PrintRequest Request() => new()
    {
        JobId = 1844,
        Destination = Destination(),
        Document = new Document { Version = 1, Blocks = [] },
        EscPos = [0x1B, 0x40],
        Copies = 2,
    };

    [Fact]
    public async Task Raw_si_funciona_no_llama_a_gdi()
    {
        var raw = new FakePrintTransport();
        var gdi = new FakePrintTransport();
        var selector = new FallbackPrintTransport(raw, gdi);

        await selector.PrintAsync(Request(), CancellationToken.None);

        Assert.Equal(1, raw.Calls);
        Assert.Equal(0, gdi.Calls);
    }

    [Fact]
    public async Task Error_definitivo_de_raw_fallback_a_gdi()
    {
        var raw = new FakePrintTransport(PrintFailureKind.Definitive, "datatype RAW rechazado");
        var gdi = new FakePrintTransport();
        var selector = new FallbackPrintTransport(raw, gdi);

        await selector.PrintAsync(Request(), CancellationToken.None);

        Assert.Equal(1, raw.Calls);
        Assert.Equal(1, gdi.Calls); // el GDI saca el ticket
    }

    [Fact]
    public async Task Si_gdi_tambien_falla_mensaje_combinado_y_kinds_del_ultimo()
    {
        var raw = new FakePrintTransport(PrintFailureKind.Definitive, "raw roto");
        var gdi = new FakePrintTransport(PrintFailureKind.PrinterNotFound, "gdi roto");
        var selector = new FallbackPrintTransport(raw, gdi);

        var ex = await Assert.ThrowsAsync<PrintTransportException>(() => selector.PrintAsync(Request(), CancellationToken.None));

        Assert.Equal(PrintFailureKind.PrinterNotFound, ex.Kind);
        Assert.Equal("orp_printer_not_found", ex.ErrorCode);
        Assert.Contains("raw roto", ex.Message);
        Assert.Contains("gdi roto", ex.Message);
    }

    [Fact]
    public async Task Fallo_transitorio_no_fallback_gdi_mismo_spooler()
    {
        var raw = new FakePrintTransport(PrintFailureKind.Transient, "spooler ocupado");
        var gdi = new FakePrintTransport();
        var selector = new FallbackPrintTransport(raw, gdi);

        var ex = await Assert.ThrowsAsync<PrintTransportException>(() => selector.PrintAsync(Request(), CancellationToken.None));

        Assert.Equal(PrintFailureKind.Transient, ex.Kind);
        Assert.Equal(1, raw.Calls);
        Assert.Equal(0, gdi.Calls); // el GDI pasa por el mismo spooler: no tiene sentido
    }

    [Fact]
    public async Task Impresora_inexistente_no_fallback_gdi()
    {
        var raw = new FakePrintTransport(PrintFailureKind.PrinterNotFound, "no existe la impresora");
        var gdi = new FakePrintTransport();
        var selector = new FallbackPrintTransport(raw, gdi);

        var ex = await Assert.ThrowsAsync<PrintTransportException>(() => selector.PrintAsync(Request(), CancellationToken.None));

        Assert.Equal(PrintFailureKind.PrinterNotFound, ex.Kind);
        Assert.Equal(0, gdi.Calls);
    }

    [Fact]
    public async Task Sin_fallback_el_error_definitivo_se_propaga()
    {
        var raw = new FakePrintTransport(PrintFailureKind.Definitive);
        var selector = new FallbackPrintTransport(raw, fallback: null);

        var ex = await Assert.ThrowsAsync<PrintTransportException>(() => selector.PrintAsync(Request(), CancellationToken.None));

        Assert.Equal(PrintFailureKind.Definitive, ex.Kind);
    }

    // ------------------------------------------------------------ fábrica

    [Fact]
    public void Fabrica_mapea_cada_modo()
    {
        var factory = new WindowsPrintTransportFactory();

        Assert.IsType<RawSpoolerTransport>(factory.Create(new DestinationOptions { Kind = PrintTransportKind.RawSpooler }));
        Assert.IsType<GdiTransport>(factory.Create(new DestinationOptions { Kind = PrintTransportKind.Gdi }));
        Assert.IsType<Tcp9100Transport>(factory.Create(new DestinationOptions { Kind = PrintTransportKind.Tcp9100 }));
        Assert.IsType<PdfTransport>(factory.Create(new DestinationOptions { Kind = PrintTransportKind.Pdf }));
        Assert.IsType<FallbackPrintTransport>(factory.Create(new DestinationOptions { Kind = PrintTransportKind.Auto }));
        Assert.IsType<FallbackPrintTransport>(factory.Create(new DestinationOptions { Kind = (PrintTransportKind)999 }));
    }
}
