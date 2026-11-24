using Orvita.Print.Gui.Models;
using Xunit;

namespace Orvita.Print.Gui.Tests;

/// <summary>
/// Etiquetas/colores de los 5 estados (Doc/08 §4.2) y filtros de la cola (estado + búsqueda).
/// </summary>
public sealed class QueueModelTests
{
    private static QueueJobRow Row(long jobId, string state, bool duplicate = false, long? orderId = null, string? role = "default") => new()
    {
        JobId = jobId,
        OrderId = orderId,
        State = state,
        DuplicateSuspected = duplicate,
        PrinterRole = role,
        Copies = 1,
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now,
    };

    // --------------------------------------------------------------- etiquetas y colores

    [Theory]
    [InlineData("received", false, "Pendiente")]
    [InlineData("printing", false, "Imprimiéndose")]
    [InlineData("done", false, "Impreso")]
    [InlineData("error", false, "Error")]
    public void Los_cuatro_estados_tienen_etiqueta_en_español(string state, bool duplicate, string expected)
    {
        Assert.Equal(expected, QueueStatusMapper.Label(state, duplicate));
    }

    [Fact]
    public void La_marca_duplicate_suspected_se_muestra_como_reimpreso()
    {
        // Es un BADGE sobre el estado (Doc/08 §4.2): gana sobre el estado subyacente.
        // GUI-16: etiqueta corta para que el badge quepa en la columna Estado al ancho
        // mínimo de ventana; el contexto completo lo dan «Último error» y el detalle.
        Assert.Equal("Reimpreso", QueueStatusMapper.Label("received", duplicate: true));
        Assert.Equal("Reimpreso", QueueStatusMapper.Label("done", duplicate: true));
        // Color ámbar en ambos casos.
        Assert.Equal("#F9A825", QueueStatusMapper.Color("received", duplicate: true));
    }

    [Theory]
    [InlineData("received", "#9E9E9E")] // gris
    [InlineData("printing", "#1565C0")] // azul (spinner)
    [InlineData("done", "#2E7D32")]     // verde
    [InlineData("error", "#C62828")]    // rojo
    public void Colores_por_estado_segun_doc(string state, string expectedColor)
    {
        Assert.Equal(expectedColor, QueueStatusMapper.Color(state, duplicate: false));
    }

    // ------------------------------------------------------------------------- filtros

    [Fact]
    public void Filtro_por_estado_deja_solo_los_de_ese_estado()
    {
        var rows = new[] { Row(1, "received"), Row(2, "done"), Row(3, "error"), Row(4, "received") };

        var pendientes = QueueFilter.Apply(rows, "received", null).ToList();

        Assert.Equal([1, 4], pendientes.Select(r => r.JobId));
    }

    [Fact]
    public void Filtro_reimpreso_trae_solo_los_marcados()
    {
        var rows = new[] { Row(1, "received"), Row(2, "received", duplicate: true) };

        var reimpresos = QueueFilter.Apply(rows, QueueFilter.DuplicateState, null).ToList();

        Assert.Equal([2], reimpresos.Select(r => r.JobId));
    }

    [Fact]
    public void Busqueda_por_numero_de_pedido_con_o_sin_almohadilla()
    {
        var rows = new[] { Row(1, "done", orderId: 778899), Row(2, "done", orderId: 100234) };

        var conAlmohadilla = QueueFilter.Apply(rows, null, "#778899").ToList();
        var sinAlmohadilla = QueueFilter.Apply(rows, null, "778899").ToList();

        Assert.Equal([1], conAlmohadilla.Select(r => r.JobId));
        Assert.Equal([1], sinAlmohadilla.Select(r => r.JobId));
    }

    [Fact]
    public void Busqueda_por_job_id_tambien_funciona()
    {
        var rows = new[] { Row(1844, "error"), Row(1850, "done") };

        var encontrados = QueueFilter.Apply(rows, null, "184").ToList();

        Assert.Equal([1844], encontrados.Select(r => r.JobId));
    }

    [Fact]
    public void Estado_y_busqueda_se_combinan()
    {
        var rows = new[] { Row(1, "error", orderId: 100), Row(2, "error", orderId: 200), Row(3, "done", orderId: 300) };

        var resultados = QueueFilter.Apply(rows, "error", "100").ToList();

        Assert.Equal([1], resultados.Select(r => r.JobId));
    }

    [Fact]
    public void Sin_filtros_vuelven_todas()
    {
        var rows = new[] { Row(1, "received"), Row(2, "done") };

        Assert.Equal(2, QueueFilter.Apply(rows, QueueFilter.AllStates, "  ").Count());
    }
}
