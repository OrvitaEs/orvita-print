using System.Text;
using System.Text.Json;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>
/// Renderizador ESC/POS (Doc/03 §6.5, Doc/05 §4.3): bytes de comando por bloque, cadena
/// obligatoria de codificación en CP850/858/437 con sustitución segura y aviso, alineación
/// de kv/items a 32 y 48 columnas, y el ejemplo completo de Doc/04 §7.3 sin excepción.
/// </summary>
public sealed class EscPosRendererTests
{
    private static readonly Encoding Cp850 = Encoding.GetEncoding(850);

    // ------------------------------------------------------------- helpers

    private static Document Document(params Block[] blocks) => new()
    {
        Version = 1,
        Meta = new DocumentMeta { JobId = 1844, Copies = 1, PrinterRole = "default" },
        Paper = new DocumentPaper { WidthMm = 80 },
        Blocks = [.. blocks],
    };

    private static byte[] Render(Document document, EscPosRendererOptions? options = null, List<string>? logs = null) =>
        new EscPosRenderer(options, log => logs?.Add(log)).Render(document);

    private static void AssertContains(byte[] haystack, byte[] needle, string? because = null) =>
        Assert.True(haystack.AsSpan().IndexOf((ReadOnlySpan<byte>)needle) >= 0,
            because ?? $"No se encontró {Convert.ToHexString(needle)} dentro de {Convert.ToHexString(haystack)}");

    private static void AssertNotContains(byte[] haystack, byte[] needle) =>
        Assert.True(haystack.AsSpan().IndexOf((ReadOnlySpan<byte>)needle) < 0,
            $"No debería aparecer {Convert.ToHexString(needle)} en {Convert.ToHexString(haystack)}");

    private static int CountOccurrences(byte[] haystack, byte[] needle)
    {
        var count = 0;
        var position = 0;
        int index;
        while ((index = haystack.AsSpan(position).IndexOf((ReadOnlySpan<byte>)needle)) >= 0)
        {
            count++;
            position += index + 1;
        }

        return count;
    }

    // ------------------------------------------------------------- texto y comandos

    [Fact]
    public void Texto_plano_emite_init_texto_y_salto_de_linea()
    {
        var bytes = Render(Document(new TextBlock { Value = "A" }));
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x41, 0x0A }, bytes);
    }

    [Fact]
    public void Texto_center_bold_double_emite_los_comandos_en_orden_y_los_resetea()
    {
        var bytes = Render(Document(new TextBlock { Value = "A", Align = TextAlign.Center, Bold = true, Size = TextSize.Double }));
        var expected = new byte[]
        {
            0x1B, 0x40,             // ESC @
            0x1B, 0x61, 0x01,       // ESC a 1 (centrar: la impresora centra según el ancho real)
            0x1B, 0x45, 0x01,       // ESC E 1 (negrita)
            0x1D, 0x21, 0x11,       // GS ! 0x11 (doble)
            0x41,                   // "A"
            0x0A,
            0x1D, 0x21, 0x00,       // reset tamaño
            0x1B, 0x45, 0x00,       // reset negrita
            0x1B, 0x61, 0x00,       // reset alineación
        };
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Texto_triple_emite_GS_bang_22()
    {
        var bytes = Render(Document(new TextBlock { Value = "A", Size = TextSize.Triple }));
        AssertContains(bytes, [0x1D, 0x21, 0x22]);
    }

    [Fact]
    public void Texto_invertido_emite_GS_B_y_su_reset()
    {
        var bytes = Render(Document(new TextBlock { Value = "A", Invert = true }));
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1D, 0x42, 0x01, 0x41, 0x0A, 0x1D, 0x42, 0x00 }, bytes);
    }

    [Fact]
    public void Texto_derecha_emite_ESC_a_2()
    {
        var bytes = Render(Document(new TextBlock { Value = "A", Align = TextAlign.Right }));
        AssertContains(bytes, [0x1B, 0x61, 0x02]);
        AssertContains(bytes, [0x1B, 0x61, 0x00]); // reset al final del bloque
    }

    [Fact]
    public void Divider_rellena_las_columnas_con_el_caracter()
    {
        var bytes = Render(Document(new DividerBlock()));
        Assert.Equal(new byte[] { 0x1B, 0x40 }, bytes[..2]);
        AssertContains(bytes, Encoding.ASCII.GetBytes(new string('-', 48) + "\n"));
    }

    [Fact]
    public void Feed_y_cut_se_emiten_segun_los_bloques_y_no_se_anade_nada_mas()
    {
        var bytes = Render(Document(new FeedBlock { Lines = 3 }, new CutBlock { Mode = CutMode.Partial }));
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x0A, 0x0A, 0x0A, 0x1D, 0x56, 0x01 }, bytes);
    }

    [Fact]
    public void Cut_full_emite_GS_V_00()
    {
        var bytes = Render(Document(new CutBlock { Mode = CutMode.Full }));
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1D, 0x56, 0x00 }, bytes);
    }

    [Fact]
    public void Drawer_emite_la_apertura_del_cajon()
    {
        var bytes = Render(Document(new DrawerBlock()));
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1B, 0x70, 0x00, 0x19, 0xFA }, bytes);
    }

    // ------------------------------------------------------------- codificación (Doc/05 §4.3)

    [Theory]
    [InlineData(EscPosEncoding.Cp850, "Pizza Margarita · jamón, piña, ñ, E, áéíóú", "C/ Cañada 3, 2º B - 28801 Alcalá")]
    [InlineData(EscPosEncoding.Cp858, "Pizza Margarita · jamón, piña, ñ, €, áéíóú", "C/ Cañada 3, 2º B - 28801 Alcalá")]
    [InlineData(EscPosEncoding.Cp437, "Pizza Margarita · jamón, piña, ñ, E, áéíóú", "C/ Cañada 3, 2º B - 28801 Alcalá")]
    public void La_cadena_obligatoria_se_imprime_completa_en_las_tres_tablas(
        EscPosEncoding encoding, string line1Expected, string line2Expected)
    {
        var document = Document(
            new TextBlock { Value = "Pizza Margarita · jamón, piña, ñ, €, áéíóú" },
            new TextBlock { Value = "C/ Cañada 3, 2º B — 28801 Alcalá" });

        var logs = new List<string>();
        var bytes = Render(document, new EscPosRendererOptions { TextEncoding = encoding }, logs);

        // Sin excepción, sin bytes fuera de tabla (el propio decoder estricto lo valida),
        // y el texto se recupera completo con las sustituciones documentadas.
        var table = Encoding.GetEncoding((int)encoding, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var decoded = table.GetString(bytes, 2, bytes.Length - 2); // se salta ESC @
        Assert.Equal(line1Expected + "\n" + line2Expected + "\n", decoded);
        AssertNotContains(bytes, [(byte)'?']); // nada cayó a «?» de último recurso
        AssertContains(bytes, [0xA4]);         // ñ en las tres tablas

        var log = string.Join("\n", logs);
        if (encoding == EscPosEncoding.Cp858)
        {
            Assert.Contains("«—»→«-»", log);
            Assert.DoesNotContain("«€»", log); // el euro sí existe en CP858
        }
        else
        {
            Assert.Contains("«€»→«E»", log);
            Assert.Contains("«—»→«-»", log);
        }
    }

    [Fact]
    public void Un_documento_ascii_no_registra_ninguna_sustitucion()
    {
        var logs = new List<string>();
        _ = Render(Document(new TextBlock { Value = "Pizza Margarita" }), logs: logs);
        Assert.Empty(logs);
    }

    // ------------------------------------------------------------- kv

    [Theory]
    [InlineData(58, 32, 21)]
    [InlineData(80, 48, 37)]
    public void El_bloque_kv_alinea_etiqueta_y_valor_al_ancho_del_papel(int widthMm, int columns, int gap)
    {
        var document = Document(new KvBlock { Items = [new KvItem { Label = "Pedido", Value = "#5678" }] });
        document.Paper = new DocumentPaper { WidthMm = widthMm };

        var bytes = Render(document);

        var expectedLine = Encoding.ASCII.GetBytes($"Pedido{new string(' ', gap)}#5678\n");
        AssertContains(bytes, expectedLine, $"La línea kv debe medir exactamente {columns} columnas.");
    }

    [Fact]
    public void El_separador_de_kv_rellena_el_hueco()
    {
        var document = Document(new KvBlock
        {
            Separator = ".",
            Items = [new KvItem { Label = "Pedido", Value = "#5678" }],
        });
        document.Paper = new DocumentPaper { WidthMm = 58 };

        AssertContains(Render(document), Encoding.ASCII.GetBytes($"Pedido{new string('.', 21)}#5678\n"));
    }

    [Fact]
    public void Una_linea_kv_que_no_cabe_se_parte_en_dos()
    {
        var document = Document(new KvBlock
        {
            Items = [new KvItem { Label = "Cliente del pedido original 2026", Value = "#5678" }],
        });
        document.Paper = new DocumentPaper { WidthMm = 58 }; // 32 columnas: 32 + 5 no caben en una línea

        var bytes = Render(document);
        AssertContains(bytes, Encoding.ASCII.GetBytes("Cliente del pedido original 2026\n"));
        AssertContains(bytes, Encoding.ASCII.GetBytes(new string(' ', 27) + "#5678\n"));
    }

    // ------------------------------------------------------------- items

    [Fact]
    public void El_bloque_items_alinea_a_32_columnas_en_papel_de_58()
    {
        var document = Document(new ItemsBlock
        {
            Columns = ["Cant", "Producto", "Importe"],
            Align = ["left", "left", "right"],
            Rows = [["1", "Margarita", "12,00"]],
        });
        document.Paper = new DocumentPaper { WidthMm = 58 };

        var bytes = Render(document);

        // Anchuras naturales 4/9/7 (22 con separadores): el sobrante de 10 va a «Producto» → 19.
        var header = "Cant" + " " + "Producto" + new string(' ', 11) + " " + "Importe";
        var row = "1" + new string(' ', 3) + " " + "Margarita" + new string(' ', 10) + " " + "  12,00";
        AssertContains(bytes, Encoding.ASCII.GetBytes(header + "\n"));
        AssertContains(bytes, Encoding.ASCII.GetBytes(row + "\n"));
    }

    [Fact]
    public void El_bloque_items_alinea_a_48_columnas_en_papel_de_80()
    {
        var document = Document(new ItemsBlock
        {
            Columns = ["Cant", "Producto", "Importe"],
            Align = ["left", "left", "right"],
            Rows = [["1", "Margarita grande", "12,00"]],
        });

        var bytes = Render(document);

        // Anchuras naturales 4/16/7 (29 con separadores): el sobrante de 19 va a «Producto» → 35.
        var header = "Cant" + " " + "Producto" + new string(' ', 27) + " " + "Importe";
        var row = "1" + new string(' ', 3) + " " + "Margarita grande" + new string(' ', 19) + " " + "  12,00";
        AssertContains(bytes, Encoding.ASCII.GetBytes(header + "\n"));
        AssertContains(bytes, Encoding.ASCII.GetBytes(row + "\n"));
    }

    [Fact]
    public void El_bloque_items_recorta_cuando_las_columnas_no_caben()
    {
        var document = Document(new ItemsBlock
        {
            Columns = ["Col1", "Col2", "Col3"],
            Rows = [["XXXXXXXXXX", "XXXXXXXXXX", "XXXXXXXXXX"]],
        });

        var bytes = Render(document, new EscPosRendererOptions { Columns = 20 });

        // 10+10+10+2 separadores = 32 > 20: se recorta de la más ancha hasta caber.
        var decoded = Cp850.GetString(bytes, 2, bytes.Length - 2);
        Assert.All(decoded.Split('\n'), line => Assert.True(line.Length <= 20, $"«{line}» supera 20 columnas."));
    }

    // ------------------------------------------------------------- totals

    [Fact]
    public void El_bloque_totals_alinea_y_aplica_negrita_por_fila()
    {
        var document = Document(new TotalsBlock
        {
            Items =
            [
                new TotalItem { Label = "Subtotal", Value = "34,00" },
                new TotalItem { Label = "TOTAL", Value = "36,00", Bold = true },
            ],
        });

        var bytes = Render(document);

        AssertContains(bytes, Encoding.ASCII.GetBytes($"Subtotal{new string(' ', 35)}34,00\n"));
        AssertContains(bytes, [0x1B, 0x45, 0x01, .. Encoding.ASCII.GetBytes($"TOTAL{new string(' ', 38)}36,00\n"), 0x1B, 0x45, 0x00]);
    }

    // ------------------------------------------------------------- image / qr / barcode / raw

    [Fact]
    public void El_bloque_image_emite_el_raster_GS_v_0()
    {
        var bitmap = Convert.ToBase64String([0xFF, 0x81, 0xFF]);
        var bytes = Render(Document(new ImageBlock { Data = bitmap, WidthDots = 8 }));
        AssertContains(bytes, [0x1D, 0x76, 0x30, 0x00, 0x01, 0x00, 0x03, 0x00, 0xFF, 0x81, 0xFF]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-es-base64!!")]
    public void El_bloque_image_invalido_se_descarta_con_aviso(string? data)
    {
        var logs = new List<string>();
        var bytes = Render(Document(new ImageBlock { Data = data, WidthDots = 8 }), logs: logs);
        Assert.Equal(2, bytes.Length); // solo ESC @
        Assert.NotEmpty(logs);
    }

    [Fact]
    public void El_bloque_image_sin_width_dots_se_descarta_con_aviso()
    {
        var logs = new List<string>();
        var bytes = Render(Document(new ImageBlock { Data = Convert.ToBase64String([0xFF]) }), logs: logs);
        Assert.Equal(2, bytes.Length);
        Assert.NotEmpty(logs);
    }

    [Fact]
    public void El_bloque_qr_emite_la_secuencia_GS_k_completa()
    {
        var bytes = Render(Document(new QrBlock { Value = "https://orvita.example/1844" }));

        AssertContains(bytes, [0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00]); // modelo 2
        AssertContains(bytes, [0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 0x04]);       // módulo 4
        AssertContains(bytes, [0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31]);       // EC nivel M
        AssertContains(bytes, [0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30]);       // imprimir
        AssertContains(bytes, Encoding.ASCII.GetBytes("https://orvita.example/1844"));
    }

    [Fact]
    public void El_bloque_barcode_CODE128_anade_el_prefijo_de_subconjunto()
    {
        var bytes = Render(Document(new BarcodeBlock { Value = "PED-1844", Symbology = BarcodeSymbology.Code128 }));
        AssertContains(bytes, Encoding.ASCII.GetBytes("{BPED-1844"));
        AssertContains(bytes, [0x1D, 0x6B, 0x49]);
    }

    [Fact]
    public void El_bloque_barcode_CODE128_sustituye_lo_no_ascii_por_seguro()
    {
        var logs = new List<string>();
        var bytes = Render(Document(new BarcodeBlock { Value = "AÑ", Symbology = BarcodeSymbology.Code128 }), logs: logs);

        AssertContains(bytes, Encoding.ASCII.GetBytes("{BA?")); // Ñ fuera de CODE128-B → «?»
        Assert.NotEmpty(logs);
    }

    [Fact]
    public void El_bloque_barcode_EAN13_imprime_los_digitos()
    {
        var bytes = Render(Document(new BarcodeBlock { Value = "8412345678903", Symbology = BarcodeSymbology.Ean13 }));
        AssertContains(bytes, [0x1D, 0x6B, 0x43, 0x0D, .. Encoding.ASCII.GetBytes("8412345678903")]);
    }

    [Fact]
    public void El_bloque_barcode_EAN13_no_valido_se_descarta_con_aviso()
    {
        var logs = new List<string>();
        var bytes = Render(Document(new BarcodeBlock { Value = "PED-1844", Symbology = BarcodeSymbology.Ean13 }), logs: logs);

        AssertNotContains(bytes, [0x1D, 0x6B]);
        Assert.NotEmpty(logs);
    }

    [Fact]
    public void El_bloque_raw_vuelca_los_bytes_decodificados()
    {
        var bytes = Render(Document(new RawBlock { Data = Convert.ToBase64String([0x1B, 0x16, 0x01]) }));
        Assert.Equal(new byte[] { 0x1B, 0x40, 0x1B, 0x16, 0x01 }, bytes);
    }

    // ------------------------------------------------------------- tolerancia y límites

    [Fact]
    public void Los_bloques_desconocidos_se_ignoran_con_aviso()
    {
        var document = JsonSerializer.Deserialize<Document>(
            """
            {"version":1,"meta":{"copies":1},"paper":{"width_mm":80},
             "blocks":[{"type":"loyalty","points":42},{"type":"text","value":"OK"}]}
            """,
            OrpJson.Default);

        Assert.NotNull(document);
        var logs = new List<string>();
        var bytes = Render(document, logs: logs);

        AssertContains(bytes, Encoding.ASCII.GetBytes("OK\n"));
        Assert.Contains(logs, l => l.Contains("loyalty"));
    }

    [Fact]
    public void Una_version_superior_a_la_conocida_se_rechaza()
    {
        var document = Document(new TextBlock { Value = "A" });
        document.Version = 2;

        var exception = Assert.Throws<NotSupportedException>(() => Render(document));
        Assert.Contains("orp_unsupported_document", exception.Message);
    }

    [Theory]
    [InlineData(30, 32)]
    [InlineData(58, 32)]
    [InlineData(59, 48)]
    [InlineData(80, 48)]
    public void El_ancho_de_papel_se_mapea_a_columnas(int widthMm, int expectedColumns) =>
        Assert.Equal(expectedColumns, EscPosRendererOptions.ColumnsForPaperWidth(widthMm));

    [Fact]
    public void Las_columnas_se_pueden_forzar_por_opcion()
    {
        var bytes = Render(Document(new DividerBlock()), new EscPosRendererOptions { Columns = 32 });
        AssertContains(bytes, Encoding.ASCII.GetBytes(new string('-', 32) + "\n"));
        AssertNotContains(bytes, Encoding.ASCII.GetBytes(new string('-', 33)));
    }

    // ------------------------------------------------------------- ejemplo completo (Doc/04 §7.3)

    [Fact]
    public void El_ejemplo_completo_del_contrato_renderiza_sin_excepcion()
    {
        var document = JsonSerializer.Deserialize<Document>(DocumentRoundtripTests.ExampleJson, OrpJson.Default);
        Assert.NotNull(document);

        var bytes = Render(document);

        // Empieza inicializando y termina en feed(3) + corte parcial, sin nada extra.
        Assert.Equal(new byte[] { 0x1B, 0x40 }, bytes[..2]);
        Assert.Equal(new byte[] { 0x0A, 0x0A, 0x0A }, bytes[^6..^3]);
        Assert.Equal(new byte[] { 0x1D, 0x56, 0x01 }, bytes[^3..]);

        // kv, items y totals alineados a 48 columnas (papel de 80 mm).
        AssertContains(bytes, Encoding.ASCII.GetBytes($"Pedido{new string(' ', 37)}#5678\n"));
        AssertContains(bytes, Encoding.ASCII.GetBytes($"Cant" + " " + "Producto" + new string(' ', 27) + " " + "Importe\n"));
        AssertContains(bytes, Encoding.ASCII.GetBytes($"TOTAL{new string(' ', 38)}36,00\n"));

        // El título doble va centrado por la impresora (ESC a 1), con negrita y tamaño.
        AssertContains(bytes, [0x1B, 0x61, 0x01, 0x1B, 0x45, 0x01, 0x1D, 0x21, 0x11, .. Encoding.ASCII.GetBytes("PIZZERIA RIO ROSSO"), 0x0A]);

        // meta.copies lo aplica el transporte, no el renderizador: exactamente una copia.
        Assert.Equal(1, CountOccurrences(bytes, Encoding.ASCII.GetBytes("Gracias por su pedido")));
    }
}
