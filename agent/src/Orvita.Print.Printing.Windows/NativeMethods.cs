using System.ComponentModel;
using System.Runtime.InteropServices;
using Orvita.Print.Core;

namespace Orvita.Print.Printing.Windows;

/// <summary>
/// Firma P/Invoke de winspool.drv (ADR-12: solo este proyecto toca APIs de Windows).
/// Solo lo que se usa: OpenPrinter/ClosePrinter, documento RAW (StartDocPrinter con
/// datatype "RAW", WritePrinter, EndDocPrinter), EnumPrinters y GetDefaultPrinter.
/// Se usa DllImport clásico (no LibraryImport) para no introducir código unsafe generado.
/// </summary>
internal static class NativeMethods
{
    // ------------------------------------------------------------ spooler RAW

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DOC_INFO_1
    {
        public string? pDocName;
        public string? pOutputFile;
        public string? pDatatype;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenPrinter(string printerName, out IntPtr printer, IntPtr @default);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClosePrinter(IntPtr printer);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartDocPrinter(IntPtr printer, int level, ref DOC_INFO_1 docInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndDocPrinter(IntPtr printer);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartPagePrinter(IntPtr printer);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndPagePrinter(IntPtr printer);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WritePrinter(IntPtr printer, byte[] buffer, int bytesToWrite, out int bytesWritten);

    // ------------------------------------------------------------ enumeración

    internal const uint PrinterEnumLocal = 0x00000002;
    internal const uint PrinterEnumConnections = 0x00000004;

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumPrinters(
        uint flags,
        string? name,
        uint level,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesNeeded,
        out uint printerCount);

    [DllImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDefaultPrinter(IntPtr buffer, ref uint bufferSize);

    // ------------------------------------------------------------ códigos Win32 usados

    internal const int ErrorAccessDenied = 5;
    internal const int ErrorNotReady = 21;
    internal const int ErrorBusy = 170;
    internal const int ErrorServiceNotActive = 1062;
    internal const int ErrorRetry = 1237;
    internal const int ErrorTimeout = 1460;
    internal const int RpcServerUnavailable = 1722;
    internal const int RpcCallFailed = 1726;
    internal const int ErrorInvalidPrinterName = 1801;
    internal const int ErrorInvalidPrinterCommand = 1803;
    internal const int ErrorInvalidDatatype = 1804;

    // Atributos de PRINTER_INFO_2
    internal const int PrinterAttributeLocal = 0x00000004;
    internal const int PrinterAttributeNetwork = 0x00000010;
    internal const int PrinterAttributeWorkOffline = 0x00000400;

    /// <summary>Errores que indican spooler/servicio ocupado o no disponible aún: fallo TRANSITORIO (release, Doc/04 §5.7).</summary>
    internal static bool IsTransientWin32(int error) => error is
        ErrorNotReady or ErrorBusy or ErrorServiceNotActive or ErrorRetry or ErrorTimeout
        or RpcServerUnavailable or RpcCallFailed;

    /// <summary>Clasifica un error Win32 del spooler en el tipo de fallo de impresión.</summary>
    internal static PrintFailureKind ClassifySpoolerError(int error) =>
        error switch
        {
            ErrorInvalidPrinterName => PrintFailureKind.PrinterNotFound,
            // El driver/print processor rechaza el datatype RAW (o el comando): es el caso
            // del fallback GDI (Doc/03 §6.5).
            ErrorInvalidDatatype or ErrorInvalidPrinterCommand => PrintFailureKind.Definitive,
            ErrorAccessDenied => PrintFailureKind.Definitive,
            _ when IsTransientWin32(error) => PrintFailureKind.Transient,
            // Ante un error desconocido asumimos definitivo: con GDI como siguiente intento
            // (fallback Auto) la impresión aún tiene salida; nunca un bucle infinito de releases.
            _ => PrintFailureKind.Definitive,
        };

    /// <summary>Lanza <see cref="PrintTransportException"/> con el contexto y el error Win32 dado.</summary>
    internal static void ThrowSpooler(string context, int error, bool dataTypeRelated = false)
    {
        var kind = ClassifySpoolerError(error);
        var detail = dataTypeRelated ? " (datatype RAW rechazado)" : string.Empty;
        throw new PrintTransportException(
            kind,
            $"{context}: {new Win32Exception(error).Message}{detail} (error {error}).",
            new Win32Exception(error));
    }
}
