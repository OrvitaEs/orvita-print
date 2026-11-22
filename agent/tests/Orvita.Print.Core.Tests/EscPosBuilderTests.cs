using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>
/// Bytes exactos de cada comando ESC/POS (Doc/03 §6.5) emitidos por el constructor de bajo nivel.
/// </summary>
public sealed class EscPosBuilderTests
{
    private static byte[] Expected(params byte[] bytes) => bytes;

    [Fact]
    public void Initialize_emite_ESC_arroba()
    {
        Assert.Equal(Expected(0x1B, 0x40), new EscPosBuilder().Initialize().ToArray());
    }

    [Theory]
    [InlineData(TextAlign.Left, new byte[] { 0x1B, 0x61, 0x00 })]
    [InlineData(TextAlign.Center, new byte[] { 0x1B, 0x61, 0x01 })]
    [InlineData(TextAlign.Right, new byte[] { 0x1B, 0x61, 0x02 })]
    public void Align_emite_ESC_a_n(TextAlign align, byte[] expected)
    {
        Assert.Equal(expected, new EscPosBuilder().Align(align).ToArray());
    }

    [Theory]
    [InlineData(true, new byte[] { 0x1B, 0x45, 0x01 })]
    [InlineData(false, new byte[] { 0x1B, 0x45, 0x00 })]
    public void Bold_emite_ESC_E_n(bool on, byte[] expected)
    {
        Assert.Equal(expected, new EscPosBuilder().Bold(on).ToArray());
    }

    [Theory]
    [InlineData(true, new byte[] { 0x1B, 0x2D, 0x01 })]
    [InlineData(false, new byte[] { 0x1B, 0x2D, 0x00 })]
    public void Underline_emite_ESC_menos_n(bool on, byte[] expected)
    {
        Assert.Equal(expected, new EscPosBuilder().Underline(on).ToArray());
    }

    [Theory]
    [InlineData(true, new byte[] { 0x1D, 0x42, 0x01 })]
    [InlineData(false, new byte[] { 0x1D, 0x42, 0x00 })]
    public void Invert_emite_GS_B_n(bool on, byte[] expected)
    {
        Assert.Equal(expected, new EscPosBuilder().Invert(on).ToArray());
    }

    [Theory]
    [InlineData(TextSize.Normal, new byte[] { 0x1D, 0x21, 0x00 })]
    [InlineData(TextSize.Double, new byte[] { 0x1D, 0x21, 0x11 })]
    [InlineData(TextSize.Triple, new byte[] { 0x1D, 0x21, 0x22 })]
    public void Size_emite_GS_bang_n(TextSize size, byte[] expected)
    {
        // GS !: magnificación alto/ancho; ESC ! no puede expresar el tamaño triple.
        Assert.Equal(expected, new EscPosBuilder().Size(size).ToArray());
    }

    [Fact]
    public void SelectPrintMode_emite_ESC_bang_n()
    {
        Assert.Equal(Expected(0x1B, 0x21, 0x08), new EscPosBuilder().SelectPrintMode(0x08).ToArray());
    }

    [Theory]
    [InlineData(CutMode.Partial, new byte[] { 0x1D, 0x56, 0x01 })]
    [InlineData(CutMode.Full, new byte[] { 0x1D, 0x56, 0x00 })]
    public void Cut_emite_GS_V_m(CutMode mode, byte[] expected)
    {
        Assert.Equal(expected, new EscPosBuilder().Cut(mode).ToArray());
    }

    [Fact]
    public void Drawer_emite_ESC_p_pin_0_50ms()
    {
        Assert.Equal(Expected(0x1B, 0x70, 0x00, 0x19, 0xFA), new EscPosBuilder().Drawer().ToArray());
    }

    [Fact]
    public void FeedLines_emite_tantos_0A_como_lineas()
    {
        Assert.Equal(Expected(0x0A, 0x0A, 0x0A), new EscPosBuilder().FeedLines(3).ToArray());
    }

    [Fact]
    public void RasterImage_emite_GS_v_0_con_dimensiones_y_datos()
    {
        // 3 filas de 1 byte (ancho 8 puntos): xL=1, xH=0, yL=3, yH=0.
        var bytes = new EscPosBuilder().RasterImage([0xFF, 0x81, 0xFF], 8).ToArray();
        Assert.Equal(Expected(0x1D, 0x76, 0x30, 0x00, 0x01, 0x00, 0x03, 0x00, 0xFF, 0x81, 0xFF), bytes);
    }

    [Fact]
    public void RasterImage_con_ancho_de_384_usa_dos_bytes_por_fila_en_xL_xH()
    {
        // Ancho 384 → 48 bytes por fila; con 2 filas: xL=48, xH=0, yL=2.
        var bitmap = new byte[96];
        var bytes = new EscPosBuilder().RasterImage(bitmap, 384).ToArray();
        Assert.Equal(Expected(0x1D, 0x76, 0x30, 0x00, 48, 0x00, 0x02, 0x00), bytes[..8]);
    }

    [Fact]
    public void Qr_emite_modelo_tamano_correccion_almacen_e_impresion()
    {
        // Fn 165 (modelo 2) + Fn 167 (módulo 4) + Fn 169 (EC nivel M) + Fn 180 (guardar «A») + Fn 181 (imprimir).
        var bytes = new EscPosBuilder().Qr([0x41], moduleSize: 4).ToArray();
        var expected = new byte[]
        {
            0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00,
            0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, 0x04,
            0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31,
            0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x50, 0x30, 0x41,
            0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30,
        };
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Qr_con_tamano_fuera_de_rango_se_ajusta_a_1_16()
    {
        var clampedLow = new EscPosBuilder().Qr([0x41], moduleSize: 0).ToArray();
        Assert.Equal(0x01, clampedLow[16]); // octeto del tamaño de módulo (tras Fn 165 de 9 bytes + Fn 167 de 7)
        var clampedHigh = new EscPosBuilder().Qr([0x41], moduleSize: 99).ToArray();
        Assert.Equal(16, clampedHigh[16]);
    }

    [Fact]
    public void Barcode_CODE128_emite_GS_h_w_H_y_GS_k_m_73()
    {
        // Altura 50, ancho ×2, HRI debajo, m=73 (CODE128) con los datos y su longitud.
        var bytes = new EscPosBuilder().Barcode([(byte)'{', (byte)'B', (byte)'1'], BarcodeSymbology.Code128).ToArray();
        var expected = new byte[] { 0x1D, 0x68, 0x32, 0x1D, 0x77, 0x02, 0x1D, 0x48, 0x02, 0x1D, 0x6B, 0x49, 0x03, 0x7B, 0x42, 0x31 };
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Barcode_EAN13_emite_GS_k_m_67()
    {
        var digits = "8412345678903"u8.ToArray();
        var bytes = new EscPosBuilder().Barcode(digits, BarcodeSymbology.Ean13).ToArray();
        var expected = new byte[] { 0x1D, 0x68, 0x32, 0x1D, 0x77, 0x02, 0x1D, 0x48, 0x02, 0x1D, 0x6B, 0x43, 0x0D };
        Assert.Equal(expected, bytes[..expected.Length]);
        Assert.Equal(digits, bytes[expected.Length..]);
    }

    [Fact]
    public void Raw_vuelca_los_bytes_tal_cual()
    {
        Assert.Equal(Expected(0x1B, 0x16, 0x01), new EscPosBuilder().Raw([0x1B, 0x16, 0x01]).ToArray());
    }

    [Fact]
    public void Barcode_vacio_o_de_mas_de_255_bytes_no_se_permite_en_el_builder()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EscPosBuilder().Barcode([], BarcodeSymbology.Ean13));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EscPosBuilder().Barcode(new byte[256], BarcodeSymbology.Code128));
    }
}
