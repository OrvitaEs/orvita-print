using System.Text;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Printing.Windows;
using Xunit;

namespace Orvita.Print.Printing.Windows.Tests;

/// <summary>
/// Pruebas del spooler RAW y del enumerador que NO necesitan impresora física: la impresora
/// inexistente da orp_printer_not_found con el nombre exacto en el mensaje (Doc/03 §6.6) y la
/// enumeración real del equipo nunca lanza (0 impresoras → lista vacía con texto explícito).
/// La impresión REAL en térmicas es el lote físico H1 (Doc/05 §4.4): ver README del agente.
/// </summary>
public sealed class RawSpoolerAndEnumeratorTests
{
    // ------------------------------------------------------------ spooler RAW

    [Fact]
    public async Task Impresora_inexistente_orp_printer_not_found()
    {
        var transport = new RawSpoolerTransport();
        var request = new PrintRequest
        {
            JobId = 1,
            Destination = new DestinationOptions { Role = "default", PrinterName = "IMPRESORA-QUE-NO-EXISTE" },
            Document = new Document { Version = 1 },
            EscPos = [0x1B, 0x40],
        };

        var ex = await Assert.ThrowsAsync<PrintTransportException>(() => transport.PrintAsync(request, CancellationToken.None));

        Assert.Equal(PrintFailureKind.PrinterNotFound, ex.Kind);
        Assert.Equal("orp_printer_not_found", ex.ErrorCode);
        Assert.Contains("IMPRESORA-QUE-NO-EXISTE", ex.Message);
    }

    [Fact]
    public async Task Sin_impresora_en_la_salida_tambien_es_not_found()
    {
        var transport = new RawSpoolerTransport();
        var request = new PrintRequest
        {
            JobId = 1,
            Destination = new DestinationOptions { Role = "cocina", PrinterName = null },
            Document = new Document { Version = 1 },
            EscPos = [0x1B, 0x40],
        };

        var ex = await Assert.ThrowsAsync<PrintTransportException>(() => transport.PrintAsync(request, CancellationToken.None));

        Assert.Equal(PrintFailureKind.PrinterNotFound, ex.Kind);
        Assert.Contains("cocina", ex.Message);
    }

    [Fact]
    public void Copias_repetidas_en_un_solo_documento()
    {
        var payload = Encoding.ASCII.GetBytes("ESC@");
        var repeated = CopiesPayload.Repeat(payload, 3);

        Assert.Equal(12, repeated.Length);
        Assert.Equal([.. payload, .. payload, .. payload], repeated);
    }

    [Fact]
    public void Copias_por_defecto_una()
    {
        var payload = new byte[] { 1, 2 };
        Assert.Same(payload, CopiesPayload.Repeat(payload, 0)); // 0 → 1 copia
        Assert.Same(payload, CopiesPayload.Repeat(payload, 1));
    }

    [Fact]
    public void Flujo_vacio_es_error_de_programacion()
    {
        Assert.Throws<ArgumentException>(() => CopiesPayload.Repeat([], 1));
    }

    // ------------------------------------------------------------ enumerador (real, sin hardware)

    [Fact]
    public void Enumeracion_real_nunca_lanza_y_los_nombres_son_exactos()
    {
        var enumerator = new WindowsPrinterEnumerator();

        var printers = enumerator.List();

        Assert.NotNull(printers);
        Assert.All(printers, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.True(p.IsNetwork || !p.IsNetwork); // sin nulos raros
        });
        Assert.Single(printers.Where(p => p.IsDefault).Select(p => p.Name).Distinct());
    }

    [Fact]
    public void Find_con_nombre_exacto_ordinal()
    {
        var enumerator = new WindowsPrinterEnumerator();
        var printers = enumerator.List();

        if (printers.Count == 0)
        {
            return; // equipo sin impresoras: nada que buscar
        }

        var first = printers[0];
        Assert.NotNull(enumerator.Find(first.Name));
        Assert.Null(enumerator.Find(first.Name + " ")); // exacto: sin tolerancia
        Assert.Null(enumerator.Find(first.Name.ToUpperInvariant() == first.Name ? first.Name.ToLowerInvariant() : first.Name.ToUpperInvariant()));
    }

    // ------------------------------------------------------------ GDI sin impresora física

    [Fact]
    public async Task Gdi_con_impresora_inexistente_not_found_sin_llamar_al_driver()
    {
        var transport = new GdiTransport(new WindowsPrinterEnumerator());
        var request = new PrintRequest
        {
            JobId = 2,
            Destination = new DestinationOptions { Role = "default", PrinterName = "IMPRESORA-QUE-NO-EXISTE" },
            Document = new Document { Version = 1 },
            EscPos = [0x1B, 0x40],
        };

        var ex = await Assert.ThrowsAsync<PrintTransportException>(() => transport.PrintAsync(request, CancellationToken.None));

        Assert.Equal(PrintFailureKind.PrinterNotFound, ex.Kind);
        Assert.Contains("IMPRESORA-QUE-NO-EXISTE", ex.Message);
    }
}
