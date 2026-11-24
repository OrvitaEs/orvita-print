using Orvita.Print.Contracts;
using Orvita.Print.Core;

namespace Orvita.Print.Gui.Tests;

/// <summary>
/// Dobles mínimos del servicio para armar el <c>ManagementRequestHandler</c> en las pruebas
/// de roundtrip de pipe (GUI-08): NO dependen del proyecto Service.Tests (que arrastra xunit
/// 2.9 y rompería el framework headless, fijado a xunit 2.4 por Avalonia).
/// </summary>
public sealed class FakePipePrinterEnumerator(params string[] names) : IPrinterEnumerator
{
    private readonly List<PrinterInfo> _printers = [.. names.Select(n => new PrinterInfo { Name = n })];

    public IReadOnlyList<PrinterInfo> List() => _printers;

    public string? DefaultPrinterName() => _printers.FirstOrDefault()?.Name;

    public PrinterInfo? Find(string name) => _printers.FirstOrDefault(p =>
        string.Equals(p.Name, name, StringComparison.Ordinal));
}

/// <summary>Transporte de impresión falso: cuenta llamadas (nunca toca una impresora real).</summary>
public sealed class FakeGuiPrintTransport : IPrintTransport
{
    public int Calls { get; private set; }

    public Task PrintAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.CompletedTask;
    }
}

public sealed class FakeGuiPrintTransportFactory : IPrintTransportFactory
{
    public FakeGuiPrintTransport Transport { get; } = new();

    public IPrintTransport Create(DestinationOptions destination) => Transport;
}

public sealed class FakeGuiPngDecoder : IPngDecoder
{
    public PngRaster? TryDecodeToRaster(string base64Data, int? targetWidthDots = null, Action<string>? log = null) => new()
    {
        WidthDots = 8,
        HeightDots = 1,
        Bitmap1Bpp = [0x80],
    };
}
