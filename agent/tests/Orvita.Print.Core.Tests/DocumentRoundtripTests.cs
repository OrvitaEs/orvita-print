using System.Text.Json;
using System.Text.Json.Nodes;
using Orvita.Print.Contracts;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>
/// Roundtrip del documento neutro de ejemplo (Doc/04 §7.3): fiel, incluidos acentos,
/// y tolerante con bloques desconocidos (04 §8).
/// </summary>
public sealed class DocumentRoundtripTests
{
    /// <summary>Ejemplo completo de Doc/04 §7.3, tal cual.</summary>
    internal const string ExampleJson = """
        {
          "version": 1,
          "meta": { "job_id": 1844, "order_id": 5678, "location_code": "pueblo-b",
                    "created_at": "2026-11-19T20:14:58+01:00", "copies": 1,
                    "printer_role": "default" },
          "paper": { "width_mm": 80 },
          "blocks": [
            { "type": "text", "value": "PIZZERIA RIO ROSSO", "align": "center",
              "bold": true, "size": "double" },
            { "type": "text", "value": "Pueblo B · 20:14", "align": "center" },
            { "type": "divider" },
            { "type": "kv", "items": [
                { "label": "Pedido", "value": "#5678" },
                { "label": "Cliente", "value": "María López" },
                { "label": "Teléfono", "value": "600 000 000" },
                { "label": "Entrega", "value": "Calle Mayor 3" }
            ]},
            { "type": "items",
              "columns": ["Cant", "Producto", "Importe"],
              "align": ["left", "left", "right"],
              "rows": [
                ["1", "Margarita grande", "12,00"],
                ["2", "Barbacoa", "22,00"]
              ]},
            { "type": "totals", "items": [
                { "label": "Subtotal", "value": "34,00" },
                { "label": "Envío", "value": "2,00" },
                { "label": "TOTAL", "value": "36,00", "bold": true }
            ]},
            { "type": "text", "value": "Gracias por su pedido", "align": "center" },
            { "type": "feed", "lines": 3 },
            { "type": "cut", "mode": "partial" }
          ]
        }
        """;

    [Fact]
    public void Parse_del_ejemplo_de_04_731_extrae_todos_los_campos()
    {
        var doc = JsonSerializer.Deserialize<Document>(ExampleJson, OrpJson.Default);

        Assert.NotNull(doc);
        Assert.Equal(1, doc!.Version);
        Assert.NotNull(doc.Meta);
        Assert.Equal(1844, doc.Meta!.JobId);
        Assert.Equal(5678, doc.Meta.OrderId);
        Assert.Equal("pueblo-b", doc.Meta.LocationCode);
        Assert.Equal(1, doc.Meta.Copies);
        Assert.Equal("default", doc.Meta.PrinterRole);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 20, 14, 58, TimeSpan.FromHours(1)), doc.Meta.CreatedAt);
        Assert.Equal(80, doc.Paper!.WidthMm);
        Assert.Equal(9, doc.Blocks.Count);

        var header = Assert.IsType<TextBlock>(doc.Blocks[0]);
        Assert.Equal("PIZZERIA RIO ROSSO", header.Value);
        Assert.Equal(TextAlign.Center, header.Align);
        Assert.True(header.Bold);
        Assert.Equal(TextSize.Double, header.Size);

        Assert.IsType<DividerBlock>(doc.Blocks[2]);

        var kv = Assert.IsType<KvBlock>(doc.Blocks[3]);
        Assert.Equal(4, kv.Items!.Count);
        Assert.Equal("Cliente", kv.Items[1].Label);
        Assert.Equal("María López", kv.Items[1].Value);          // acento
        Assert.Equal("Teléfono", kv.Items[2].Label);
        Assert.Equal("Entrega", kv.Items[3].Label);
        Assert.Equal("Calle Mayor 3", kv.Items[3].Value);

        var items = Assert.IsType<ItemsBlock>(doc.Blocks[4]);
        Assert.Equal(["Cant", "Producto", "Importe"], items.Columns);
        Assert.Equal(["left", "left", "right"], items.Align);
        Assert.Equal(2, items.Rows!.Count);
        Assert.Equal("Margarita grande", items.Rows[0][1]);

        var totals = Assert.IsType<TotalsBlock>(doc.Blocks[5]);
        Assert.Equal(3, totals.Items!.Count);
        Assert.Equal("Envío", totals.Items[1].Label);            // acento
        Assert.True(totals.Items[2].Bold);
        Assert.Equal("TOTAL", totals.Items[2].Label);

        var feed = Assert.IsType<FeedBlock>(doc.Blocks[7]);
        Assert.Equal(3, feed.Lines);

        var cut = Assert.IsType<CutBlock>(doc.Blocks[8]);
        Assert.Equal(CutMode.Partial, cut.Mode);
    }

    [Fact]
    public void Roundtrip_del_ejemplo_es_fiel_incluidos_acentos()
    {
        var doc = JsonSerializer.Deserialize<Document>(ExampleJson, OrpJson.Default);
        var serialized = JsonSerializer.Serialize(doc, OrpJson.Default);

        Assert.True(
            JsonDeepEqualsNormalized(JsonNode.Parse(ExampleJson), JsonNode.Parse(serialized)),
            $"El roundtrip alteró el documento. Serializado: {serialized}");
    }

    [Fact]
    public void El_cadena_de_prueba_obligatoria_sobrevive_el_roundtrip()
    {
        // Regla del proyecto: toda compilación supera esta cadena (Doc/03 §6.5).
        const string chain = "Pizza Margarita · jamón, piña, ñ, €, áéíóú";
        var doc = new Document
        {
            Version = 1,
            Blocks = { new TextBlock { Value = chain, Align = TextAlign.Left } },
        };

        var roundtripped = JsonSerializer.Deserialize<Document>(
            JsonSerializer.Serialize(doc, OrpJson.Default), OrpJson.Default);

        var text = Assert.IsType<TextBlock>(Assert.Single(roundtripped!.Blocks));
        Assert.Equal(chain, text.Value);
    }

    [Fact]
    public void Bloque_desconocido_se_ignora_pero_se_conserva()
    {
        const string json = """
            {
              "version": 1,
              "blocks": [
                { "type": "text", "value": "hola" },
                { "type": "star_wars", "credits": 42 }
              ]
            }
            """;

        var doc = JsonSerializer.Deserialize<Document>(json, OrpJson.Default);
        Assert.Equal(2, doc!.Blocks.Count);

        var unknown = Assert.IsType<UnknownBlock>(doc.Blocks[1]);
        Assert.Equal("star_wars", unknown.TypeName);
        Assert.Equal(42, unknown.Raw.GetProperty("credits").GetInt32());

        // Y el roundtrip lo reemite tal cual, sin romper el documento.
        var serialized = JsonSerializer.Serialize(doc, OrpJson.Default);
        Assert.True(
            JsonDeepEqualsNormalized(JsonNode.Parse(json), JsonNode.Parse(serialized)),
            $"Roundtrip con bloque desconocido alteró el JSON: {serialized}");
    }

    [Fact]
    public void Todos_los_bloques_de_04_732_funden_roundtrip()
    {
        var doc = new Document
        {
            Version = 1,
            Meta = new DocumentMeta { JobId = 1, OrderId = 2, LocationCode = "x", Copies = 2, PrinterRole = "cocina" },
            Paper = new DocumentPaper { WidthMm = 58 },
            Blocks =
            {
                new TextBlock { Value = "áéíóú ñ · €", Align = TextAlign.Right, Bold = true, Size = TextSize.Triple, Invert = true },
                new KvBlock { Items = [new KvItem { Label = "l", Value = "v" }], Separator = ":" },
                new ItemsBlock { Columns = ["c"], Align = ["center"], Rows = [new List<string> { "r1" }] },
                new TotalsBlock { Items = [new TotalItem { Label = "TOTAL", Value = "1", Bold = true }] },
                new DividerBlock { Char = "=" },
                new ImageBlock { Data = "aG9sYQ==", WidthDots = 384 },
                new QrBlock { Value = "https://orvita", Size = 5 },
                new BarcodeBlock { Value = "8412345678903", Symbology = BarcodeSymbology.Ean13 },
                new FeedBlock { Lines = 4 },
                new CutBlock { Mode = CutMode.Full },
                new DrawerBlock { Action = DrawerAction.Open },
                new RawBlock { Data = "aG9sYQ==" },
            },
        };

        var roundtripped = JsonSerializer.Deserialize<Document>(
            JsonSerializer.Serialize(doc, OrpJson.Default), OrpJson.Default);

        Assert.Equal(12, roundtripped!.Blocks.Count);
        var text = Assert.IsType<TextBlock>(roundtripped.Blocks[0]);
        Assert.Equal("áéíóú ñ · €", text.Value);
        Assert.Equal(TextSize.Triple, text.Size);
        Assert.True(text.Invert);
        Assert.Equal(CutMode.Full, Assert.IsType<CutBlock>(roundtripped.Blocks[9]).Mode);
        Assert.Equal(DrawerAction.Open, Assert.IsType<DrawerBlock>(roundtripped.Blocks[10]).Action);
        Assert.Equal(BarcodeSymbology.Ean13, Assert.IsType<BarcodeBlock>(roundtripped.Blocks[7]).Symbology);
        Assert.Equal(58, roundtripped.Paper!.WidthMm);
        Assert.Equal("cocina", roundtripped.Meta!.PrinterRole);
    }

    [Fact]
    public void Version_desconocida_se_lee_tal_cual_para_que_el_poller_marque_failed()
    {
        const string json = """{ "version": 2, "blocks": [] }""";
        var doc = JsonSerializer.Deserialize<Document>(json, OrpJson.Default);
        Assert.Equal(2, doc!.Version); // el agente la rechazará al renderizar (orp_unsupported_document)
    }

    /// <summary>
    /// DeepEquals tolerante con fechas: System.Text.Json escribe DateTimeOffset en
    /// formato "O" (con fracciones), así que normalizamos cualquier cadena que sea una
    /// fecha ISO antes de comparar.
    /// </summary>
    private static bool JsonDeepEqualsNormalized(JsonNode? expected, JsonNode? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        return JsonNode.DeepEquals(NormalizeDates(expected), NormalizeDates(actual));
    }

    private static JsonNode? NormalizeDates(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(kv => new KeyValuePair<string, JsonNode?>(
            kv.Key, NormalizeDates(kv.Value)?.DeepClone()))),
        JsonArray arr => new JsonArray(arr.Select(n => NormalizeDates(n)?.DeepClone()).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var s) && TryDate(s) is { } date =>
            JsonValue.Create(date),
        _ => node,
    };

    private static DateTimeOffset? TryDate(string s) =>
        DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
}
