using Orvita.Print.Core;

namespace Orvita.Print.Printing.Windows;

/// <summary>
/// Selección de transporte en modo <see cref="PrintTransportKind.Auto"/> (Doc/03 §6.5):
/// spooler RAW primero; si RAW falla con error DEFINITIVO (driver que no acepta RAW u otro
/// error del driver) reintenta con GDI. Los fallos TRANSITORIOS (spooler ocupado, cola) y
/// «impresora no encontrada» NO tienen fallback: el primero se libera con release (el GDI
/// pasaría por el mismo spooler) y el segundo es un dato que hay que corregir, no algo que
/// un segundo transporte arregle.
/// </summary>
public sealed class FallbackPrintTransport : IPrintTransport
{
    private readonly IPrintTransport _primary;
    private readonly IPrintTransport? _fallback;
    private readonly Action<string>? _log;

    public FallbackPrintTransport(IPrintTransport primary, IPrintTransport? fallback, Action<string>? log = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback;
        _log = log;
    }

    /// <inheritdoc />
    public async Task PrintAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _primary.PrintAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (PrintTransportException ex) when (
            ex.Kind == PrintFailureKind.Definitive && _fallback is not null)
        {
            _log?.Invoke(
                $"El transporte principal falló de forma definitiva en «{request.Destination.PrinterName}» ({ex.Message}) " +
                "Reintento con el transporte de respaldo GDI…");

            try
            {
                await _fallback.PrintAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (PrintTransportException gdi)
            {
                throw new PrintTransportException(
                    gdi.Kind,
                    $"RAW falló ({ex.Message}) y el respaldo GDI también ({gdi.Message})",
                    ex);
            }
        }
    }
}
