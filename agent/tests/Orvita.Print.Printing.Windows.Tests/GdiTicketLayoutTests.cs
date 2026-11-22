using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Printing.Windows;
using Xunit;

namespace Orvita.Print.Printing.Windows.Tests;

/// <summary>
/// Maquetación del respaldo GDI (Doc/03 §6.5): mismas columnas y reparto que el renderizador
/// ESC/POS, kv/totals a dos extremos, items con anchos distribuidos, feed en blanco e
/// ignorado limpio de cut/drawer/raw y de bloques desconocidos.
/// </summary>
public sealed class GdiTicketLayoutTests
{
    private const int Columns = EscPosRendererOptions.Columns80Mm; // 48

    private static List<LayoutLine> Build(Action<Document> configure, List<string>? logs = null)
    {
        var document = new Document { Version = 1, Paper = new DocumentPaper { WidthMm = 80 } };
        configure(document);
        return GdiTicketLayout.Build(document, log: logs is null ? null : logs.Add);
    }

    [Fact]
    public void Texto_con_alineacion_negrita_y_tamano()
    {
        var lines = Build(d => d.Blocks.Add(new TextBlock
        {
            Value = "PIZZERIA RIO ROSSO",
            Align = TextAlign.Center,
            Bold = true,
            Size = TextSize.Double,
        }));

        var line = Assert.Single(lines);
        Assert.Equal("PIZZERIA RIO ROSSO", line.Text);
        Assert.True(line.Bold);
        Assert.Equal(LayoutSize.Double, line.Size);
        Assert.Equal(TextAlign.Center, line.Align);
    }

    [Fact]
    public void Kv_llena_la_linea_entre_etiqueta_y_valor()
    {
        var lines = Build(d => d.Blocks.Add(new KvBlock
        {
            Items = [new KvItem { Label = "Pedido", Value = "#5678" }],
        }));

        var line = Assert.Single(lines);
        Assert.Equal(Columns, line.Text.Length);
        Assert.StartsWith("Pedido", line.Text);
        Assert.EndsWith("#5678", line.Text);
        Assert.All(line.Text[6..^5], c => Assert.Equal(' ', c));
    }

    [Fact]
    public void Totals_bold_llega_a_la_linea()
    {
        var lines = Build(d => d.Blocks.Add(new TotalsBlock
        {
            Items = [new TotalItem { Label = "TOTAL", Value = "36,00", Bold = true }],
        }));

        var line = Assert.Single(lines);
        Assert.True(line.Bold);
        Assert.EndsWith("36,00", line.Text);
    }

    [Fact]
    public void Items_reparte_anchos_como_el_renderizador_esc_pos()
    {
        var lines = Build(d => d.Blocks.Add(new ItemsBlock
        {
            Columns = ["Cant", "Producto", "Importe"],
            Align = ["left", "left", "right"],
            Rows = [["1", "Margarita grande", "12,00"]],
        }));

        // Cabecera + fila, misma anchura (48) y misma distribución de anchos que ESC/POS.
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(Columns, l.Text.Length)); // 48 columnas con separadores incluidos
        Assert.StartsWith("Cant", lines[0].Text);
        Assert.EndsWith("Importe", lines[0].Text);
    }

    [Fact]
    public void Divider_rellena_toda_la_linea()
    {
        var lines = Build(d => d.Blocks.Add(new DividerBlock { Char = "=" }));

        var line = Assert.Single(lines);
        Assert.Equal(new string('=', Columns), line.Text);
    }

    [Fact]
    public void Feed_produce_lineas_en_blanco_y_cut_drawer_raw_no_pintan()
    {
        var lines = Build(d =>
        {
            d.Blocks.Add(new FeedBlock { Lines = 3 });
            d.Blocks.Add(new CutBlock { Mode = CutMode.Partial });
            d.Blocks.Add(new DrawerBlock { Action = DrawerAction.Open });
            d.Blocks.Add(new RawBlock { Data = Convert.ToBase64String([0x1B, 0x70]) });
        });

        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.True(l.IsBlank));
    }

    [Fact]
    public void Qr_y_barcode_se_maquetan_como_texto_legible()
    {
        var logs = new List<string>();
        var lines = Build(d =>
        {
            d.Blocks.Add(new QrBlock { Value = "PEDIDO-5678" });
            d.Blocks.Add(new BarcodeBlock { Value = "841234567890", Symbology = BarcodeSymbology.Ean13 });
        }, logs);

        Assert.Equal("[QR] PEDIDO-5678", lines[0].Text);
        Assert.Equal("[barcode] 841234567890", lines[1].Text);
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public void Papel_de_58_mm_usa_32_columnas()
    {
        var document = new Document
        {
            Version = 1,
            Paper = new DocumentPaper { WidthMm = 58 },
            Blocks = [new DividerBlock()],
        };

        var line = Assert.Single(GdiTicketLayout.Build(document));
        Assert.Equal(EscPosRendererOptions.Columns58Mm, line.Text.Length);
    }

    [Fact]
    public void Bloque_desconocido_se_ignora_con_aviso()
    {
        var logs = new List<string>();
        var lines = Build(d => d.Blocks.Add(new UnknownBlock("firmaDigital", default)), logs);

        Assert.Empty(lines);
        Assert.Contains(logs, l => l.Contains("firmaDigital"));
    }

    [Fact]
    public void Image_con_raster_1bpp_se_mantiene()
    {
        // 8 puntos de ancho, 2 filas: el formato que el procesador normaliza (F2-07).
        var raster = new byte[] { 0x80, 0x40 };
        var lines = Build(d => d.Blocks.Add(new ImageBlock
        {
            Data = Convert.ToBase64String(raster),
            WidthDots = 8,
        }));

        var line = Assert.Single(lines);
        Assert.NotNull(line.Image);
        Assert.Equal(8, line.Image!.WidthDots);
        Assert.Equal(2, line.Image.HeightDots);
    }
}
