using Orvita.Print.Contracts;

namespace Orvita.Print.Core;

/// <summary>
/// Constructor de bytes ESC/POS de bajo nivel: cada método emite el comando exacto de la referencia
/// ESC/POS (Epson/Xprinter, Doc/03 §6.5). No interpreta el documento neutro: <see cref="EscPosRenderer"/>
/// lo usa para componer el ticket. No es seguro para concurrencia (usar una instancia por renderizado).
/// </summary>
public sealed class EscPosBuilder
{
    private readonly List<byte> _buffer = [];

    /// <summary>Contenido acumulado hasta ahora, como copia.</summary>
    public byte[] ToArray() => [.. _buffer];

    /// <summary>Número de bytes acumulados.</summary>
    public int Length => _buffer.Count;

    /// <summary>ESC @ — inicializa: borra la cola de impresión, resetea modos y posición.</summary>
    public EscPosBuilder Initialize()
    {
        _buffer.Add(0x1B); _buffer.Add(0x40);
        return this;
    }

    /// <summary>ESC a n — alineación: left / center / right.</summary>
    public EscPosBuilder Align(TextAlign align)
    {
        var n = (byte)(align switch
        {
            TextAlign.Left => 0x00,
            TextAlign.Center => 0x01,
            TextAlign.Right => 0x02,
            _ => 0x00,
        });
        _buffer.Add(0x1B); _buffer.Add(0x61); _buffer.Add(n);
        return this;
    }

    /// <summary>ESC E n — negrita.</summary>
    public EscPosBuilder Bold(bool on)
    {
        _buffer.Add(0x1B); _buffer.Add(0x45); _buffer.Add(on ? (byte)0x01 : (byte)0x00);
        return this;
    }

    /// <summary>ESC - n — subrayado (1 punto). No aparece en los bloques del documento neutro v1;
    /// disponible para el modo avanzado y para composiciones propias del transporte.</summary>
    public EscPosBuilder Underline(bool on)
    {
        _buffer.Add(0x1B); _buffer.Add(0x2D); _buffer.Add(on ? (byte)0x01 : (byte)0x00);
        return this;
    }

    /// <summary>GS B n — video invertido (blanco/negro). Algunas impresoras lo ignoran.</summary>
    public EscPosBuilder Invert(bool on)
    {
        _buffer.Add(0x1D); _buffer.Add(0x42); _buffer.Add(on ? (byte)0x01 : (byte)0x00);
        return this;
    }

    /// <summary>
    /// GS ! n — tamaño de carácter por magnificación: normal (1×1), double (2×2), triple (3×3).
    /// Se usa GS ! y no ESC ! porque ESC ! no puede expresar el tamaño triple; ESC ! sigue
    /// disponible vía <see cref="SelectPrintMode"/> para modos combinados.
    /// </summary>
    public EscPosBuilder Size(TextSize size)
    {
        var n = (byte)(size switch
        {
            TextSize.Double => 0x11,
            TextSize.Triple => 0x22,
            TextSize.Normal => 0x00,
            _ => 0x00,
        });
        _buffer.Add(0x1D); _buffer.Add(0x21); _buffer.Add(n);
        return this;
    }

    /// <summary>ESC ! n — modo de impresión combinado (fuente, negrita, doble alto/ancho, subrayado).</summary>
    public EscPosBuilder SelectPrintMode(byte mode)
    {
        _buffer.Add(0x1B); _buffer.Add(0x21); _buffer.Add(mode);
        return this;
    }

    /// <summary>Emite bytes de texto ya codificados y un salto de línea (0x0A).</summary>
    public EscPosBuilder TextLine(ReadOnlySpan<byte> encodedText)
    {
        foreach (var b in encodedText)
        {
            _buffer.Add(b);
        }

        _buffer.Add(0x0A);
        return this;
    }

    /// <summary>Añade bytes tal cual (texto codificado o relleno).</summary>
    public EscPosBuilder Bytes(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            _buffer.Add(b);
        }

        return this;
    }

    /// <summary>Añade <paramref name="count"/> espacios (0x20).</summary>
    public EscPosBuilder Spaces(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _buffer.Add(0x20);
        }

        return this;
    }

    /// <summary>Un salto de línea (0x0A).</summary>
    public EscPosBuilder LineFeed()
    {
        _buffer.Add(0x0A);
        return this;
    }

    /// <summary>Feed de papel: n saltos de línea (0x0A). Equivalente y más universal que ESC d n.</summary>
    public EscPosBuilder FeedLines(int lines)
    {
        for (var i = 0; i < lines; i++)
        {
            _buffer.Add(0x0A);
        }

        return this;
    }

    /// <summary>GS V m — corte: partial = m 1 (corte parcial), full = m 0 (corte total).</summary>
    public EscPosBuilder Cut(CutMode mode)
    {
        _buffer.Add(0x1D); _buffer.Add(0x56);
        _buffer.Add(mode == CutMode.Full ? (byte)0x00 : (byte)0x01);
        return this;
    }

    /// <summary>ESC p m t1 t2 — abre el cajón: pin 0, 50 ms de pulso, 500 ms de pausa (valores de facto).</summary>
    public EscPosBuilder Drawer()
    {
        _buffer.Add(0x1B); _buffer.Add(0x70); _buffer.Add(0x00); _buffer.Add(0x19); _buffer.Add(0xFA);
        return this;
    }

    /// <summary>
    /// GS v 0 m xL xH yL yH data — logo raster monocromo: <paramref name="bitmap"/> es un mapa de
    /// 1 bit por píxel (bit alto = píxel izquierdo, 1 = imprime punto), filas rellenas a byte;
    /// <paramref name="widthDots"/> es el ancho en puntos y el alto se deduce de los datos.
    /// </summary>
    public EscPosBuilder RasterImage(ReadOnlySpan<byte> bitmap, int widthDots)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthDots);
        var rowBytes = (widthDots + 7) / 8;
        var height = bitmap.Length / rowBytes; // filas incompletas descartadas (el renderer avisa)
        ArgumentOutOfRangeException.ThrowIfZero(height);

        _buffer.Add(0x1D); _buffer.Add(0x76); _buffer.Add(0x30); _buffer.Add(0x00);
        _buffer.Add((byte)(rowBytes & 0xFF)); _buffer.Add((byte)((rowBytes >> 8) & 0xFF));
        _buffer.Add((byte)(height & 0xFF)); _buffer.Add((byte)((height >> 8) & 0xFF));
        Bytes(bitmap[..(height * rowBytes)]);
        return this;
    }

    /// <summary>
    /// GS ( k — QR (modelo 2, corrección de errores M, tamaño de módulo 1–16): selección de modelo,
    /// tamaño de módulo, corrección, almacenamiento de datos (Fn 180) e impresión (Fn 181).
    /// </summary>
    public EscPosBuilder Qr(ReadOnlySpan<byte> data, int moduleSize)
    {
        moduleSize = Math.Clamp(moduleSize, 1, 16);

        // Seleccionar modelo 2 (Fn 165): GS ( k 04 00 31 41 32 00
        _buffer.AddRange([0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00]);
        // Tamaño de módulo (Fn 167): GS ( k 03 00 31 43 n
        _buffer.AddRange([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, (byte)moduleSize]);
        // Corrección de errores M (Fn 169): GS ( k 03 00 31 45 31
        _buffer.AddRange([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31]);
        // Almacenar datos (Fn 180): GS ( k pL pH 31 50 30 data — pL/pH = longitud de lo que sigue + 3
        var length = data.Length + 3;
        _buffer.AddRange([0x1D, 0x28, 0x6B, (byte)(length & 0xFF), (byte)((length >> 8) & 0xFF), 0x31, 0x50, 0x30]);
        Bytes(data);
        // Imprimir (Fn 181): GS ( k 03 00 31 51 30
        _buffer.AddRange([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30]);
        return this;
    }

    /// <summary>
    /// GS h/w/H + GS k — código de barras: altura 50 puntos, ancho ×2, texto legible debajo,
    /// y GS k con m 73 (CODE128; los datos deben llevar el prefijo de subconjunto, p. ej. «{B»)
    /// o m 67 (EAN13; 12–13 dígitos). <paramref name="data"/> ya debe ser válido para la simbología.
    /// </summary>
    public EscPosBuilder Barcode(ReadOnlySpan<byte> data, BarcodeSymbology symbology)
    {
        ArgumentOutOfRangeException.ThrowIfZero(data.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, 255);

        _buffer.AddRange([0x1D, 0x68, 0x32]);  // altura 50
        _buffer.AddRange([0x1D, 0x77, 0x02]);  // ancho ×2
        _buffer.AddRange([0x1D, 0x48, 0x02]);  // HRI debajo
        var m = (byte)(symbology switch
        {
            BarcodeSymbology.Code128 => 0x49, // 73
            BarcodeSymbology.Ean13 => 0x43,   // 67
            _ => throw new ArgumentOutOfRangeException(nameof(symbology), symbology, "Simbología no soportada."),
        });
        _buffer.Add(0x1D); _buffer.Add(0x6B); _buffer.Add(m); _buffer.Add((byte)data.Length);
        Bytes(data);
        return this;
    }

    /// <summary>Válvula de escape: bytes ESC/POS en crudo (bloque raw, Doc/04 §7.2).</summary>
    public EscPosBuilder Raw(ReadOnlySpan<byte> bytes) => Bytes(bytes);
}
