using System.Text.Json.Serialization;

namespace Orvita.Print.Contracts;

/// <summary>Documento neutro v1 (Doc/04 §7.1). La versión se comprueba al recibir; &gt; 1 → trabajo fallido con orp_unsupported_document.</summary>
public sealed class Document
{
    /// <summary>Versión del documento neutro. Solo se conoce la 1.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    public DocumentMeta? Meta { get; set; }

    public DocumentPaper? Paper { get; set; }

    public List<Block> Blocks { get; set; } = [];
}

/// <summary>Metadatos del documento (Doc/04 §7.1). Opcional en el listado de /jobs; completo en el payload real.</summary>
public sealed class DocumentMeta
{
    public long? JobId { get; set; }

    public long? OrderId { get; set; }

    public string? LocationCode { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    public int? Copies { get; set; }

    public string? PrinterRole { get; set; }
}

/// <summary>Papel del documento (Doc/04 §7.1): 58 u 80 mm.</summary>
public sealed class DocumentPaper
{
    public int? WidthMm { get; set; }
}
