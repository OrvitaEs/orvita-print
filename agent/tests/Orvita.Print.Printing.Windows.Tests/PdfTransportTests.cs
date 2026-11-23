using System.Text;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Printing.Windows.Tests;

/// <summary>
/// Marca de prueba para la integración REAL con «Microsoft Print to PDF». xUnit evalúa el
/// atributo en el descubrimiento: si la impresora no está instalada, la prueba se SALTA con
/// mensaje (SKIP) en lugar de fallar; si lo está (host Windows), imprime de verdad.
/// </summary>
public sealed class RequiresPdfPrinterFact : FactAttribute
{
    public RequiresPdfPrinterFact()
    {
        if (new WindowsPrinterEnumerator().Find(PdfTransport.PrinterName) is null)
        {
            Skip = $"SKIP: «{PdfTransport.PrinterName}» no está instalada en este equipo.";
        }
    }
}

/// <summary>
/// Destino tipo «pdf» (pruebas de integración y demos): cálculo del nombre de fichero y del
/// directorio, repetición de copias en UNA pasada, selección por la fábrica, error explícito
/// sin la impresora, y el ticket REAL escrito a PDF sin diálogos (RequiresPdfPrinterFact).
/// </summary>
public sealed class PdfTransportTests
{
    private static Document Document(int orderId = 1844) => new()
    {
        Version = 1,
        Meta = new DocumentMeta
        {
            JobId = 1,
            OrderId = orderId,
            LocationCode = "tst",
            CreatedAt = DateTimeOffset.UtcNow,
            Copies = 1,
            PrinterRole = "default",
        },
        Paper = new DocumentPaper { WidthMm = 80 },
        Blocks =
        [
            new TextBlock { Value = "ORVITA PRINT", Align = TextAlign.Center, Bold = true, Size = TextSize.Double },
            new TextBlock { Value = TestTicket.CadenaPrueba, Align = TextAlign.Center },
            new CutBlock { Mode = CutMode.Partial },
        ],
    };

    private static DestinationOptions Destination(string? address = null) =>
        new() { Role = "default", Label = "Demo", Kind = PrintTransportKind.Pdf, Address = address };

    private static PrintRequest Request(Document? document = null, long jobId = 77, int copies = 1, string? address = null) =>
        new()
        {
            JobId = jobId,
            Destination = Destination(address),
            Document = document ?? Document(),
            EscPos = [0x1B, 0x40],
            Copies = copies,
        };

    private sealed class FakePrinters(params string[] names) : IPrinterEnumerator
    {
        public IReadOnlyList<PrinterInfo> List() => [.. names.Select(n => new PrinterInfo { Name = n })];

        public string? DefaultPrinterName() => names.FirstOrDefault();

        public PrinterInfo? Find(string name) => List().FirstOrDefault(p => p.Name == name);
    }

    // ------------------------------------------------------- nombre de fichero / directorio

    [Fact]
    public void Nombre_de_fichero_con_orderId_jobId_y_utc()
    {
        // 11:30:45 +01:00 → 10:30:45 UTC (el nombre NO depende de la zona del equipo).
        var timestamp = new DateTimeOffset(2026, 1, 15, 11, 30, 45, TimeSpan.FromHours(1));

        Assert.Equal("ticket-1844-77-20260115103045.pdf", PdfTransport.BuildFileName(1844, 77, timestamp));
    }

    [Fact]
    public void Nombre_de_fichero_con_ceros_en_pruebas()
    {
        // orderId y jobId valen 0 en las impresiones de prueba: el nombre sigue siendo válido.
        var timestamp = new DateTimeOffset(2026, 1, 15, 10, 30, 45, TimeSpan.Zero);

        Assert.Equal("ticket-0-0-20260115103045.pdf", PdfTransport.BuildFileName(0, 0, timestamp));
    }

    [Fact]
    public void Directorio_vacio_usa_programdata()
    {
        Assert.Equal(PdfTransport.DefaultDirectory, PdfTransport.ResolveDirectory(null));
        Assert.Equal(PdfTransport.DefaultDirectory, PdfTransport.ResolveDirectory("   "));
        Assert.EndsWith(Path.Combine("OrvitaPrint", "prints"), PdfTransport.DefaultDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void Directorio_relativo_se_absolutiza()
    {
        var resolved = PdfTransport.ResolveDirectory("pdfs-demo");

        Assert.Equal(Path.GetFullPath("pdfs-demo"), resolved);
        Assert.Equal("pdfs-demo", Path.GetFileName(resolved)); // sigue siendo el mismo sitio
    }

    // ------------------------------------------------------------------- copias (una pasada)

    [Fact]
    public void Copias_repite_el_documento_en_una_sola_pasada()
    {
        var una = PdfTransport.BuildLines(Document(), 1);
        var tres = PdfTransport.BuildLines(Document(), 3);

        // La primera copia va intacta y el total crece: 3 layouts + 2 separadores (3 líneas).
        static List<(string, TextAlign, bool, LayoutSize)> Shape(List<LayoutLine> lines) =>
            [.. lines.Select(l => (l.Text, l.Align, l.Bold, l.Size))];
        Assert.Equal(Shape(una), Shape(tres).GetRange(0, una.Count));
        Assert.Equal((3 * una.Count) + (2 * 3), tres.Count);
        // La leyenda de la segunda copia aparece entre ambas.
        Assert.Contains(tres, line => line.Text.Contains("copia 2 de 3", StringComparison.Ordinal));
    }

    [Fact]
    public void Copias_menor_que_uno_es_una()
    {
        Assert.Equal(PdfTransport.BuildLines(Document(), 1).Count, PdfTransport.BuildLines(Document(), 0).Count);
    }

    // ------------------------------------------------------------------- selección fábrica

    [Fact]
    public void La_fabrica_mapea_pdf_al_transporte_pdf()
    {
        var factory = new WindowsPrintTransportFactory();

        Assert.IsType<PdfTransport>(factory.Create(new DestinationOptions { Kind = PrintTransportKind.Pdf }));
    }

    // ----------------------------------------------------- impresora «Microsoft Print to PDF»

    [Fact]
    public async Task Sin_la_impresora_pdf_error_explicito_y_nada_escrito()
    {
        var transport = new PdfTransport(new FakePrinters("EPSON TM-T20III"));

        var ex = await Assert.ThrowsAsync<PrintTransportException>(
            () => transport.PrintAsync(Request(), CancellationToken.None));

        Assert.Equal(PrintFailureKind.PrinterNotFound, ex.Kind);
        Assert.Equal("orp_printer_not_found", ex.ErrorCode);
        Assert.Contains(PdfTransport.PrinterName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("no está instalada en este equipo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelada_antes_de_empezar_no_toca_disco()
    {
        var transport = new PdfTransport(new FakePrinters(PdfTransport.PrinterName));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.PrintAsync(Request(), new CancellationToken(canceled: true)));
    }

    // --------------------------------------------- integración REAL (host Windows, sin diálogos)

    [RequiresPdfPrinterFact]
    public async Task Imprime_un_ticket_real_a_pdf_sin_dialogos()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "orvitaprint-pdf-tests", Guid.NewGuid().ToString("N"));
        var transport = new PdfTransport();

        await transport.PrintAsync(Request(copies: 2, jobId: 99, address: directory), CancellationToken.None);

        // Copias: UNA sola pasada → un único PDF para el trabajo (decisión documentada).
        var files = await WaitForPdfAsync(directory, timeout: TimeSpan.FromSeconds(10));
        var pdf = Assert.Single(files);
        var fileName = Path.GetFileName(pdf);
        Assert.StartsWith("ticket-1844-99-", fileName, StringComparison.Ordinal);
        Assert.EndsWith(".pdf", fileName, StringComparison.Ordinal);

        var bytes = await File.ReadAllBytesAsync(pdf);
        Assert.True(bytes.Length > 0, "El PDF salió vacío.");
        // Cabecera mágica de PDF: 25 50 44 46 2D → "%PDF-".
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes, 0, Math.Min(8, bytes.Length)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Espera a que haya PDFs legibles en el directorio: el spooler puede devolver antes de
    /// que el monitor de puerto «Microsoft Print to PDF» cierre el fichero.
    /// </summary>
    private static async Task<string[]> WaitForPdfAsync(string directory, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var files = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.pdf").Where(f => new FileInfo(f).Length > 0).ToArray()
                : [];
            if (files.Length > 0)
            {
                return files;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(
                    $"No apareció ningún PDF en «{directory}» en {timeout.TotalSeconds:0}s.");
            }

            await Task.Delay(100);
        }
    }
}
