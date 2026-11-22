using System.IO.Compression;
using System.Text;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Printing.Windows;
using Xunit;

namespace Orvita.Print.Printing.Windows.Tests;

/// <summary>
/// Fábrica de PNG mínimos para las pruebas del decodificador: chunks válidos (con CRC32) y
/// flujo zlib real (cabecera 0x78 0x01 + DeflateStream). Permite cubrir gris, RGB, paleta,
/// gris+alfa, RGBA, reescalado, varios IDAT y los caminos de error.
/// </summary>
internal static class TestPng
{
    internal static byte[] Build(
        int width,
        int height,
        Func<int, int, (byte R, byte G, byte B, byte A)> pixel,
        byte colorType = 6,
        int? targetScale = null,
        bool interlaced = false,
        int bitDepth = 8,
        int idatSplit = 1)
    {
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var stride = (width * channels) + 1;
        var filtered = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            filtered[y * stride] = 0; // filtro None
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                var o = (y * stride) + 1 + (x * channels);
                switch (colorType)
                {
                    case 0:
                        filtered[o] = g;
                        break;
                    case 2:
                        filtered[o] = r;
                        filtered[o + 1] = g;
                        filtered[o + 2] = b;
                        break;
                    case 3:
                        filtered[o] = r; // índice de paleta
                        break;
                    case 4:
                        filtered[o] = g;
                        filtered[o + 1] = a;
                        break;
                    default:
                        filtered[o] = r;
                        filtered[o + 1] = g;
                        filtered[o + 2] = b;
                        filtered[o + 3] = a;
                        break;
                }
            }
        }

        byte[]? palette = null;
        if (colorType == 3)
        {
            // Paleta: 0 = negro, 1 = blanco.
            palette = [0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var (r, _, _, _) = pixel(x, y);
                    filtered[(y * stride) + 1 + x] = r == 0 ? (byte)0 : (byte)1;
                }
            }
        }

        using var idat = new MemoryStream();
        idat.WriteByte(0x78);
        idat.WriteByte(0x01);
        using var deflate = new DeflateStream(idat, CompressionLevel.Fastest, leaveOpen: true);
        deflate.Write(filtered);
        deflate.Dispose();

        using var output = new MemoryStream();
        output.Write(Signature);
        WriteChunk(output, "IHDR", [
            .. ToBytesBE((uint)width), .. ToBytesBE((uint)height),
            (byte)bitDepth, colorType, 0, 0, (byte)(interlaced ? 1 : 0),
        ]);
        if (palette is not null)
        {
            WriteChunk(output, "PLTE", palette);
        }

        var idatBytes = idat.ToArray();
        if (idatSplit <= 1)
        {
            WriteChunk(output, "IDAT", idatBytes);
        }
        else
        {
            // Parte el IDAT en trozos (los decodificadores deben concatenarlos).
            var chunkSize = Math.Max(1, idatBytes.Length / idatSplit);
            for (var offset = 0; offset < idatBytes.Length; offset += chunkSize)
            {
                WriteChunk(output, "IDAT", idatBytes[offset..Math.Min(idatBytes.Length, offset + chunkSize)]);
            }
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static void WriteChunk(MemoryStream stream, string type, byte[] data)
    {
        stream.Write(ToBytesBE((uint)data.Length));
        stream.Write(Encoding.ASCII.GetBytes(type));
        stream.Write(data);
        stream.Write(ToBytesBE(Crc32(Encoding.ASCII.GetBytes(type), data)));
    }

    private static byte[] ToBytesBE(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in type.Concat(data))
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320 & ((~(crc & 1)) + 1));
            }
        }

        return ~crc;
    }
}

/// <summary>
/// Decodificador PNG puro (Doc/04 §7.2): raster 1bpp correcto por tipo de color, reescalado,
/// varios IDAT y rechazo explícito (null + aviso, nunca excepción) de PNG no soportados.
/// </summary>
public sealed class PngDecoderTests
{
    private readonly PngDecoder _decoder = new();

    [Fact]
    public void Rgba_un_cuadrado_negro_queda_en_bits()
    {
        // 8×4: solo la primera columna negra (en escala de grises, no un matiz de color).
        var png = TestPng.Build(8, 4, (x, _) => (x == 0 ? (byte)0 : (byte)255, x == 0 ? (byte)0 : (byte)255, x == 0 ? (byte)0 : (byte)255, (byte)255));
        var raster = _decoder.TryDecodeToRaster(Convert.ToBase64String(png));

        Assert.NotNull(raster);
        Assert.Equal(8, raster!.WidthDots);
        Assert.Equal(4, raster.HeightDots);
        Assert.Equal(4, raster.Bitmap1Bpp.Length); // 1 byte/fila × 4 filas
        Assert.Equal(0x80, raster.Bitmap1Bpp[0]);  // MSB primero: x=0 es el bit 0x80
        Assert.Equal(0x80, raster.Bitmap1Bpp[2]);
    }

    [Fact]
    public void Gris_umbraliza_a_128()
    {
        var png = TestPng.Build(4, 1, (x, _) => ((byte)0, x == 0 ? (byte)127 : (byte)255, 0, 0), colorType: 0);
        var raster = _decoder.TryDecodeToRaster(Convert.ToBase64String(png));

        Assert.NotNull(raster);
        Assert.Equal(0x80, raster!.Bitmap1Bpp[0]); // 127 < 128 → negro; el resto claro
    }

    [Fact]
    public void Rgb_negro_en_el_centro()
    {
        var png = TestPng.Build(16, 1, (x, _) =>
        {
            var v = x is >= 7 and <= 8 ? (byte)0 : (byte)255;
            return (v, v, v, (byte)255);
        }, colorType: 2);
        var raster = _decoder.TryDecodeToRaster(Convert.ToBase64String(png));

        Assert.NotNull(raster);
        Assert.Equal(2, raster!.Bitmap1Bpp.Length);
        // x=7 → bit 0x01 del primer byte; x=8 → bit 0x80 del segundo.
        Assert.Equal([0x01, 0x80], raster.Bitmap1Bpp);
    }

    [Fact]
    public void Paleta_negro_y_blanco()
    {
        var png = TestPng.Build(8, 1, (x, _) => (x == 0 ? (byte)0 : (byte)255, 0, 0, 0), colorType: 3);
        var raster = _decoder.TryDecodeToRaster(Convert.ToBase64String(png));

        Assert.NotNull(raster);
        Assert.Equal(0x80, raster!.Bitmap1Bpp[0]);
    }

    [Fact]
    public void Gris_alfa_transparente_sobre_blanco_no_mancha()
    {
        // Todo el píxel gris claro (240) con alfa 10: compuesto sobre blanco sigue siendo claro.
        var png = TestPng.Build(4, 1, (_, _) => ((byte)0, (byte)240, 0, (byte)10), colorType: 4);
        var raster = _decoder.TryDecodeToRaster(Convert.ToBase64String(png));

        Assert.NotNull(raster);
        Assert.Equal(0x00, raster!.Bitmap1Bpp[0]);
    }

    [Fact]
    public void Reescala_por_vecino_mas_cercano_al_ancho_objetivo()
    {
        var png = TestPng.Build(8, 2, (x, _) =>
        {
            var v = x < 4 ? (byte)0 : (byte)255;
            return (v, v, v, (byte)255);
        });
        var raster = _decoder.TryDecodeToRaster(Convert.ToBase64String(png), targetWidthDots: 4);

        Assert.NotNull(raster);
        Assert.Equal(4, raster!.WidthDots);
        // Vecino más cercano 8→4: fuentes x = 0,2,4,6 → dos bits negros a la izquierda.
        Assert.Equal(0b1100_0000, raster.Bitmap1Bpp[0]);
    }

    [Fact]
    public void Varios_chunks_idat_se_concatenan()
    {
        var png = TestPng.Build(8, 4, (x, _) =>
        {
            var v = x == 0 ? (byte)0 : (byte)255;
            return (v, v, v, (byte)255);
        }, idatSplit: 4);
        var raster = _decoder.TryDecodeToRaster(Convert.ToBase64String(png));

        Assert.NotNull(raster);
        Assert.Equal(0x80, raster!.Bitmap1Bpp[0]);
    }

    [Theory]
    [InlineData("no-es-base64-***")]
    [InlineData("")]
    public void Base64_roto_devuelve_null_con_aviso_y_no_excepcion(string base64)
    {
        var logs = new List<string>();
        var raster = _decoder.TryDecodeToRaster(base64, log: logs.Add);

        Assert.Null(raster);
        Assert.NotEmpty(logs);
    }

    [Fact]
    public void Firma_incorrecta_devuelve_null_y_avisa()
    {
        var logs = new List<string>();
        // Un mapa de bits crudo (como la plantilla v0) no lleva firma PNG.
        var raster = _decoder.TryDecodeToRaster(Convert.ToBase64String(new byte[64]), log: logs.Add);

        Assert.Null(raster);
        Assert.Contains(logs, l => l.Contains("firma PNG"));
    }

    [Fact]
    public void Entrelazado_y_profundidad_no_sopestada_devuelven_null()
    {
        var interlaced = TestPng.Build(4, 4, (_, _) => (0, 0, 0, 255), interlaced: true);
        var deep = TestPng.Build(4, 4, (_, _) => (0, 0, 0, 255), bitDepth: 16);

        var logs = new List<string>();
        Assert.Null(_decoder.TryDecodeToRaster(Convert.ToBase64String(interlaced), log: logs.Add));
        Assert.Null(_decoder.TryDecodeToRaster(Convert.ToBase64String(deep), log: logs.Add));
        Assert.Single(logs, l => l.Contains("entrelazado"));
        Assert.Single(logs, l => l.Contains("profundidad"));
    }

    [Fact]
    public void Truncado_devuelve_null()
    {
        var png = TestPng.Build(32, 32, (_, _) => (0, 0, 0, 255));
        var truncado = png[..(png.Length / 2)];

        var logs = new List<string>();
        Assert.Null(_decoder.TryDecodeToRaster(Convert.ToBase64String(truncado), log: logs.Add));
    }
}
