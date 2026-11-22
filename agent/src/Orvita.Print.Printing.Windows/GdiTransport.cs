using System.Drawing;
using System.Drawing.Printing;
using Orvita.Print.Contracts;
using Orvita.Print.Core;

namespace Orvita.Print.Printing.Windows;

/// <summary>
/// Respaldo GDI (Doc/03 §6.5): cuando el driver no acepta RAW, imprime el documento con
/// <c>System.Drawing.Printing</c> vía el driver normal de Windows. Solo vive aquí, nunca en
/// Core (ADR-12). El resultado es un ticket legible renderizado por GDI: los comandos ESC/POS
/// (corte, cajón, QR gráfico) no aplican y quedan como texto o se omiten (ver
/// <see cref="GdiTicketLayout"/>).
///
/// Copias: se llama a <see cref="PrintDocument.Print"/> una vez por copia — determinístico en
/// cualquier driver (algunos ignoran <c>PrinterSettings.Copies</c>); el coste es irrelevante
/// en térmicas a este volumen (Doc/03 §12).
/// </summary>
public sealed class GdiTransport : IPrintTransport
{
    private readonly IPrinterEnumerator _printers;

    public GdiTransport(IPrinterEnumerator? printers = null)
    {
        _printers = printers ?? new WindowsPrinterEnumerator();
    }

    /// <inheritdoc />
    public Task PrintAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var printerName = request.Destination.PrinterName;
        if (string.IsNullOrWhiteSpace(printerName))
        {
            throw new PrintTransportException(
                PrintFailureKind.PrinterNotFound,
                "La salida «" + request.Destination.Role + "» no tiene impresora configurada.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Verificación previa con mensaje explícito (Doc/03 §6.6): si el nombre exacto no
        // existe, falla con orp_printer_not_found y sin tocar el driver.
        if (_printers.Find(printerName) is null)
        {
            throw new PrintTransportException(
                PrintFailureKind.PrinterNotFound,
                $"No existe la impresora «{printerName}» en este equipo. " +
                "Revísala en orvitaprint destinations o vuelve a añadirla.");
        }

        var lines = GdiTicketLayout.Build(request.Document);
        var copies = Math.Max(1, request.Copies);
        var docName = RawSpoolerTransport.DocNameFor(request.JobId, request.Destination.Role);

        return Task.Run(
            () =>
            {
                for (var copy = 1; copy <= copies; copy++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PrintOne(printerName, lines, docName, request.Copies > 1 ? $"{copy}/{copies}" : null, request.JobId);
                }
            },
            cancellationToken);
    }

    private static void PrintOne(string printerName, List<LayoutLine> lines, string docName, string? copyLabel, long jobId)
    {
        using var document = new PrintDocument
        {
            PrinterSettings = new PrinterSettings { PrinterName = printerName },
            DocumentName = copyLabel is null ? docName : $"{docName} (copia {copyLabel})",
        };

        var state = new PaintState(lines);
        document.PrintPage += (_, e) => PaintPage(e, state);

        try
        {
            document.Print();
        }
        catch (InvalidPrinterException ex)
        {
            throw new PrintTransportException(
                PrintFailureKind.Definitive,
                $"El driver de «{printerName}» rechazó la impresión GDI: {ex.Message}",
                ex);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new PrintTransportException(
                PrintFailureKind.Definitive,
                $"El spooler devolvió un error imprimiendo en «{printerName}»: {ex.Message}",
                ex);
        }
    }

    // ------------------------------------------------------------ pintado

    /// <summary>Estado de pintado entre páginas de la misma copia.</summary>
    private sealed class PaintState
    {
        public PaintState(List<LayoutLine> lines)
        {
            Lines = lines;
            Next = 0;
        }

        public List<LayoutLine> Lines { get; }

        public int Next { get; private set; }

        public float CharWidth { get; private set; }

        public float LineHeight { get; private set; }

        public float MarginLeft { get; private set; }

        public float Top { get; private set; }

        public float PrintableWidth { get; private set; }

        public float PaperDots { get; private set; }

        public void Measure(PrintPageEventArgs e)
        {
            if (CharWidth > 0)
            {
                return;
            }

            // Calibración con la rejilla del ticket: el ancho imprimible se reparte en las
            // columnas del documento (32 en 58 mm, 48 en 80 mm — Doc/03 §6.5).
            var columns = EscPosRendererOptions.Columns80Mm;
            CharWidth = Math.Max(1f, e.MarginBounds.Width / (float)columns);
            LineHeight = (CharWidth / 0.6f) * 1.35f;
            MarginLeft = e.MarginBounds.Left;
            Top = e.MarginBounds.Top;
            PrintableWidth = e.MarginBounds.Width;
            PaperDots = columns * 8f;
        }

        public void Advance() => Next++;
    }

    private static void PaintPage(PrintPageEventArgs e, PaintState state)
    {
        state.Measure(e);
        var graphics = e.Graphics!;
        var bottom = e.MarginBounds.Bottom;
        var y = state.Top;

        while (state.Next < state.Lines.Count)
        {
            var line = state.Lines[state.Next];
            var height = line.Size switch
            {
                LayoutSize.Double => state.LineHeight * 2f,
                LayoutSize.Triple => state.LineHeight * 3f,
                _ => state.LineHeight,
            };

            if (y + height > bottom)
            {
                // Página llena: el ticket continúa en la siguiente hoja.
                e.HasMorePages = true;
                return;
            }

            if (line.Image is { } image)
            {
                DrawImage(graphics, image, state, y);
            }
            else if (!line.IsBlank)
            {
                DrawText(graphics, line, state, y, height);
            }

            y += height;
            state.Advance();
        }

        e.HasMorePages = false;
    }

    private static void DrawText(Graphics graphics, LayoutLine line, PaintState state, float y, float height)
    {
        var scale = line.Size switch
        {
            LayoutSize.Double => 2f,
            LayoutSize.Triple => 3f,
            _ => 1f,
        };

        // Fuente monoespaciada en píxeles: avance ≈ 0,6 × tamaño. A tamaño normal llena
        // exactamente la rejilla de columnas.
        using var font = new Font(
            FontFamily.GenericMonospace,
            (state.CharWidth / 0.6f) * scale,
            line.Bold ? FontStyle.Bold : FontStyle.Regular,
            GraphicsUnit.Pixel);

        var textWidth = line.Text.Length * state.CharWidth * scale;
        var x = line.Align switch
        {
            TextAlign.Center => state.MarginLeft + ((state.PrintableWidth - textWidth) / 2f),
            TextAlign.Right => state.MarginLeft + (state.PrintableWidth - textWidth),
            _ => state.MarginLeft,
        };

        // La fuente de tamaño n× avanza n× más; el ascenso se ancla a la parte superior de la línea.
        var baseline = y + (height - (state.LineHeight * scale)) / 2f;
        graphics.DrawString(line.Text, font, Brushes.Black, x, baseline);
    }

    private static void DrawImage(Graphics graphics, PngRaster image, PaintState state, float y)
    {
        // Raster 1bpp (MSB primero, 1 = negro) → Bitmap ARGB. Logos de ticket: SetPixel sobra.
        using var bitmap = new Bitmap(image.WidthDots, image.HeightDots);
        var rowBytes = (image.WidthDots + 7) / 8;
        for (var row = 0; row < image.HeightDots; row++)
        {
            for (var column = 0; column < image.WidthDots; column++)
            {
                var bit = (image.Bitmap1Bpp[(row * rowBytes) + (column / 8)] >> (7 - (column % 8))) & 1;
                if (bit != 0)
                {
                    bitmap.SetPixel(column, row, Color.Black);
                }
            }
        }

        // Un punto ESC/POS ≈ 0,125 mm: el logo se dibuja proporcional al papel
        // (el papel de 80 mm son 384 puntos de ancho).
        var drawWidth = Math.Min(state.PrintableWidth * image.WidthDots / state.PaperDots, state.PrintableWidth);
        var drawHeight = drawWidth * image.HeightDots / Math.Max(1, image.WidthDots);
        graphics.DrawImage(bitmap, state.MarginLeft, y, drawWidth, drawHeight);
    }
}
