using System.ComponentModel;
using System.Runtime.InteropServices;
using Orvita.Print.Core;

namespace Orvita.Print.Printing.Windows;

/// <summary>
/// Transporte principal (ADR-05): spooler de Windows con datatype <c>RAW</c> vía winspool.drv —
/// OpenPrinter → StartDocPrinter(RAW) → StartPagePrinter → WritePrinter → EndPagePrinter →
/// EndDocPrinter → ClosePrinter. Los bytes ESC/POS llegan intactos a la impresora: es la vía
/// que da soporte completo a corte, cajón, QR y raster (Doc/03 §6.5).
///
/// Copias (Doc/03 §6.7): el flujo de bytes se repite <c>copies</c> veces DENTRO del mismo
/// documento de spooler — un único trabajo en la cola, y cada copia empieza con el ESC @
/// que emite el renderizador.
/// </summary>
public sealed class RawSpoolerTransport : IPrintTransport
{
    /// <summary>Tamaño de trozo para WritePrinter (64 KB: un ticket cabe de sobra en uno).</summary>
    public const int ChunkSize = 64 * 1024;

    /// <summary>Nombre del documento en la cola del spooler.</summary>
    public static string DocNameFor(long jobId, string? role) =>
        jobId > 0 ? $"OrvitaPrint trabajo {jobId} ({role ?? "salida"})" : "OrvitaPrint impresión de prueba";

    /// <inheritdoc />
    public Task PrintAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var printerName = request.Destination.PrinterName;
        if (string.IsNullOrWhiteSpace(printerName))
        {
            throw new PrintTransportException(
                PrintFailureKind.PrinterNotFound,
                "La salida «" + request.Destination.Role + "» no tiene impresora configurada.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var payload = CopiesPayload.Repeat(request.EscPos, request.Copies);
        return Task.Run(
            () => SendToSpooler(printerName, DocNameFor(request.JobId, request.Destination.Role), payload, request, cancellationToken),
            cancellationToken);
    }

    private static void SendToSpooler(string printerName, string docName, byte[] payload, PrintRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!NativeMethods.OpenPrinter(printerName, out var printer, default))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            NativeMethods.ThrowSpooler(
                $"No se pudo abrir la impresora «{printerName}» (¿existe exactamente con ese nombre?)",
                error.NativeErrorCode);
        }

        var docStarted = false;
        var pageStarted = false;
        try
        {
            var info = new NativeMethods.DOC_INFO_1
            {
                pDocName = docName,
                pOutputFile = null,
                pDatatype = "RAW",
            };

            if (!NativeMethods.StartDocPrinter(printer, 1, ref info))
            {
                var error = Marshal.GetLastWin32Error();
                NativeMethods.ThrowSpooler(
                    $"El driver de «{printerName}» rechazó el documento RAW", error, dataTypeRelated: true);
            }

            docStarted = true;

            if (!NativeMethods.StartPagePrinter(printer))
            {
                var error = Marshal.GetLastWin32Error();
                NativeMethods.ThrowSpooler($"No se pudo iniciar la página en «{printerName}»", error);
            }

            pageStarted = true;

            for (var offset = 0; offset < payload.Length; offset += ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                var size = Math.Min(ChunkSize, payload.Length - offset);
                if (!NativeMethods.WritePrinter(printer, payload, size, out var written) || written != size)
                {
                    var error = Marshal.GetLastWin32Error();
                    NativeMethods.ThrowSpooler(
                        $"La escritura en «{printerName}» falló (escritos {written} de {size} bytes)", error);
                }
            }

            if (!NativeMethods.EndPagePrinter(printer))
            {
                var error = Marshal.GetLastWin32Error();
                NativeMethods.ThrowSpooler($"No se pudo cerrar la página en «{printerName}»", error);
            }

            pageStarted = false;

            if (!NativeMethods.EndDocPrinter(printer))
            {
                var error = Marshal.GetLastWin32Error();
                NativeMethods.ThrowSpooler($"El spooler no cerró el documento en «{printerName}»", error);
            }

            docStarted = false;
        }
        finally
        {
            // Orden inverso: el spooler exige End antes de Close para que el trabajo quede válido.
            if (pageStarted)
            {
                _ = NativeMethods.EndPagePrinter(printer);
            }

            if (docStarted)
            {
                _ = NativeMethods.EndDocPrinter(printer);
            }

            _ = NativeMethods.ClosePrinter(printer);
        }
    }
}
