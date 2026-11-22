namespace Orvita.Print.Core;

/// <summary>
/// Mapa de bits de 1 bit por píxel listo para el comando GS v 0 del <see cref="EscPosBuilder"/>:
/// filas de <c>ceil(widthDots/8)</c> bytes, bit más significativo primero, 1 = punto negro.
/// </summary>
public sealed class PngRaster
{
    /// <summary>Ancho en puntos (píxeles) de impresión.</summary>
    public required int WidthDots { get; init; }

    /// <summary>Alto en puntos.</summary>
    public required int HeightDots { get; init; }

    /// <summary>Filas compactadas: <c>ceil(WidthDots/8)</c> bytes por fila, MSB primero.</summary>
    public required byte[] Bitmap1Bpp { get; init; }
}

/// <summary>
/// Decodificador de logos PNG (Doc/04 §7.2: el bloque <c>image</c> es PNG base64, decodificado
/// en el agente con .NET puro, sin System.Drawing). La interfaz vive en Core y el procesador
/// (F2-07) usa la implementación para normalizar los logos a raster 1bpp antes de renderizar.
/// Devuelve null (con aviso) ante PNG no soportado: nunca lanza por datos de entrada.
/// </summary>
public interface IPngDecoder
{
    /// <summary>
    /// Decodifica un PNG en base64 y lo reduce a raster 1bpp (umbral de luminancia).
    /// <paramref name="targetWidthDots"/> fija el ancho resultante (vecino más cercano);
    /// null usa el ancho del propio PNG.null si la firma/chunks/filtros no son un PNG soportado.
    /// </summary>
    PngRaster? TryDecodeToRaster(string base64Data, int? targetWidthDots = null, Action<string>? log = null);
}
