using Orvita.Print.Contracts;
using Orvita.Print.Core;

namespace Orvita.Print.Printing.Windows;

/// <summary>Tamaño relativo de una línea (espejo del texto del documento, Doc/04 §7.2).</summary>
public enum LayoutSize
{
    Normal,
    Double,
    Triple,
}

/// <summary>Lógica de una línea del ticket en el respaldo GDI: texto maquetado o imagen.</summary>
public sealed class LayoutLine
{
    public string Text { get; init; } = string.Empty;

    public TextAlign Align { get; init; } = TextAlign.Left;

    public bool Bold { get; init; }

    public LayoutSize Size { get; init; } = LayoutSize.Normal;

    /// <summary>Línea en blanco (bloque <c>feed</c>).</summary>
    public bool IsBlank => Text.Length == 0 && Image is null;

    /// <summary>Logo decodificado (raster 1bpp ya normalizado por el procesador, F2-07).</summary>
    public PngRaster? Image { get; init; }
}

/// <summary>
/// Maquetador del documento neutro para el respaldo GDI (Doc/03 §6.5: «si el driver rechaza
/// RAW»). Espeja el reparto de columnas del <see cref="EscPosRenderer"/> (58→32, resto→48
/// columnas; kv/items/totals con la misma regla de anchos) para que el ticket GDI sea fiel al
/// ESC/POS aunque el trazo sea del driver. QR/código de barras/raw no se dibujan como imagen:
/// quedan como texto legible entre corchetes y aviso (el GDI es un respaldo de legibilidad).
/// </summary>
public static class GdiTicketLayout
{
    /// <summary>Convierte el documento en líneas maquetadas; <see cref="EscPosRendererOptions"/> da las columnas.</summary>
    public static List<LayoutLine> Build(Document document, EscPosRendererOptions? options = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var columns = options?.Columns
                      ?? EscPosRendererOptions.ColumnsForPaperWidth(document.Paper?.WidthMm ?? 80);
        var lines = new List<LayoutLine>();

        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case TextBlock text:
                    lines.Add(new LayoutLine
                    {
                        Text = text.Value ?? string.Empty,
                        Align = text.Align ?? TextAlign.Left,
                        Bold = text.Bold == true,
                        Size = MapSize(text.Size),
                    });
                    break;

                case KvBlock kv:
                    foreach (var item in kv.Items ?? [])
                    {
                        AddPair(lines, item?.Label ?? string.Empty, item?.Value ?? string.Empty, columns, kv.Separator);
                    }

                    break;

                case ItemsBlock items:
                    AddItems(lines, items, columns, log);
                    break;

                case TotalsBlock totals:
                    foreach (var total in totals.Items ?? [])
                    {
                        AddPair(lines, total?.Label ?? string.Empty, total?.Value ?? string.Empty, columns, separator: null, bold: total?.Bold == true);
                    }

                    break;

                case DividerBlock divider:
                    lines.Add(new LayoutLine { Text = RepeatChar(divider.Char is { Length: > 0 } c ? c : "-", columns) });
                    break;

                case ImageBlock image:
                    AddImage(lines, image, log);
                    break;

                case QrBlock qr when !string.IsNullOrEmpty(qr.Value):
                    log?.Invoke("Respaldo GDI: el QR se imprime como texto legible (sin gráfico).");
                    lines.Add(new LayoutLine { Text = $"[QR] {qr.Value}", Align = TextAlign.Center });
                    break;

                case BarcodeBlock barcode when !string.IsNullOrEmpty(barcode.Value):
                    log?.Invoke("Respaldo GDI: el código de barras se imprime como texto legible (sin gráfico).");
                    lines.Add(new LayoutLine { Text = $"[barcode] {barcode.Value}", Align = TextAlign.Center });
                    break;

                case FeedBlock feed:
                    for (var i = 0; i < Math.Clamp(feed.Lines ?? 1, 1, 255); i++)
                    {
                        lines.Add(new LayoutLine());
                    }

                    break;

                case CutBlock:
                case DrawerBlock:
                case RawBlock:
                    // El corte, el cajón y el raw son comandos ESC/POS: en GDI no tienen sentido
                    // (el driver controla el papel y el cajón es del driver). Sin aviso: es lo esperable.
                    break;

                case UnknownBlock unknown:
                    log?.Invoke($"Bloque «{unknown.Type}» desconocido; se ignora (Doc/04 §8).");
                    break;
            }
        }

        return lines;
    }

    // ------------------------------------------------------------ piezas

    private static LayoutSize MapSize(TextSize? size) => size switch
    {
        TextSize.Double => LayoutSize.Double,
        TextSize.Triple => LayoutSize.Triple,
        _ => LayoutSize.Normal,
    };

    private static void AddPair(List<LayoutLine> lines, string label, string value, int columns, string? separator, bool bold = false)
    {
        var gap = columns - label.Length - value.Length;
        if (gap >= 1)
        {
            var filler = separator is { Length: > 0 } ? separator[0] : ' ';
            lines.Add(new LayoutLine
            {
                Text = label + new string(filler, gap) + value,
                Bold = bold,
            });
        }
        else
        {
            lines.Add(new LayoutLine { Text = Truncate(label, columns) });
            lines.Add(new LayoutLine { Text = value.PadLeft(Math.Min(value.Length, columns)), Align = TextAlign.Right, Bold = bold });
        }
    }

    private static void AddItems(List<LayoutLine> lines, ItemsBlock items, int columns, Action<string>? log)
    {
        var columnCount = items.Columns?.Count ?? 0;
        if (columnCount == 0)
        {
            log?.Invoke("Bloque «items» sin columnas; se ignora.");
            return;
        }

        var aligns = new TextAlign[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            aligns[i] = items.Align is { } a && i < a.Count
                && Enum.TryParse<TextAlign>(a[i], ignoreCase: true, out var parsed) ? parsed : TextAlign.Left;
        }

        var rows = new List<IList<string>> { items.Columns! };
        foreach (var row in items.Rows ?? [])
        {
            rows.Add(row);
        }

        // Mismo reparto de anchuras que el renderizador ESC/POS (Distribute).
        var widths = new int[columnCount];
        foreach (var row in rows)
        {
            for (var i = 0; i < columnCount; i++)
            {
                widths[i] = Math.Min(Math.Max(widths[i], Cell(row, i, columnCount).Length), columns);
            }
        }

        Distribute(widths, aligns, columns);

        foreach (var row in rows)
        {
            var text = string.Empty;
            for (var i = 0; i < columnCount; i++)
            {
                if (i > 0)
                {
                    text += " ";
                }

                text += Fit(Cell(row, i, columnCount), widths[i], aligns[i]);
            }

            lines.Add(new LayoutLine { Text = text });
        }
    }

    private static void AddImage(List<LayoutLine> lines, ImageBlock image, Action<string>? log)
    {
        if (string.IsNullOrEmpty(image.Data) || image.WidthDots is not { } widthDots || widthDots <= 0)
        {
            log?.Invoke("Bloque «image» sin base64 o sin width_dots válidos; se ignora.");
            return;
        }

        byte[] bitmap;
        try
        {
            bitmap = Convert.FromBase64String(image.Data);
        }
        catch (FormatException)
        {
            log?.Invoke("Bloque «image» con base64 inválido; se ignora.");
            return;
        }

        var rowBytes = (widthDots + 7) / 8;
        if (bitmap.Length < rowBytes)
        {
            log?.Invoke($"El mapa de bits del logo ({bitmap.Length} bytes) no llega a una fila de {rowBytes} bytes; se ignora.");
            return;
        }

        var height = bitmap.Length / rowBytes;
        lines.Add(new LayoutLine
        {
            Image = new PngRaster
            {
                WidthDots = widthDots,
                HeightDots = height,
                Bitmap1Bpp = bitmap,
            },
        });
    }

    private static string Cell(IList<string>? row, int index, int columnCount) =>
        row is null || index >= row.Count ? string.Empty : row[index] ?? string.Empty;

    private static string Fit(string cell, int width, TextAlign align)
    {
        if (cell.Length > width)
        {
            return cell[..width];
        }

        return align switch
        {
            TextAlign.Right => cell.PadLeft(width),
            TextAlign.Center => cell.PadLeft(width - ((width - cell.Length) / 2)).PadRight(width),
            _ => cell.PadRight(width),
        };
    }

    private static void Distribute(int[] widths, TextAlign[] aligns, int columns)
    {
        var total = widths.Sum() + (widths.Length - 1);
        if (total <= columns)
        {
            var stretch = -1;
            for (var i = 0; i < widths.Length; i++)
            {
                if (aligns[i] == TextAlign.Left && (stretch < 0 || widths[i] > widths[stretch]))
                {
                    stretch = i;
                }
            }

            if (stretch >= 0)
            {
                widths[stretch] += columns - total;
            }

            return;
        }

        while (widths.Sum() + (widths.Length - 1) > columns)
        {
            var widest = 0;
            for (var i = 1; i < widths.Length; i++)
            {
                if (widths[i] > widths[widest])
                {
                    widest = i;
                }
            }

            if (widths[widest] <= 1)
            {
                break;
            }

            widths[widest]--;
        }
    }

    private static string RepeatChar(string pattern, int columns)
    {
        if (pattern.Length == 0)
        {
            pattern = "-";
        }

        var result = new System.Text.StringBuilder(columns);
        for (var i = 0; i < columns; i++)
        {
            result.Append(pattern[i % pattern.Length]);
        }

        return result.ToString();
    }

    private static string Truncate(string value, int width) => value.Length <= width ? value : value[..width];
}
