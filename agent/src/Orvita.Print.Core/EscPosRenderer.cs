using System.Text;
using Orvita.Print.Contracts;

namespace Orvita.Print.Core;

/// <summary>
/// Opciones del renderizador ESC/POS.
/// </summary>
public sealed class EscPosRendererOptions
{
    /// <summary>Columnas de una impresora de 58 mm (fuente A de 12×24).</summary>
    public const int Columns58Mm = 32;

    /// <summary>Columnas de una impresora de 80 mm (fuente A de 12×24).</summary>
    public const int Columns80Mm = 48;

    /// <summary>Ancho máximo de logo del contrato (Doc/04 §7.2).</summary>
    public const int MaxImageWidthDots = 384;

    /// <summary>Tabla de código del texto: CP850 por defecto (Doc/03 §6.5), CP858/CP437 configurables.</summary>
    public EscPosEncoding TextEncoding { get; init; } = EscPosEncoding.Cp850;

    /// <summary>
    /// Ancho de línea en columnas. Si no se fija, se deduce de <c>paper.width_mm</c>:
    /// 58 mm → 32 columnas, resto → 48. Parametrizable para fuentes alternativas.
    /// </summary>
    public int? Columns { get; init; }

    public static int ColumnsForPaperWidth(int widthMm) => widthMm <= 58 ? Columns58Mm : Columns80Mm;
}

/// <summary>
/// Renderizador ESC/POS (Doc/03 §6.5): traduce un <see cref="Document"/> (Doc/04 §7) a bytes
/// ESC/POS puros. No toca APIs de impresión: produce un <c>byte[]</c> que el transporte
/// (spooler RAW, TCP 9100…) envía tal cual — ADR-12.
///
/// Codificación: CP850 por defecto, CP858/CP437 configurables. Todo carácter no representable
/// se sustituye por uno seguro y se registra el aviso (Doc/05 §4.3); nunca se envían bytes
/// inválidos que puedan romper la impresora. Los bloques desconocidos se ignoran con aviso
/// (Doc/04 §8) y los datos inválidos (base64 roto, EAN13 no numérico…) se descartan con aviso.
///
/// <para>El renderizador emite UNA copia: el multiplicador de <c>meta.copies</c> lo aplica el
/// transporte (repetir el flujo de bytes o el documento de spooler, F2-05), no este (Doc/03 §6.7).
/// La cadena final de feed/corte sale de los bloques del documento; aquí no se añade nada.</para>
/// </summary>
public sealed class EscPosRenderer
{
    static EscPosRenderer()
    {
        // CP850/CP858/CP437 viven en CodePagesEncodingProvider, incluido en el shared framework
        // de .NET (sin paquete adicional). El registro repetido es inócuo.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private readonly EscPosRendererOptions _options;

    /// <summary>Log opcional (sustituciones, bloques ignorados); el Service lo conecta a su ILogger.</summary>
    public Action<string>? Log { get; set; }

    public EscPosRenderer(EscPosRendererOptions? options = null, Action<string>? log = null)
    {
        _options = options ?? new EscPosRendererOptions();
        Log = log;
    }

    /// <summary>
    /// Renderiza el documento a bytes ESC/POS. El flujo empieza con ESC @ (inicializar) y termina
    /// con el último bloque (normalmente feed + corte). Lanza <see cref="NotSupportedException"/>
    /// si <c>version</c> &gt; 1 (la rechaza el agente con orp_unsupported_document, Doc/04 §8).
    /// </summary>
    public byte[] Render(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version > 1)
        {
            throw new NotSupportedException(
                $"Documento neutro versión {document.Version} no soportada por este agente (orp_unsupported_document, Doc/04 §8).");
        }

        var columns = _options.Columns
            ?? EscPosRendererOptions.ColumnsForPaperWidth(document.Paper?.WidthMm ?? 80);
        var fallback = new LoggingSubstitutionFallback();
        var encoding = Encoding.GetEncoding((int)_options.TextEncoding, fallback, DecoderFallback.ReplacementFallback);

        var builder = new EscPosBuilder();
        builder.Initialize();

        foreach (var block in document.Blocks)
        {
            if (block is null)
            {
                continue;
            }

            RenderBlock(builder, block, columns, encoding);
        }

        if (fallback.Applied.Count > 0)
        {
            var detail = string.Join(", ", fallback.Applied.Select(Describe));
            Log?.Invoke(
                $"Codificación {encoding.WebName}: {fallback.Applied.Count} carácter(es) sin entrada en la tabla, " +
                $"sustituidos de forma segura (Doc/05 §4.3): {detail}");
        }

        return builder.ToArray();
    }

    // ------------------------------------------------------------ bloques

    private void RenderBlock(EscPosBuilder builder, Block block, int columns, Encoding encoding)
    {
        switch (block)
        {
            case TextBlock text:
                RenderText(builder, text, encoding);
                return;
            case KvBlock kv:
                RenderKv(builder, kv, columns, encoding);
                return;
            case ItemsBlock items:
                RenderItems(builder, items, columns, encoding);
                return;
            case TotalsBlock totals:
                RenderTotals(builder, totals, columns, encoding);
                return;
            case DividerBlock divider:
                RenderDivider(builder, divider, columns, encoding);
                return;
            case ImageBlock image:
                RenderImage(builder, image);
                return;
            case QrBlock qr:
                RenderQr(builder, qr, encoding);
                return;
            case BarcodeBlock barcode:
                RenderBarcode(builder, barcode, encoding);
                return;
            case FeedBlock feed:
                builder.FeedLines(Math.Clamp(feed.Lines ?? 1, 1, 255));
                return;
            case CutBlock cut:
                builder.Cut(cut.Mode ?? CutMode.Partial);
                return;
            case DrawerBlock:
                builder.Drawer();
                return;
            case RawBlock raw:
                RenderRaw(builder, raw);
                return;
            case UnknownBlock unknown:
                Log?.Invoke($"Bloque «{unknown.Type}» desconocido para esta versión del agente; se ignora (Doc/04 §8).");
                return;
            default:
                Log?.Invoke($"Bloque de tipo «{block.Type}» no manejado; se ignora.");
                return;
        }
    }

    private void RenderText(EscPosBuilder builder, TextBlock text, Encoding encoding)
    {
        var align = text.Align ?? TextAlign.Left;
        var oversize = text.Size is { } size && size != TextSize.Normal;
        var bold = text.Bold == true;
        var invert = text.Invert == true;

        if (align != TextAlign.Left)
        {
            builder.Align(align); // ESC a: la impresora centra/righta según el ancho real impreso
        }

        if (bold)
        {
            builder.Bold(true);
        }

        if (oversize)
        {
            builder.Size(text.Size!.Value);
        }

        if (invert)
        {
            builder.Invert(true);
        }

        builder.TextLine(encoding.GetBytes(text.Value ?? string.Empty));

        // Higiene de estado: los atributos no se filtran al bloque siguiente.
        if (invert)
        {
            builder.Invert(false);
        }

        if (oversize)
        {
            builder.Size(TextSize.Normal);
        }

        if (bold)
        {
            builder.Bold(false);
        }

        if (align != TextAlign.Left)
        {
            builder.Align(TextAlign.Left);
        }
    }

    private void RenderKv(EscPosBuilder builder, KvBlock kv, int columns, Encoding encoding)
    {
        var separator = kv.Separator ?? string.Empty;
        foreach (var item in kv.Items ?? [])
        {
            var label = encoding.GetBytes(item?.Label ?? string.Empty);
            var value = encoding.GetBytes(item?.Value ?? string.Empty);
            var gap = columns - label.Length - value.Length;

            if (gap >= 1)
            {
                builder.Bytes(label);
                Fill(builder, separator, gap, encoding);
                builder.Bytes(value);
                builder.LineFeed();
            }
            else
            {
                // No caben en una línea: etiqueta truncada y valor alineado a la derecha debajo.
                if (label.Length > columns)
                {
                    Log?.Invoke($"La etiqueta «{item?.Label}» no cabe en {columns} columnas; se trunca.");
                }

                builder.TextLine(Fit(label, columns, TextAlign.Left));
                builder.TextLine(Fit(value, columns, TextAlign.Right));
            }
        }
    }

    private void RenderItems(EscPosBuilder builder, ItemsBlock items, int columns, Encoding encoding)
    {
        var columnCount = items.Columns?.Count ?? 0;
        if (columnCount == 0)
        {
            Log?.Invoke("Bloque «items» sin columnas; se ignora.");
            return;
        }

        var aligns = new TextAlign[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            aligns[i] = items.Align is { } align && i < align.Count
                && Enum.TryParse<TextAlign>(align[i], ignoreCase: true, out var parsed)
                    ? parsed
                    : TextAlign.Left;
        }

        // Cabecera + filas, pre-codificadas: el ancho se mide en bytes de impresión, no en caracteres.
        var rows = new List<byte[][]> { EncodeRow(items.Columns!, columnCount, encoding) };
        foreach (var row in items.Rows ?? [])
        {
            rows.Add(EncodeRow(row, columnCount, encoding));
        }

        var widths = new int[columnCount];
        foreach (var row in rows)
        {
            for (var i = 0; i < columnCount; i++)
            {
                widths[i] = Math.Min(Math.Max(widths[i], row[i].Length), columns);
            }
        }

        Distribute(widths, aligns, columns);

        foreach (var row in rows)
        {
            for (var i = 0; i < columnCount; i++)
            {
                if (i > 0)
                {
                    builder.Spaces(1);
                }

                builder.Bytes(Fit(row[i], widths[i], aligns[i]));
            }

            builder.LineFeed();
        }
    }

    private void RenderTotals(EscPosBuilder builder, TotalsBlock totals, int columns, Encoding encoding)
    {
        foreach (var item in totals.Items ?? [])
        {
            var label = encoding.GetBytes(item?.Label ?? string.Empty);
            var value = encoding.GetBytes(item?.Value ?? string.Empty);
            var bold = item?.Bold == true;

            if (bold)
            {
                builder.Bold(true);
            }

            var gap = columns - label.Length - value.Length;
            if (gap >= 1)
            {
                builder.Bytes(label);
                builder.Spaces(gap);
                builder.Bytes(value);
                builder.LineFeed();
            }
            else
            {
                builder.TextLine(Fit(label, columns, TextAlign.Left));
                builder.TextLine(Fit(value, columns, TextAlign.Right));
            }

            if (bold)
            {
                builder.Bold(false);
            }
        }
    }

    private void RenderDivider(EscPosBuilder builder, DividerBlock divider, int columns, Encoding encoding)
    {
        var chars = divider.Char is { Length: > 0 } custom ? custom : "-";
        var bytes = encoding.GetBytes(chars);
        var fill = bytes.Length > 0 ? bytes[0] : (byte)0x2D;
        for (var i = 0; i < columns; i++)
        {
            builder.Bytes([fill]);
        }

        builder.LineFeed();
    }

    private void RenderImage(EscPosBuilder builder, ImageBlock image)
    {
        if (string.IsNullOrEmpty(image.Data) || image.WidthDots is not { } widthDots || widthDots <= 0)
        {
            Log?.Invoke("Bloque «image» sin base64 o sin width_dots válidos; se ignora.");
            return;
        }

        byte[] bitmap;
        try
        {
            bitmap = Convert.FromBase64String(image.Data);
        }
        catch (FormatException)
        {
            Log?.Invoke("Bloque «image» con base64 inválido; se ignora (nunca se envían bytes sospechosos a la impresora).");
            return;
        }

        var rowBytes = (widthDots + 7) / 8;
        if (bitmap.Length < rowBytes)
        {
            Log?.Invoke($"El mapa de bits del logo ({bitmap.Length} bytes) no llega a una fila de {rowBytes} bytes; se ignora.");
            return;
        }

        var remainder = bitmap.Length % rowBytes;
        if (remainder != 0)
        {
            Log?.Invoke($"El mapa de bits del logo deja {remainder} byte(s) fuera de filas completas " +
                        $"(ancho {widthDots} = {rowBytes} bytes/fila); se descartan.");
        }

        // F2-13 (decisión documentada): un logo más ancho que el máximo del contrato (384 puntos)
        // se RECORTA POR LA DERECHA hasta el máximo — nunca bytes por encima del papel de 80 mm,
        // que el firmware de algunas impresoras interpreta como basura o desalinea la fila.
        if (widthDots > EscPosRendererOptions.MaxImageWidthDots)
        {
            Log?.Invoke($"El logo declara {widthDots} puntos, por encima del máximo del contrato " +
                        $"({EscPosRendererOptions.MaxImageWidthDots}); se recorta POR LA DERECHA al máximo " +
                        $"(columnas {EscPosRendererOptions.MaxImageWidthDots + 1}–{widthDots} descartadas).");
            bitmap = CropRight(bitmap, widthDots, EscPosRendererOptions.MaxImageWidthDots);
            widthDots = EscPosRendererOptions.MaxImageWidthDots;
        }

        builder.RasterImage(bitmap, widthDots);
    }

    /// <summary>
    /// Recorta un mapa de bits de 1 bpp por la DERECHA: conserva las <paramref name="toWidth"/>
    /// columnas izquierdas de cada fila (bit alto = píxel izquierdo, MSB primero), con las filas
    /// de origen rellenadas a <c>ceil(fromWidth/8)</c> bytes y las de destino a
    /// <c>ceil(toWidth/8)</c>. Las filas incompletas de origen quedan fuera (el renderizador ya
    /// avisó de ellas antes de llamar aquí).
    /// </summary>
    private static byte[] CropRight(byte[] bitmap, int fromWidth, int toWidth)
    {
        var fromRowBytes = (fromWidth + 7) / 8;
        var toRowBytes = (toWidth + 7) / 8;
        var rows = bitmap.Length / fromRowBytes;
        var cropped = new byte[rows * toRowBytes];

        // Máscara del último byte de cada fila destino: se conservan los bits ALTOS
        // (columnas más a la izquierda). Si toWidth es múltiplo de 8, la fila queda completa.
        var lastBits = toWidth & 7;
        var lastMask = lastBits == 0 ? (byte)0xFF : (byte)(0xFF << (8 - lastBits));

        for (var row = 0; row < rows; row++)
        {
            Array.Copy(bitmap, row * fromRowBytes, cropped, row * toRowBytes, toRowBytes - 1);
            cropped[(row * toRowBytes) + toRowBytes - 1] = (byte)(bitmap[(row * fromRowBytes) + toRowBytes - 1] & lastMask);
        }

        return cropped;
    }

    private void RenderQr(EscPosBuilder builder, QrBlock qr, Encoding encoding)
    {
        if (string.IsNullOrEmpty(qr.Value))
        {
            Log?.Invoke("Bloque «qr» vacío; se ignora.");
            return;
        }

        builder.Qr(encoding.GetBytes(qr.Value), qr.Size ?? 4);
    }

    private void RenderBarcode(EscPosBuilder builder, BarcodeBlock barcode, Encoding encoding)
    {
        if (string.IsNullOrEmpty(barcode.Value))
        {
            Log?.Invoke("Bloque «barcode» vacío; se ignora.");
            return;
        }

        switch (barcode.Symbology)
        {
            case BarcodeSymbology.Ean13:
                if (barcode.Value.Length is not (12 or 13) || barcode.Value.Any(c => !char.IsAsciiDigit(c)))
                {
                    Log?.Invoke($"Bloque «barcode» EAN13 «{barcode.Value}» no válido (se esperan 12–13 dígitos); se ignora.");
                    return;
                }

                builder.Barcode(encoding.GetBytes(barcode.Value), BarcodeSymbology.Ean13);
                return;

            case BarcodeSymbology.Code128:
            {
                // Subconjunto B: ASCII imprimible 0x20–0x7E. Lo que se salga se sustituye por «?»
                // y se registra; el prefijo de subconjunto lo añade el renderizador.
                var sanitized = barcode.Value
                    .Select(c => c is >= ' ' and <= '~' ? c : '?')
                    .ToArray();
                if (!sanitized.AsSpan().SequenceEqual(barcode.Value))
                {
                    Log?.Invoke($"Bloque «barcode» CODE128 con caracteres fuera de ASCII imprimible; " +
                                $"se imprimen como «?» (valor «{barcode.Value}»).");
                }

                var payload = Encoding.ASCII.GetBytes("{B" + new string(sanitized));
                if (payload.Length > 255)
                {
                    Log?.Invoke("Bloque «barcode» CODE128 demasiado largo para GS k (máx. 255 bytes); se ignora.");
                    return;
                }

                builder.Barcode(payload, BarcodeSymbology.Code128);
                return;
            }

            default:
                Log?.Invoke("Bloque «barcode» sin simbología soportada (CODE128 | EAN13); se ignora.");
                return;
        }
    }

    private void RenderRaw(EscPosBuilder builder, RawBlock raw)
    {
        if (string.IsNullOrEmpty(raw.Data))
        {
            Log?.Invoke("Bloque «raw» vacío; se ignora.");
            return;
        }

        try
        {
            builder.Raw(Convert.FromBase64String(raw.Data));
        }
        catch (FormatException)
        {
            Log?.Invoke("Bloque «raw» con base64 inválido; se ignora.");
        }
    }

    // ------------------------------------------------------------ utilidades de línea

    /// <summary>Rellena <paramref name="count"/> columnas con el separador (cíclico) o espacios.</summary>
    private static void Fill(EscPosBuilder builder, string separator, int count, Encoding encoding)
    {
        if (string.IsNullOrEmpty(separator))
        {
            builder.Spaces(count);
            return;
        }

        var fill = encoding.GetBytes(separator);
        if (fill.Length == 0)
        {
            builder.Spaces(count);
            return;
        }

        for (var i = 0; i < count; i++)
        {
            builder.Bytes([fill[i % fill.Length]]);
        }
    }

    /// <summary>Recorta o rellena una celda ya codificada a la anchura destino según su alineación.</summary>
    private static byte[] Fit(byte[] cell, int width, TextAlign align)
    {
        if (cell.Length > width)
        {
            return cell[..width];
        }

        var pad = width - cell.Length;
        return align switch
        {
            TextAlign.Right => [.. Enumerable.Repeat((byte)0x20, pad), .. cell],
            TextAlign.Center => [.. Enumerable.Repeat((byte)0x20, pad / 2), .. cell, .. Enumerable.Repeat((byte)0x20, pad - (pad / 2))],
            _ => [.. cell, .. Enumerable.Repeat((byte)0x20, pad)],
        };
    }

    /// <summary>
    /// Reparto de anchuras de la tabla <c>items</c>: si cabe, el sobrante va a la columna
    /// izquierda más ancha (el nombre del producto); si no cabe, se recorta de la más ancha
    /// hasta entrar (mínimo 1) y el truncado fino lo hace <see cref="Fit"/>.
    /// </summary>
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
                break; // no hay más que recortar; Fit truncará
            }

            widths[widest]--;
        }
    }

    private static byte[][] EncodeRow(List<string> row, int columnCount, Encoding encoding) =>
        EncodeRow((IList<string>)row, columnCount, encoding);

    private static byte[][] EncodeRow(IList<string> row, int columnCount, Encoding encoding)
    {
        var cells = new byte[columnCount][];
        for (var i = 0; i < columnCount; i++)
        {
            cells[i] = encoding.GetBytes(i < row.Count ? row[i] ?? string.Empty : string.Empty);
        }

        return cells;
    }

    private static string Describe((string Original, string Replacement) substitution)
    {
        var original = substitution.Original.Length == 1 && char.IsControl(substitution.Original[0])
            ? $"U+{(int)substitution.Original[0]:X4}"
            : substitution.Original;
        return $"«{original}»→«{substitution.Replacement}»";
    }
}
