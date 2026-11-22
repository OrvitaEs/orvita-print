namespace Orvita.Print.Core;

/// <summary>
/// Naturaleza del fallo de impresión; decide la acción del procesador (Doc/04 §5.6–5.7):
/// transitorio → release sin consumir intento; impresora inexistente o documento no
/// soportado → ack failed con su código; definitivo → ack failed (con fallback GDI si venía de RAW).
/// </summary>
public enum PrintFailureKind
{
    /// <summary>Cola/spooler ocupado o red no disponible: reintentable. No consume intento del servidor (release).</summary>
    Transient,

    /// <summary>La impresora no existe en este equipo (nombre cambiado o desinstalada): orp_printer_not_found.</summary>
    PrinterNotFound,

    /// <summary>Documento no soportado (versión futura, datos imposibles): orp_unsupported_document.</summary>
    UnsupportedDocument,

    /// <summary>Error definitivo del transporte (driver, acceso denegado, dirección inválida…).</summary>
    Definitive,
}

/// <summary>
/// Error de impresión con clasificación para el procesador (F2-07). El mensaje es siempre
/// explícito y en español para el panel y los logs (Doc/03 §1, principio 5: fallo visible).
/// </summary>
public class PrintTransportException : Exception
{
    public PrintTransportException(PrintFailureKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public PrintFailureKind Kind { get; }

    /// <summary>Código para el ack (Doc/04 §6: códigos propios del agente; no son errores HTTP).</summary>
    public string ErrorCode => Kind switch
    {
        PrintFailureKind.PrinterNotFound => "orp_printer_not_found",
        PrintFailureKind.UnsupportedDocument => "orp_unsupported_document",
        PrintFailureKind.Transient => "orp_print_transient",
        _ => "orp_print_failed",
    };

    /// <summary>Envuelve un <see cref="Win32Exception"/> (o cualquier excepción con NativeErrorCode) clasificándolo.</summary>
    public static PrintTransportException FromWin32(PrintFailureKind kind, string context, System.ComponentModel.Win32Exception inner) =>
        new(kind, $"{context}: {inner.Message} (error {inner.NativeErrorCode}).", inner);
}
