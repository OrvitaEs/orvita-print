using Orvita.Print.Core;

namespace Orvita.Print.Printing.Windows;

/// <summary>
/// Fábrica de transportes de impresión de Windows (ADR-12): el Service la inyecta y el resto
/// del agente no conoce winspool.drv, GDI ni sockets. Los transportes son sin estado: se crea
/// uno por solicitud de impresión.
/// </summary>
public sealed class WindowsPrintTransportFactory : IPrintTransportFactory
{
    private readonly Action<string>? _log;

    public WindowsPrintTransportFactory(Action<string>? log = null)
    {
        _log = log;
    }

    /// <inheritdoc />
    public IPrintTransport Create(DestinationOptions destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        return destination.Kind switch
        {
            PrintTransportKind.RawSpooler => new RawSpoolerTransport(),
            PrintTransportKind.Gdi => new GdiTransport(),
            PrintTransportKind.Tcp9100 => new Tcp9100Transport(),
            _ => new FallbackPrintTransport(new RawSpoolerTransport(), new GdiTransport(), _log),
        };
    }
}
