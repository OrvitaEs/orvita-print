using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orvita.Print.Contracts;

/// <summary>
/// Bloque del documento neutro (Doc/04 §7.2). El discriminador viaja en la propiedad
/// <c>"type"</c>; cada subclase lo emite con su nombre exacto del contrato.
/// </summary>
public abstract class Block
{
    /// <summary>Nombre del tipo de bloque en el contrato ("text", "kv", …).</summary>
    public abstract string Type { get; }
}

/// <summary>
/// Bloque de un tipo que esta versión del agente no conoce. Se ignora al renderizar y
/// se registra un aviso (Doc/04 §8); su JSON original se conserva íntegro para que el
/// roundtrip no pierda información.
/// </summary>
public sealed class UnknownBlock : Block
{
    public UnknownBlock(string typeName, JsonElement raw)
    {
        TypeName = typeName;
        Raw = raw;
    }

    /// <summary>Nombre tal cual llegó en "type" (puede estar vacío si faltaba).</summary>
    public string TypeName { get; }

    /// <summary>Objeto JSON original del bloque.</summary>
    public JsonElement Raw { get; }

    public override string Type => string.IsNullOrEmpty(TypeName) ? "unknown" : TypeName;
}

/// <summary>Bloque <c>text</c>: una línea o párrafo con alineación, negrita, tamaño e inversión.</summary>
public sealed class TextBlock : Block
{
    public override string Type => "text";

    public string? Value { get; set; }

    public TextAlign? Align { get; set; }

    public bool? Bold { get; set; }

    public TextSize? Size { get; set; }

    public bool? Invert { get; set; }
}

/// <summary>Fila de un bloque <c>kv</c>.</summary>
public sealed class KvItem
{
    public string? Label { get; set; }

    public string? Value { get; set; }
}

/// <summary>Bloque <c>kv</c>: pares etiqueta/valor alineados al ancho del papel.</summary>
public sealed class KvBlock : Block
{
    public override string Type => "kv";

    public List<KvItem>? Items { get; set; }

    public string? Separator { get; set; }
}

/// <summary>Bloque <c>items</c>: tabla de productos (columnas, filas y alineación por columna).</summary>
public sealed class ItemsBlock : Block
{
    public override string Type => "items";

    public List<string>? Columns { get; set; }

    /// <summary>Alineación por columna ("left" | "right" | "center"); el renderizador la interpreta.</summary>
    public List<string>? Align { get; set; }

    public List<List<string>>? Rows { get; set; }
}

/// <summary>Fila de un bloque <c>totals</c>.</summary>
public sealed class TotalItem
{
    public string? Label { get; set; }

    public string? Value { get; set; }

    public bool? Bold { get; set; }
}

/// <summary>Bloque <c>totals</c>: subtotal, envío, total…</summary>
public sealed class TotalsBlock : Block
{
    public override string Type => "totals";

    public List<TotalItem>? Items { get; set; }
}

/// <summary>Bloque <c>divider</c>: línea horizontal; por defecto con "-".</summary>
public sealed class DividerBlock : Block
{
    public override string Type => "divider";

    [JsonPropertyName("char")]
    public string? Char { get; set; }
}

/// <summary>Bloque <c>image</c>: logo raster en base64; máximo 384 px de ancho.</summary>
public sealed class ImageBlock : Block
{
    public override string Type => "image";

    /// <summary>Bytes en base64.</summary>
    public string? Data { get; set; }

    public int? WidthDots { get; set; }
}

/// <summary>Bloque <c>qr</c>.</summary>
public sealed class QrBlock : Block
{
    public override string Type => "qr";

    public string? Value { get; set; }

    public int? Size { get; set; }
}

/// <summary>Bloque <c>barcode</c>.</summary>
public sealed class BarcodeBlock : Block
{
    public override string Type => "barcode";

    public string? Value { get; set; }

    public BarcodeSymbology? Symbology { get; set; }
}

/// <summary>Bloque <c>feed</c>: avance de papel en líneas.</summary>
public sealed class FeedBlock : Block
{
    public override string Type => "feed";

    public int? Lines { get; set; }
}

/// <summary>Bloque <c>cut</c>.</summary>
public sealed class CutBlock : Block
{
    public override string Type => "cut";

    public CutMode? Mode { get; set; }
}

/// <summary>Bloque <c>drawer</c>: apertura del cajón.</summary>
public sealed class DrawerBlock : Block
{
    public override string Type => "drawer";

    public DrawerAction? Action { get; set; }
}

/// <summary>Bloque <c>raw</c>: válvula de escape ESC/POS en base64; solo uso avanzado.</summary>
public sealed class RawBlock : Block
{
    public override string Type => "raw";

    /// <summary>Bytes en base64.</summary>
    public string? Data { get; set; }
}

/// <summary>
/// Conversor polimórfico de <see cref="Block"/> por el discriminador "type". Los tipos
/// desconocidos se leen como <see cref="UnknownBlock"/> conservando el JSON original
/// (Doc/04 §8: se ignoran y se registra un aviso; nunca rompen el trabajo).
/// </summary>
public sealed class BlockConverter : JsonConverter<Block>
{
    public override Block Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Un bloque del documento neutro debe ser un objeto JSON con \"type\".");

        var raw = root.Clone();
        var typeName = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;

        // El discriminador se salta al deserializar el tipo concreto: cada subclase lo
        // expone como propiedad get-only, que System.Text.Json ignora al leer.
        Block? block = typeName switch
        {
            "text" => raw.Deserialize<TextBlock>(options),
            "kv" => raw.Deserialize<KvBlock>(options),
            "items" => raw.Deserialize<ItemsBlock>(options),
            "totals" => raw.Deserialize<TotalsBlock>(options),
            "divider" => raw.Deserialize<DividerBlock>(options),
            "image" => raw.Deserialize<ImageBlock>(options),
            "qr" => raw.Deserialize<QrBlock>(options),
            "barcode" => raw.Deserialize<BarcodeBlock>(options),
            "feed" => raw.Deserialize<FeedBlock>(options),
            "cut" => raw.Deserialize<CutBlock>(options),
            "drawer" => raw.Deserialize<DrawerBlock>(options),
            "raw" => raw.Deserialize<RawBlock>(options),
            _ => new UnknownBlock(typeName ?? string.Empty, raw),
        };

        return block ?? throw new JsonException($"El bloque «{typeName}» no pudo deserializarse.");
    }

    public override void Write(Utf8JsonWriter writer, Block value, JsonSerializerOptions options)
    {
        // Bloque desconocido: reemitir su JSON original tal cual.
        if (value is UnknownBlock unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }

        // Bloque conocido: serializar por su tipo real con opciones SIN el conversor de
        // Block (OrpJson.Inner) para no reentrar aquí (el atributo no está en la subclase).
        // La propiedad get-only Type emite "type": "…".
        JsonSerializer.Serialize(writer, value, value.GetType(), OrpJson.Inner);
    }
}
