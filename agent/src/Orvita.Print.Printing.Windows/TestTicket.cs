using Orvita.Print.Contracts;
using Orvita.Print.Core;

namespace Orvita.Print.Printing.Windows;

/// <summary>
/// Documento fijo de la impresión de prueba (<c>orvitaprint test</c>, Doc/03 §9, tarea F2-05):
/// incluye la cadena obligatoria de la matriz H1 (Doc/05 §4.3) y la leyenda de Doc/03 §6.7.
/// Se renderiza con <see cref="EscPosRenderer"/> (la misma vía que un trabajo real) y se envía
/// por el transporte de la salida, así que prueba codificación, ancho, corte y transporte a la vez.
/// </summary>
public static class TestTicket
{
    /// <summary>Cadena obligatoria de verificación de codificación (Doc/05 §4.3).</summary>
    public const string CadenaPrueba = "Pizza Margarita · jamón, piña, ñ, €, áéíóú";

    /// <summary>Rol que identifica la impresión de prueba (no forma parte del contrato).</summary>
    public const string RolPrueba = "test";

    public static Document Create(string? destinationLabel = null)
    {
        return new Document
        {
            Version = 1,
            Meta = new DocumentMeta
            {
                JobId = 0,
                OrderId = 0,
                LocationCode = "test",
                CreatedAt = DateTimeOffset.UtcNow,
                Copies = 1,
                PrinterRole = RolPrueba,
            },
            Paper = new DocumentPaper { WidthMm = 80 },
            Blocks =
            [
                new TextBlock { Value = "ORVITA PRINT", Align = TextAlign.Center, Bold = true, Size = TextSize.Double },
                new TextBlock { Value = "Impresión de prueba", Align = TextAlign.Center },
                new DividerBlock(),
                new KvBlock
                {
                    Items =
                    [
                        new KvItem { Label = "Salida", Value = destinationLabel ?? "prueba" },
                        new KvItem { Label = "Documento", Value = "neutro v1" },
                        new KvItem { Label = "Codificación", Value = "ñ, €, áéíóú" },
                    ],
                },
                new ItemsBlock
                {
                    Columns = ["Cant", "Producto", "Importe"],
                    Align = ["left", "left", "right"],
                    Rows =
                    [
                        ["1", "Margarita grande", "12,00"],
                        ["2", "Barbacoa", "22,00"],
                    ],
                },
                new TotalsBlock
                {
                    Items =
                    [
                        new TotalItem { Label = "Subtotal", Value = "34,00" },
                        new TotalItem { Label = "TOTAL", Value = "36,00", Bold = true },
                    ],
                },
                new DividerBlock(),
                new TextBlock { Value = CadenaPrueba, Align = TextAlign.Center },
                new TextBlock { Value = "Si imprime esto, la salida funciona.", Align = TextAlign.Center },
                new TextBlock { Value = "No válido como factura", Align = TextAlign.Center },
                new FeedBlock { Lines = 3 },
                new CutBlock { Mode = CutMode.Partial },
            ],
        };
    }
}
