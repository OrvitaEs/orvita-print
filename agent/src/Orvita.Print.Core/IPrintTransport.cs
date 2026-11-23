using Orvita.Print.Contracts;

namespace Orvita.Print.Core;

/// <summary>
/// Vía de impresión de una salida (Doc/03 §6.5). <see cref="PrintTransportKind.Auto"/>
/// es el comportamiento por defecto: spooler RAW primero y fallback GDI si el driver lo rechaza;
/// TCP 9100 solo cuando la salida está configurada como impresora de red.
/// </summary>
public enum PrintTransportKind
{
    /// <summary>RAW con fallback GDI (por defecto).</summary>
    Auto,

    /// <summary>Spooler de Windows con datatype RAW (winspool.drv, ADR-05).</summary>
    RawSpooler,

    /// <summary>Spooler de Windows vía GDI (System.Drawing.Printing). Solo vive en Printing.Windows.</summary>
    Gdi,

    /// <summary>Socket directo al puerto 9100 de una impresora de red.</summary>
    Tcp9100,

    /// <summary>
    /// Fichero PDF vía «Microsoft Print to PDF», sin diálogos (pruebas de integración y
    /// demos). La implementación vive en Printing.Windows; <see cref="DestinationOptions.Address"/>
    /// es el DIRECTORIO donde escribir los PDFs.
    /// </summary>
    Pdf,
}

/// <summary>
/// Opciones de una salida física del agente (Doc/04 §2.5 y Doc/03 §6.7). En V1 vive en la
/// configuración del servicio; F2-10 la persistirá en SQLite vía la pipe de gestión.
/// <see cref="PrinterName"/> es el nombre EXACTO de Windows (Doc/03 §6.6): se guarda tal cual.
/// </summary>
public sealed class DestinationOptions
{
    /// <summary>Identificador estable de la salida (<c>default</c>, <c>cocina</c>, <c>cliente</c>…).</summary>
    public string Role { get; set; } = "default";

    /// <summary>Nombre visible («Mostrador», «Cocina»…).</summary>
    public string? Label { get; set; }

    /// <summary>Nombre exacto de la impresora en Windows. Para TCP 9100, opcional (usa <see cref="Address"/>).</summary>
    public string? PrinterName { get; set; }

    /// <summary>Copias por defecto de la salida. El trabajo trae las suyas congeladas (Doc/04 §2.4).</summary>
    public int Copies { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public PrintTransportKind Kind { get; set; } = PrintTransportKind.Auto;

    /// <summary>Host o host:puerto para <see cref="PrintTransportKind.Tcp9100"/> (puerto por defecto 9100).</summary>
    /// <para>Para <see cref="PrintTransportKind.Pdf"/>: DIRECTORIO donde escribir los PDFs (opcional).</para>
    public string? Address { get; set; }
}

/// <summary>
/// Solicitud de impresión para un trabajo ya reclamado. El transporte elige lo que necesita:
/// RAW y TCP 9100 envían <see cref="EscPos"/> tal cual; GDI pinta <see cref="Document"/> (no
/// interpreta ESC/POS). <see cref="Copies"/> es el multiplicador congelado del trabajo (Doc/04 §2.4/§2.8):
/// el transporte repite el flujo — nunca el renderizador (Doc/03 §6.7).
/// </summary>
public sealed record PrintRequest
{
    /// <summary>Job id del servidor (0 en la impresión de prueba).</summary>
    public long JobId { get; init; }

    /// <summary>Salida física destino.</summary>
    public required DestinationOptions Destination { get; init; }

    /// <summary>Documento neutro v1 ya normalizado (logos en raster 1bpp).</summary>
    public required Document Document { get; init; }

    /// <summary>Bytes ESC/POS renderizados con <see cref="EscPosRenderer"/>.</summary>
    public required byte[] EscPos { get; init; }

    /// <summary>Veces que se imprime el documento (≥ 1), del trabajo o de la impresión de prueba.</summary>
    public int Copies { get; init; } = 1;
}

/// <summary>
/// Transporte de impresión (ADR-12): la interfaz vive en Core para que el procesador de
/// trabajos no dependa de Windows; las implementaciones (spooler RAW, GDI, TCP 9100) viven
/// en <c>Orvita.Print.Printing.Windows</c> y se inyectan en el arranque del Service.
/// </summary>
public interface IPrintTransport
{
    /// <summary>
    /// Imprime la solicitud completa (con sus copias). Lanza <see cref="PrintTransportException"/>
    /// ante fallo: transitorio (release, Doc/04 §5.7), impresora no encontrada, documento no
    /// soportado o fallo definitivo.
    /// </summary>
    Task PrintAsync(PrintRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Fábrica de transportes por salida. La implementa Printing.Windows (la única pieza que
/// conoce los transportes concretos); el Service la inyecta en el arranque (ADR-12).
/// </summary>
public interface IPrintTransportFactory
{
    /// <summary>Transporte para la salida dada; nunca null (los fallos se manifiestan al imprimir).</summary>
    IPrintTransport Create(DestinationOptions destination);
}
