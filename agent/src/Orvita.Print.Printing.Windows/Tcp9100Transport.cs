using System.Net.Sockets;
using Orvita.Print.Core;

namespace Orvita.Print.Printing.Windows;

/// <summary>
/// Transporte TCP 9100 (JetDirect/RAW): socket directo a la impresora de red, bytes ESC/POS
/// tal cual (Doc/03 §6.5). Para salidas configuradas como impresora de red con dirección
/// <c>host</c> o <c>host:puerto</c> (puerto por defecto 9100); no pasa por el spooler.
///
/// Copias: mismo criterio que el spooler RAW — el flujo se repite <c>copies</c> veces en la
/// misma conexión. Un corte de red u host inalcanzable es TRANSITORIO (release, Doc/04 §5.7);
/// una dirección malformada es DEFINITIVO (configuración).
/// </summary>
public sealed class Tcp9100Transport : IPrintTransport
{
    /// <summary>Puerto por defecto del protocolo RAW 9100.</summary>
    public const int DefaultPort = 9100;

    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _writeTimeout;

    public Tcp9100Transport(TimeSpan? connectTimeout = null, TimeSpan? writeTimeout = null)
    {
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    public async Task PrintAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var address = ParseAddress(request.Destination);
        var payload = CopiesPayload.Repeat(request.EscPos, request.Copies);

        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_connectTimeout);

            try
            {
                await client.ConnectAsync(address.Host, address.Port, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PrintTransportException(
                    PrintFailureKind.Transient,
                    $"La impresora de red {address} no responde (agotados {_connectTimeout.TotalSeconds:0}s de conexión). " +
                    "Comprueba que está encendida y con IP fija.");
            }
            catch (SocketException ex)
            {
                throw new PrintTransportException(
                    PrintFailureKind.Transient,
                    $"No se pudo conectar con la impresora de red {address}: {ex.Message} (error {ex.SocketErrorCode}).",
                    ex);
            }

            var stream = client.GetStream();
            stream.WriteTimeout = (int)_writeTimeout.TotalMilliseconds;

            try
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                throw new PrintTransportException(
                    PrintFailureKind.Transient,
                    $"La conexión con {address} se cayó durante la impresión: {ex.Message} (error {ex.SocketErrorCode}).",
                    ex);
            }
        }
        finally
        {
            client.Dispose();
        }
    }

    // ------------------------------------------------------------ dirección

    internal readonly record struct PrinterAddress(string Host, int Port)
    {
        public override string ToString() => $"{Host}:{Port}";
    }

    internal static PrinterAddress ParseAddress(DestinationOptions destination)
    {
        var raw = destination.Address?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new PrintTransportException(
                PrintFailureKind.Definitive,
                $"La salida «{destination.Role}» es de red (TCP 9100) pero no tiene dirección configurada (host o host:puerto).");
        }

        var parts = raw.Split(':');
        if (parts.Length > 2 || (parts.Length == 2 && !int.TryParse(parts[1], out _)))
        {
            throw new PrintTransportException(
                PrintFailureKind.Definitive,
                $"Dirección de impresora de red no válida: «{raw}» (formato esperado: host o host:puerto).");
        }

        var port = parts.Length == 2 ? int.Parse(parts[1]) : DefaultPort;
        if (port is < 1 or > 65535 || parts[0].Length == 0)
        {
            throw new PrintTransportException(
                PrintFailureKind.Definitive,
                $"Dirección de impresora de red no válida: «{raw}» (puerto 1–65535, host obligatorio).");
        }

        return new PrinterAddress(parts[0], port);
    }
}
