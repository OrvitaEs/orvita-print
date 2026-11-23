using System.ComponentModel;
using System.Drawing.Printing;
using System.Globalization;
using Orvita.Print.Contracts;
using Orvita.Print.Core;

namespace Orvita.Print.Printing.Windows;

/// <summary>
/// Destino tipo «pdf» (pruebas de integración y demos, Doc/03 §6.5): imprime el ticket a un
/// fichero PDF con la impresora de Windows «Microsoft Print to PDF», SIN diálogos — el
/// <see cref="PrintDocument.PrintController"/> es un <see cref="StandardPrintController"/>
/// (suprime el diálogo de progreso) y <c>PrinterSettings.PrintToFile + PrintFileName</c>
/// suprimen el de «guardar como». Maqueta con el mismo <see cref="GdiTicketLayout"/> y pinta
/// con el mismo motor GDI que <see cref="GdiTransport"/> (misma rejilla de columnas), así que
/// el PDF es fiel al ticket que saldría por la térmica.
///
/// Semántica de <see cref="DestinationOptions.Address"/>: es el DIRECTORIO donde escribir los
/// PDFs — se crea si no existe; vacío → <see cref="DefaultDirectory"/>
/// (%ProgramData%\OrvitaPrint\prints\). El nombre lo genera el transporte sin colisiones:
/// <c>ticket-{orderId:0}-{jobId}-{yyyyMMddHHmmss}.pdf</c> (orderId puede ser 0 en pruebas; la
/// marca de tiempo UTC evita que una reimpresión del mismo trabajo sobrescriba el intento previo).
///
/// Copias: UNA SOLA PASADA con el documento repetido (decisión documentada): un PDF por
/// trabajo, con cada copia tras tres líneas en blanco y la leyenda «copia n de N». El ticket
/// es papel continuo y así el resultado es determinístico en cualquier driver (mismo criterio
/// que <see cref="GdiTransport"/>: no se confía en <c>PrinterSettings.Copies</c>).
/// </summary>
public sealed class PdfTransport : IPrintTransport
{
    /// <summary>Nombre EXACTO de la impresora de Windows que genera los PDFs.</summary>
    public const string PrinterName = "Microsoft Print to PDF";

    private readonly IPrinterEnumerator _printers;

    public PdfTransport(IPrinterEnumerator? printers = null)
    {
        _printers = printers ?? new WindowsPrinterEnumerator();
    }

    /// <summary>Directorio por defecto de los PDFs: %ProgramData%\OrvitaPrint\prints\.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OrvitaPrint",
        "prints");

    /// <summary>
    /// Nombre de fichero del PDF: <c>ticket-{orderId:0}-{jobId}-{yyyyMMddHHmmss}.pdf</c>
    /// (marca de tiempo UTC, InvariantCulture: el nombre no depende del idioma del equipo).
    /// </summary>
    public static string BuildFileName(long orderId, long jobId, DateTimeOffset timestamp) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"ticket-{orderId.ToString("0", CultureInfo.InvariantCulture)}-{jobId.ToString("0", CultureInfo.InvariantCulture)}-{timestamp.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.pdf");

    /// <summary>
    /// Normaliza el directorio de salida: vacío → <see cref="DefaultDirectory"/>; ruta relativa
    /// → absoluta (el servicio vive en sesión 0: nunca se confía en el directorio actual).
    /// No crea el directorio: eso lo hace <see cref="PrintAsync"/> al imprimir.
    /// </summary>
    public static string ResolveDirectory(string? address) =>
        string.IsNullOrWhiteSpace(address) ? DefaultDirectory : Path.GetFullPath(address.Trim());

    /// <summary>
    /// Líneas maquetadas del PDF: el layout del documento repetido <paramref name="copies"/>
    /// veces (una sola pasada), separando cada copia con líneas en blanco y leyenda.
    /// </summary>
    public static List<LayoutLine> BuildLines(Document document, int copies)
    {
        var total = Math.Max(1, copies);
        var lines = new List<LayoutLine>();
        for (var copy = 1; copy <= total; copy++)
        {
            if (copy > 1)
            {
                lines.Add(new LayoutLine());
                lines.Add(new LayoutLine
                {
                    Text = $"— copia {copy} de {total} —",
                    Align = TextAlign.Center,
                });
                lines.Add(new LayoutLine());
            }

            lines.AddRange(GdiTicketLayout.Build(document));
        }

        return lines;
    }

    /// <inheritdoc />
    public Task PrintAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Verificación previa con mensaje explícito (mismo criterio que GdiTransport, Doc/03 §6.6):
        // si la impresora de PDF no está, falla con orp_printer_not_found y sin tocar el driver.
        if (_printers.Find(PrinterName) is null)
        {
            throw new PrintTransportException(
                PrintFailureKind.PrinterNotFound,
                $"La impresora «{PrinterName}» no está instalada en este equipo. " +
                "Viene con Windows: revisa «Panel de control → Dispositivos e impresoras» o " +
                "consulta las salidas con orvitaprint destination list.");
        }

        var directory = ResolveDirectory(request.Destination.Address);
        Directory.CreateDirectory(directory);

        var orderId = request.Document.Meta?.OrderId ?? 0;
        var fileName = Path.Combine(directory, BuildFileName(orderId, request.JobId, DateTimeOffset.UtcNow));
        var lines = BuildLines(request.Document, request.Copies);
        var docName = RawSpoolerTransport.DocNameFor(request.JobId, request.Destination.Role);

        return Task.Run(() => PrintToFile(fileName, lines, docName), cancellationToken);
    }

    private static void PrintToFile(string filePath, List<LayoutLine> lines, string docName)
    {
        using var document = new PrintDocument
        {
            PrinterSettings = new PrinterSettings
            {
                PrinterName = PrinterName,
                PrintToFile = true,
                PrintFileName = filePath,
            },
            DocumentName = docName,
            // Sin diálogos: ni el de progreso (StandardPrintController) ni el de «guardar como»
            // (PrintToFile + PrintFileName arriba).
            PrintController = new StandardPrintController(),
        };

        var state = new GdiTransport.PaintState(lines);
        document.PrintPage += (_, e) => GdiTransport.PaintPage(e, state);

        try
        {
            document.Print();
        }
        catch (InvalidPrinterException ex)
        {
            throw new PrintTransportException(
                PrintFailureKind.Definitive,
                $"El driver de «{PrinterName}» rechazó la impresión a PDF: {ex.Message}",
                ex);
        }
        catch (Win32Exception ex)
        {
            throw new PrintTransportException(
                PrintFailureKind.Definitive,
                $"El spooler devolvió un error generando «{Path.GetFileName(filePath)}»: {ex.Message}",
                ex);
        }
    }
}
