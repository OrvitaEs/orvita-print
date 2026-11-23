using System.Text;
using Orvita.Print.Contracts;

namespace Orvita.Print.Core;

/// <summary>
/// Renderizado del documento neutro a TEXTO plano legible (Doc/08 §4.2): es la vista del
/// detalle de un trabajo en la GUI de bandeja (op <c>queue.item</c>). NO es el renderizado
/// térmico (eso es <see cref="EscPosRenderer"/>, bytes ESC/POS): es una vista de lectura,
/// portable (ADR-12) y a prueba de munición — los bloques que no sabe mostrar se representan
/// con un marcador claro, nunca se rompe ni lanza.
/// </summary>
public static class DocumentText
{
    /// <summary>Texto legible del documento. Nunca lanza: ante lo desconocido, marcador.</summary>
    public static string Render(Document? document, int widthChars = 42)
    {
        if (document is null)
        {
            return "(sin documento)";
        }

        var width = Math.Clamp(widthChars, 20, 120);
        var builder = new StringBuilder();
        foreach (var block in document.Blocks)
        {
            AppendBlock(builder, block, width);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private static void AppendBlock(StringBuilder builder, Block block, int width)
    {
        switch (block)
        {
            case TextBlock text:
                AppendText(builder, text.Value ?? string.Empty, text.Align, width);
                break;
            case KvBlock kv:
                AppendKv(builder, kv, width);
                break;
            case ItemsBlock items:
                AppendItems(builder, items, width);
                break;
            case TotalsBlock totals:
                AppendTotals(builder, totals, width);
                break;
            case DividerBlock divider:
                builder.AppendLine(new string(DividerChar(divider.Char), width));
                break;
            case FeedBlock feed:
                var lines = Math.Clamp(feed.Lines ?? 1, 0, 100);
                builder.Append(new string('\n', lines));
                break;
            case CutBlock:
                builder.AppendLine("— — corte — —");
                break;
            case DrawerBlock:
                builder.AppendLine("[cajón]");
                break;
            case ImageBlock:
                builder.AppendLine("[logo]");
                break;
            case QrBlock qr:
                builder.AppendLine($"[QR: {qr.Value}]");
                break;
            case BarcodeBlock barcode:
                builder.AppendLine($"[código: {barcode.Value}]");
                break;
            case RawBlock:
                builder.AppendLine("[esc/pos]");
                break;
            case UnknownBlock unknown:
                builder.AppendLine($"[bloque desconocido: {unknown.Type}]");
                break;
            default:
                builder.AppendLine($"[bloque no soportado: {block.Type}]");
                break;
        }
    }

    private static void AppendText(StringBuilder builder, string value, TextAlign? align, int width)
    {
        foreach (var line in value.Split('\n'))
        {
            var content = line.TrimEnd('\r');
            builder.AppendLine(align == TextAlign.Center
                ? Center(content, width)
                : content);
        }
    }

    private static void AppendKv(StringBuilder builder, KvBlock kv, int width)
    {
        if (kv.Items is null)
        {
            return;
        }

        // Dos columnas: etiqueta a la izquierda, valor a la derecha (misma rejilla que el ticket).
        var labelWidth = Math.Max(1, Math.Min(width / 2, kv.Items.Count == 0 ? 1 : kv.Items.Max(i => (i.Label ?? string.Empty).Length)));
        foreach (var item in kv.Items)
        {
            var label = (item.Label ?? string.Empty).PadRight(labelWidth);
            var value = item.Value ?? string.Empty;
            var space = width - labelWidth - 1;
            builder.AppendLine(space > 0
                ? $"{label} {Truncate(value, space)}"
                : Truncate($"{label} {value}", width));
        }
    }

    private static void AppendItems(StringBuilder builder, ItemsBlock items, int width)
    {
        var columns = items.Columns;
        if (columns is null || columns.Count == 0)
        {
            return;
        }

        var rows = (items.Rows ?? []).Select(r => (IReadOnlyList<string>)r.Select(c => c ?? string.Empty).ToList()).ToList();
        var widths = ColumnWidths(columns, rows, width);
        builder.AppendLine(JoinRow(columns, items.Align, widths));
        foreach (var row in rows)
        {
            builder.AppendLine(JoinRow(row, items.Align, widths));
        }
    }

    /// <summary>
    /// Anchos por columna: el que pide el contenido (cabecera y celdas); si no cabe en el
    /// ancho del papel, se reduce proporcionalmente (mínimo 1) — la vista nunca desborda.
    /// </summary>
    private static List<int> ColumnWidths(IReadOnlyList<string> columns, List<IReadOnlyList<string>> rows, int width)
    {
        var maxes = columns.Select((c, i) => Math.Max(
            c.Length,
            rows.Select(r => i < r.Count ? r[i].Length : 0).DefaultIfEmpty(0).Max())).ToList();

        var total = maxes.Sum() + (maxes.Count - 1);
        if (total <= width)
        {
            return maxes;
        }

        var available = width - (maxes.Count - 1);
        var sum = Math.Max(1, maxes.Sum());
        return maxes.Select(m => Math.Max(1, m * available / sum)).ToList();
    }

    private static string JoinRow(IReadOnlyList<string> cells, List<string>? aligns, List<int> widths)
    {
        var rendered = new List<string>();
        for (var index = 0; index < cells.Count; index++)
        {
            var slot = index < widths.Count ? widths[index] : 1;
            var cell = index < widths.Count ? Truncate(cells[index], slot) : cells[index];
            var alignment = index < (aligns?.Count ?? 0) ? aligns![index] : null;
            rendered.Add(alignment == "right" ? cell.PadLeft(slot) : alignment == "center" ? Center(cell, slot) : cell.PadRight(slot));
        }

        return string.Join(" ", rendered).TrimEnd();
    }

    private static void AppendTotals(StringBuilder builder, TotalsBlock totals, int width)
    {
        if (totals.Items is null)
        {
            return;
        }

        foreach (var item in totals.Items)
        {
            var label = (item.Label ?? string.Empty).PadRight(Math.Max(1, width - 10));
            var value = (item.Value ?? string.Empty).PadLeft(Math.Min(10, width));
            builder.AppendLine(Truncate($"{label}{value}", width));
        }
    }

    private static char DividerChar(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || char.IsControl(raw[0]))
        {
            return '-';
        }

        return raw[0];
    }

    private static string Center(string value, int width)
    {
        if (value.Length >= width)
        {
            return Truncate(value, width);
        }

        var padding = width - value.Length;
        var left = padding / 2;
        return new string(' ', left) + value + new string(' ', padding - left);
    }

    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..width];
}
