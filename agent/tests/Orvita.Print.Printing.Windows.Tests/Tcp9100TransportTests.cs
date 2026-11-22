using System.Net;
using System.Net.Sockets;
using System.Text;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Printing.Windows;
using Xunit;

namespace Orvita.Print.Printing.Windows.Tests;

/// <summary>
/// TCP 9100 sin impresora: un TcpListener local hace de impresora y captura los bytes.
/// Comprueba el flujo ESC/POS completo, las copias repetidas en la misma conexión, el
/// clasificado de errores (dirección mala → definitivo, puerto muerto → transitorio) y que
/// respeta la cancelación.
/// </summary>
public sealed class Tcp9100TransportTests
{
    private static DestinationOptions Destination(string address) => new()
    {
        Role = "default",
        Label = "Cocina",
        PrinterName = "XP-80 (red)",
        Kind = PrintTransportKind.Tcp9100,
        Address = address,
    };

    private static PrintRequest Request(int copies = 1) => new()
    {
        JobId = 7,
        Destination = null!, // se fija en cada prueba
        Document = new Document { Version = 1, Blocks = [] },
        EscPos = [0x1B, 0x40, 0x70, 0x01], // ESC @ + corte
        Copies = copies,
    };

    [Fact]
    public async Task Imprime_los_bytes_en_una_sola_conexion()
    {
        var received = new byte[4];
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var accept = listener.AcceptTcpClientAsync();
        var transport = new Tcp9100Transport();

        var request = Request();
        request = request with { Destination = Destination($"127.0.0.1:{port}") };
        await transport.PrintAsync(request, CancellationToken.None);

        using var client = await accept;
        using var stream = client.GetStream();
        var read = 0;
        while (read < received.Length)
        {
            var n = await stream.ReadAsync(received.AsMemory(read));
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        Assert.Equal(request.EscPos, received[..read]);
    }

    [Fact]
    public async Task Las_copias_se_repiten_en_la_misma_conexion()
    {
        var payload = new byte[] { 0x1B, 0x40, 0x41, 0x0A };
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var accept = listener.AcceptTcpClientAsync();
        var transport = new Tcp9100Transport();

        var request = Request(copies: 3) with { EscPos = payload, Destination = Destination($"localhost:{port}") };
        await transport.PrintAsync(request, CancellationToken.None);

        using var client = await accept;
        using var stream = client.GetStream();
        var received = new byte[payload.Length * 3];
        var read = 0;
        while (read < received.Length)
        {
            var n = await stream.ReadAsync(received.AsMemory(read));
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        Assert.Equal(received.Length, read);
        Assert.Equal([.. payload, .. payload, .. payload], received);
    }

    [Fact]
    public async Task Puerto_muerto_es_fallo_transitorio()
    {
        // Puerto reservado en un socket que ya se cerró: conexión rechazada.
        using var dead = new TcpListener(IPAddress.Loopback, 0);
        dead.Start();
        var port = ((IPEndPoint)dead.LocalEndpoint).Port;
        dead.Stop();

        var transport = new Tcp9100Transport();
        var request = Request();
        request = request with { Destination = Destination($"127.0.0.1:{port}") };

        var ex = await Assert.ThrowsAsync<PrintTransportException>(() => transport.PrintAsync(request, CancellationToken.None));

        Assert.Equal(PrintFailureKind.Transient, ex.Kind);
        Assert.Equal("orp_print_transient", ex.ErrorCode);
    }

    [Fact]
    public async Task Direccion_vacia_o_mala_es_fallo_definitivo()
    {
        var transport = new Tcp9100Transport();

        foreach (var bad in new[] { "", "  ", "host:puerto", "host:0", ":9100" })
        {
            var request = Request();
            request = request with { Destination = Destination(bad) };
            var ex = await Assert.ThrowsAsync<PrintTransportException>(() => transport.PrintAsync(request, CancellationToken.None));
            Assert.Equal(PrintFailureKind.Definitive, ex.Kind);
            Assert.Equal("orp_print_failed", ex.ErrorCode);
        }
    }

    [Fact]
    public async Task Cancelacion_no_es_fallo_de_impresion()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var transport = new Tcp9100Transport();
        var request = Request();
        request = request with { Destination = Destination($"127.0.0.1:{port}") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.PrintAsync(request, cts.Token));
    }
}
