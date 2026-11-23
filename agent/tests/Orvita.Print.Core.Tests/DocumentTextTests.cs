using System.Text.Json;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>
/// Renderizado del documento neutro a texto legible (GUI-02/04): la vista del detalle del
/// trabajo en la GUI. Portable, sin lanzar — bloques desconocidos → marcador claro.
/// </summary>
public sealed class DocumentTextTests
{
    private static Document Document(params Block[] blocks) => new() { Version = 1, Blocks = [.. blocks] };

    [Fact]
    public void Sin_documento_devuelve_marcador_y_nunca_lanza()
    {
        Assert.Equal("(sin documento)", DocumentText.Render(null));
    }

    [Fact]
    public void Bloques_de_texto_se_renderizan_con_acentos_y_euro()
    {
        var text = DocumentText.Render(Document(new TextBlock { Value = "Pizza Margarita · jamón, piña, ñ, €, áéíóú" }));

        Assert.Contains("Pizza Margarita · jamón, piña, ñ, €, áéíóú", text);
    }

    [Fact]
    public void Texto_centrado_se_rellena_simetrico()
    {
        var text = DocumentText.Render(Document(new TextBlock { Value = "TIENDA", Align = TextAlign.Center }), 20);
        var line = text.Split('\n')[0].TrimEnd('\r');

        Assert.Equal(20, line.Length);
        Assert.StartsWith("  ", line);
        Assert.EndsWith("  ", line);
        Assert.Contains("TIENDA", line);
    }

    [Fact]
    public void Bloques_items_renderizan_cabecera_y_filas()
    {
        var text = DocumentText.Render(Document(new ItemsBlock
        {
            Columns = ["Cant.", "Producto", "Importe"],
            Align = ["left", "left", "right"],
            Rows = [["1", "Pizza Margarita", "12,50 €"]],
        }));

        Assert.Contains("Cant.", text);
        Assert.Contains("Pizza Margarita", text);
        Assert.Contains("12,50 €", text);
    }

    [Fact]
    public void Totales_divisor_y_marcadores_de_bloques_especiales()
    {
        var text = DocumentText.Render(Document(
            new TotalsBlock { Items = [new TotalItem { Label = "Total", Value = "12,50 €", Bold = true }] },
            new DividerBlock { Char = "=" },
            new QrBlock { Value = "https://tienda.example/pedido/1" },
            new ImageBlock { Data = "aG9sYQ==" },
            new CutBlock { Mode = CutMode.Full }));

        Assert.Contains("Total", text);
        Assert.Contains("12,50 €", text);
        Assert.Contains(new string('=', 42), text);
        Assert.Contains("[QR: https://tienda.example/pedido/1]", text);
        Assert.Contains("[logo]", text);
        Assert.Contains("corte", text);
    }

    [Fact]
    public void Bloque_desconocido_no_rompe_la_vista()
    {
        // Un tipo futuro que esta versión no conoce (Doc/04 §8): marcador, sin excepción.
        var json = """
            {"version":1,"blocks":[{"type":"futuro_magic","payload":{"algo":1}}]}
            """;
        var document = JsonSerializer.Deserialize<Document>(json, OrpJson.Default);

        var text = DocumentText.Render(document);

        Assert.Contains("[bloque desconocido: futuro_magic]", text);
    }

    [Fact]
    public void Feed_y_kv_se_renderizan()
    {
        var text = DocumentText.Render(Document(
            new KvBlock { Items = [new KvItem { Label = "Pedido", Value = "#778899" }, new KvItem { Label = "Local", Value = "Centro" }] },
            new FeedBlock { Lines = 2 }));

        Assert.Contains("Pedido", text);
        Assert.Contains("#778899", text);
        Assert.Contains("Local", text);
        Assert.Contains("Centro", text);
    }
}
