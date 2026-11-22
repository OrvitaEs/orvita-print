using System.IO.Compression;
using Orvita.Print.Core;

namespace Orvita.Print.Printing.Windows;

/// <summary>
/// Decodificador PNG en .NET puro (Doc/04 §7.2: «PNG base64, decodificado en el agente con
/// .NET puro, sin System.Drawing»). Implementa <see cref="IPngDecoder"/> para que el
/// procesador (F2-07) convierta los logos a raster 1bpp antes del renderizado ESC/POS.
///
/// Subconjunto soportado, con error explícito para lo demás:
/// - Profundidad 8 bits por canal; color: gris (0), RGB (2), paleta (3), gris+alfa (4), RGBA (6).
/// - No entrelazados (Adam7 rechazado: un logo medio renderizado no es aceptable).
/// - El alfa se compone sobre blanco y la luminancia se umbraliza a 1 bpp (1 = punto negro).
/// - Reescalado por vecino más cercano al ancho objetivo (width_dots del bloque).
///
/// Implementación: chunks PNG (IHDR/PLTE/tRNS/IDAT/IEND), flujo zlib → <see cref="DeflateStream"/>
/// (se salta la cabecera de 2 bytes; el Adler-32 final sobra), desfiltros 0–4 por filas.
/// </summary>
public sealed class PngDecoder : IPngDecoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Límite de cordura para no descomprimir bombas: un logo nunca pasa de esto.</summary>
    public const int MaxDimension = 4096;

    /// <inheritdoc />
    public PngRaster? TryDecodeToRaster(string base64Data, int? targetWidthDots = null, Action<string>? log = null)
    {
        try
        {
            return Decode(base64Data, targetWidthDots, log);
        }
        catch (Exception ex) when (
            ex is FormatException or InvalidDataException or EndOfStreamException or OutOfMemoryException or NotSupportedException)
        {
            log?.Invoke($"Logo PNG no decodificable, se ignora (nunca se envían bytes sospechosos a la impresora): {ex.Message}");
            return null;
        }
    }

    private static PngRaster Decode(string base64Data, int? targetWidthDots, Action<string>? log)
    {
        byte[] png;
        try
        {
            png = Convert.FromBase64String(base64Data);
        }
        catch (FormatException ex)
        {
            throw new FormatException("el base64 del bloque «image» no es válido", ex);
        }

        if (png.Length < Signature.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new NotSupportedException(
                "no lleva la firma PNG (¿quizá es un mapa de bits crudo de la plantilla v0?); regenera el logo");
        }

        var (header, palette, idat) = ReadChunks(png);
        if (header.Length < 13)
        {
            throw new FormatException("el chunk IHDR no tiene las 13 bytes reglamentarios");
        }

        var width = (int)ReadUInt32BE(header, 0);
        var height = (int)ReadUInt32BE(header, 4);
        var bitDepth = header[8];
        var colorType = header[9];
        var compression = header[10];
        var filterMethod = header[11];
        var interlace = header[12];

        if (width is 0 || height is 0 || width > MaxDimension || height > MaxDimension)
        {
            throw new NotSupportedException($"dimensiones {width}×{height} fuera de lo razonable (máx. {MaxDimension}).");
        }

        if (bitDepth != 8)
        {
            throw new NotSupportedException($"profundidad {bitDepth} bits; solo se soporta PNG de 8 bits por canal.");
        }

        if (compression != 0 || filterMethod != 0)
        {
            throw new NotSupportedException("método de compresión o de filtro desconocido.");
        }

        if (interlace != 0)
        {
            throw new NotSupportedException("entrelazado Adam7 no soportado; guarda el logo sin entrelazar.");
        }

        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new NotSupportedException($"tipo de color {colorType} no soportado."),
        };

        if (colorType == 3 && palette is null)
        {
            throw new FormatException("PNG de paleta sin chunk PLTE");
        }

        var raw = Inflate(idat);
        var stride = width * channels;              // bytes de píxel por fila
        var rowStride = stride + 1;                 // + el byte de filtro que encabeza cada fila
        if (raw.Length < (long)height * rowStride)
        {
            throw new EndOfStreamException($"los datos descomprimidos ({raw.Length} bytes) no llegan a {width}×{height}.");
        }

        Unfilter(raw, height, stride, rowStride, channels);

        // Raster destino: vecino más cercano al ancho objetivo; por defecto el propio del PNG.
        var target = Math.Clamp(targetWidthDots ?? width, 1, EscPosRendererOptions.MaxImageWidthDots);
        var rowBytes = (target + 7) / 8;
        var raster = new byte[rowBytes * height];

        for (var y = 0; y < height; y++)
        {
            var dataStart = (y * rowStride) + 1;
            for (var x = 0; x < target; x++)
            {
                var source = (int)(x * (long)width / target);
                if (IsDark(raw, dataStart + (source * channels), colorType, palette))
                {
                    raster[(y * rowBytes) + (x / 8)] |= (byte)(0x80 >> (x % 8));
                }
            }
        }

        log?.Invoke($"Logo PNG decodificado: {width}×{height}px → raster {target}×{height} puntos (1bpp).");
        return new PngRaster { WidthDots = target, HeightDots = height, Bitmap1Bpp = raster };
    }

    // ------------------------------------------------------------ chunks y zlib

    private static (byte[] Header, byte[]? Palette, byte[] Idat) ReadChunks(byte[] png)
    {
        byte[]? header = null;
        byte[]? palette = null;
        List<byte[]>? idatParts = null;

        var position = Signature.Length;
        while (position + 8 <= png.Length)
        {
            var length = (int)ReadUInt32BE(png, position);
            var type = System.Text.Encoding.ASCII.GetString(png, position + 4, 4);
            position += 8;

            if (length < 0 || position + length > png.Length)
            {
                throw new EndOfStreamException($"chunk «{type}» truncado (declarado {length} bytes).");
            }

            switch (type)
            {
                case "IHDR":
                    header = png[position..(position + length)];
                    break;

                case "PLTE" when palette is null:
                    palette = png[position..(position + length)];
                    break;

                case "PLTE":
                    throw new FormatException("chunk PLTE duplicado");

                case "IDAT":
                    idatParts ??= [];
                    idatParts.Add(png[position..(position + length)]);
                    break;

                case "IEND":
                    return (header ?? throw new FormatException("PNG sin chunk IHDR"), palette, Concat(idatParts));
            }

            position += length + 4; // + CRC
        }

        throw new FormatException("PNG sin chunk IEND (¿truncado?)");

        static byte[] Concat(List<byte[]>? parts) =>
            parts is null || parts.Count == 0
                ? throw new FormatException("PNG sin datos de imagen (IDAT)")
                : parts.Count == 1 ? parts[0] : parts.SelectMany(p => p).ToArray();
    }

    private static byte[] Inflate(byte[] idat)
    {
        if (idat.Length < 2 || (idat[0] & 0x0F) != 8)
        {
            throw new NotSupportedException("el IDAT no empieza por un flujo zlib (deflate).");
        }

        using var source = new MemoryStream(idat, 2, idat.Length - 2);
        using var deflate = new DeflateStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    // ------------------------------------------------------------ desfiltrado

    private static void Unfilter(byte[] raw, int height, int stride, int rowStride, int bppBytes)
    {
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * rowStride;
            var filter = raw[rowStart];
            var offset = rowStart + 1;
            var end = rowStart + rowStride;

            if (filter > 4)
            {
                throw new NotSupportedException($"filtro de fila {filter} desconocido.");
            }

            switch (filter)
            {
                case 0: // None
                    break;

                case 1: // Sub: cada byte suma el vecino izquierdo ya desfiltrado
                    for (var i = offset + bppBytes; i < end; i++)
                    {
                        raw[i] = (byte)(raw[i] + raw[i - bppBytes]);
                    }

                    break;

                case 2: // Up: suma el de la fila anterior
                    if (y > 0)
                    {
                        for (var i = offset; i < end; i++)
                        {
                            raw[i] = (byte)(raw[i] + raw[i - rowStride]);
                        }
                    }

                    break;

                case 3: // Average: media del izquierdo y el de arriba
                    for (var i = offset; i < end; i++)
                    {
                        var left = i >= offset + bppBytes ? raw[i - bppBytes] : (byte)0;
                        var up = y > 0 ? raw[i - rowStride] : (byte)0;
                        raw[i] = (byte)(raw[i] + ((left + up) / 2));
                    }

                    break;

                case 4: // Paeth
                    for (var i = offset; i < end; i++)
                    {
                        var a = i >= offset + bppBytes ? raw[i - bppBytes] : (byte)0;
                        var b = y > 0 ? raw[i - rowStride] : (byte)0;
                        var c = y > 0 && i >= offset + bppBytes ? raw[i - rowStride - bppBytes] : (byte)0;
                        raw[i] = (byte)(raw[i] + PaethPredictor(a, b, c));
                    }

                    break;
            }
        }
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : (pb <= pc ? b : c);
    }

    // ------------------------------------------------------------ píxel → negro

    /// <summary>True si el píxel queda negro: luminancia &lt; 128 tras componer el alfa sobre blanco.</summary>
    private static bool IsDark(byte[] raw, int pixel, int colorType, byte[]? palette)
    {
        byte luminance;
        int alpha = 255;

        switch (colorType)
        {
            case 0: // gris
                luminance = raw[pixel];
                break;

            case 2: // RGB
                luminance = Luminance(raw[pixel], raw[pixel + 1], raw[pixel + 2]);
                break;

            case 3: // paleta
                var index = raw[pixel];
                if (palette is null || (index * 3) + 2 >= palette.Length)
                {
                    return false; // índice fuera de paleta: blanco
                }

                luminance = Luminance(palette[index * 3], palette[(index * 3) + 1], palette[(index * 3) + 2]);
                break;

            case 4: // gris + alfa
                luminance = raw[pixel];
                alpha = raw[pixel + 1];
                break;

            default: // 6 RGBA
                luminance = Luminance(raw[pixel], raw[pixel + 1], raw[pixel + 2]);
                alpha = raw[pixel + 3];
                break;
        }

        if (alpha == 255)
        {
            return luminance < 128;
        }

        if (alpha == 0)
        {
            return false; // transparente sobre papel: blanco
        }

        // Composición sobre blanco: g' = g·a + 255·(1−a)
        var blended = ((luminance * alpha) + (255 * (255 - alpha))) / 255;
        return blended < 128;
    }

    private static byte Luminance(byte r, byte g, byte b) =>
        (byte)((r * 299 + (g * 587) + (b * 114)) / 1000);

    private static uint ReadUInt32BE(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
