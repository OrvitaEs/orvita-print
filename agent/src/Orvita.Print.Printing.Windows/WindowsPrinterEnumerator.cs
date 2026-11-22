using System.ComponentModel;
using System.Runtime.InteropServices;
using Orvita.Print.Core;

namespace Orvita.Print.Printing.Windows;

/// <summary>
/// Enumeración de impresoras instaladas (Doc/03 §6.6, tarea F2-06) con <c>EnumPrinters</c>
/// de winspool.drv — decisión documentada: P/Invoke directo en lugar de WMI
/// (<c>Win32_Printer</c> vía System.Management) porque no añade dependencia, es rápido y no
/// sufre los arranques fríos/lentos de WMI en sesión 0, que es donde vive el servicio.
/// Se pide nivel 2 (una sola llamada: nombre exacto, atributos local/red y estado como pista).
///
/// El estado es SOLO una pista (Doc/03 §6.6): nunca bloquea la impresión; las térmicas no
/// devuelven estado fiable.
/// </summary>
public sealed class WindowsPrinterEnumerator : IPrinterEnumerator
{
    /// <inheritdoc />
    public IReadOnlyList<PrinterInfo> List()
    {
        const uint flags = NativeMethods.PrinterEnumLocal | NativeMethods.PrinterEnumConnections;
        const uint level = 2;

        var defaultName = DefaultPrinterName();
        var printers = new List<PrinterInfo>();

        _ = NativeMethods.EnumPrinters(flags, null, level, IntPtr.Zero, 0, out var needed, out var count);
        if (needed == 0)
        {
            return printers;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!NativeMethods.EnumPrinters(flags, null, level, buffer, needed, out _, out var returned))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                throw new PrintTransportException(
                    PrintFailureKind.Transient,
                    $"No se pudieron enumerar las impresoras del equipo: {error.Message} " +
                    "(¿está arrancado el servicio Spooler?).",
                    error);
            }

            // PRINTER_INFO_2 x64: 13 punteros (104 bytes) y luego los DWORD; solo necesitamos
            // pPrinterName (offset 8), Attributes (104) y Status (124).
            const int offsetName = 8;
            const int offsetAttributes = 104;
            const int offsetStatus = 124;
            const int recordSize = 136;

            for (var i = 0; i < returned; i++)
            {
                var record = buffer + (i * recordSize);
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(record, offsetName));
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var attributes = Marshal.ReadInt32(record, offsetAttributes);
                var status = Marshal.ReadInt32(record, offsetStatus);
                printers.Add(new PrinterInfo
                {
                    Name = name,
                    IsDefault = string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase),
                    IsNetwork = (attributes & NativeMethods.PrinterAttributeNetwork) != 0
                                && (attributes & NativeMethods.PrinterAttributeLocal) == 0,
                    StatusHint = DescribeStatus(status, attributes),
                });
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return printers;
    }

    /// <inheritdoc />
    public string? DefaultPrinterName()
    {
        uint size = 0;
        _ = NativeMethods.GetDefaultPrinter(IntPtr.Zero, ref size);
        if (size == 0)
        {
            // ERROR_FILE_NOT_FOUND: no hay impresora predeterminada (equipo sin impresoras).
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)size * sizeof(char));
        try
        {
            return NativeMethods.GetDefaultPrinter(buffer, ref size)
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <inheritdoc />
    public PrinterInfo? Find(string name) =>
        List().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    // ------------------------------------------------------------ pistas de estado

    private static string? DescribeStatus(int status, int attributes)
    {
        if ((attributes & NativeMethods.PrinterAttributeWorkOffline) != 0)
        {
            return "Desconectada (sin conexión con la impresora)";
        }

        if (status == 0)
        {
            return "Lista";
        }

        var hints = new List<string>();
        void Add(int flag, string text)
        {
            if ((status & flag) != 0)
            {
                hints.Add(text);
            }
        }

        Add(0x00000001, "En pausa");
        Add(0x00000002, "Error");
        Add(0x00000004, "Eliminándose");
        Add(0x00000008, "Tapa abierta");
        Add(0x00000010, "Sin papel");
        Add(0x00000020, "Atasco de papel");
        Add(0x00000040, "Desconectada");
        Add(0x00000080, "Sin tóner");
        Add(0x00000100, "No disponible");
        Add(0x00000200, "Esperando");
        Add(0x00000400, "Imprimiendo (recurso ocupado)");
        Add(0x00004000, "Imprimiendo");
        Add(0x00008000, "Inicializando");

        return hints.Count > 0 ? string.Join(", ", hints) : $"Estado {status} (ver spooler)";
    }
}
