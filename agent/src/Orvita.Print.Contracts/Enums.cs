using System.Text.Json.Serialization;

namespace Orvita.Print.Contracts;

/// <summary>Alineación de un bloque de texto (Doc/04 §7.2).</summary>
public enum TextAlign
{
    [JsonStringEnumMemberName("left")]
    Left,

    [JsonStringEnumMemberName("center")]
    Center,

    [JsonStringEnumMemberName("right")]
    Right,
}

/// <summary>Tamaño de texto (Doc/04 §7.2): normal, doble o triple.</summary>
public enum TextSize
{
    [JsonStringEnumMemberName("normal")]
    Normal,

    [JsonStringEnumMemberName("double")]
    Double,

    [JsonStringEnumMemberName("triple")]
    Triple,
}

/// <summary>Modo de corte (Doc/04 §7.2).</summary>
public enum CutMode
{
    [JsonStringEnumMemberName("partial")]
    Partial,

    [JsonStringEnumMemberName("full")]
    Full,
}

/// <summary>Acción del cajón portamonedas (Doc/04 §7.2). En v1 solo existe "open".</summary>
public enum DrawerAction
{
    [JsonStringEnumMemberName("open")]
    Open,
}

/// <summary>Simbología del código de barras (Doc/04 §7.2).</summary>
public enum BarcodeSymbology
{
    [JsonStringEnumMemberName("CODE128")]
    Code128,

    [JsonStringEnumMemberName("EAN13")]
    Ean13,
}
