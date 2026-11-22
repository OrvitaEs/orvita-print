using System.Text;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Printing.Windows;
using Xunit;

namespace Orvita.Print.Printing.Windows.Tests;

/// <summary>
/// Impresión de prueba (tarea F2-05): documento fijo con la cadena obligatoria de Doc/05 §4.3,
/// leyenda «No válido como factura» (Doc/03 §6.7) y renderizado con el renderizador real.
/// </summary>
public sealed class TestTicketTests
{
    [Fact]
    public void Renderiza_con_init_y_cadena_obligatoria_en_cp850()
    {
        var bytes = new EscPosRenderer().Render(TestTicket.Create());

        Assert.Equal(new byte[] { 0x1B, 0x40 }, bytes[..2]); // ESC @
        var cp850 = Encoding.GetEncoding(850);
        // El € no existe en CP850: el renderizador lo sustituye por «E» de forma segura (Doc/05 §4.3).
        AssertContains(bytes, cp850.GetBytes(TestTicket.CadenaPrueba.Replace("€", "E")));
        AssertContains(bytes, cp850.GetBytes("No válido como factura"));
        AssertContains(bytes, cp850.GetBytes("Impresión de prueba"));
        AssertContains(bytes, [0x1D, 0x56, 0x01]); // GS V 1 corte parcial (según EscPosBuilder)
    }

    [Fact]
    public void El_ticket_de_prueba_lleva_rol_y_una_copia()
    {
        var ticket = TestTicket.Create("Mostrador");

        Assert.Equal(1, ticket.Version);
        Assert.Equal(TestTicket.RolPrueba, ticket.Meta!.PrinterRole);
        Assert.Equal(1, ticket.Meta.Copies);
        Assert.Equal(80, ticket.Paper!.WidthMm);
        Assert.Contains(ticket.Blocks, b => b is KvBlock kv && kv.Items!.Any(i => i.Value == "Mostrador"));
    }

    private static void AssertContains(byte[] haystack, byte[] needle) =>
        Assert.True(haystack.AsSpan().IndexOf((ReadOnlySpan<byte>)needle) >= 0,
            $"No se encontró {Encoding.ASCII.GetString(needle)} en el flujo renderizado.");
}
