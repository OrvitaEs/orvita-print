using System.Text.Json;
using Orvita.Print.Core;
using Orvita.Print.Gui.Management;
using Orvita.Print.Gui.ViewModels;
using Xunit;

namespace Orvita.Print.Gui.Tests;

/// <summary>
/// Pestaña «Cola» contra un DOBLE de la pipe (GUI-04/08): auto-refresco solo con la ventana
/// abierta, banner de errores no vistos persistente hasta revisarse, detalle vía queue.item.
/// </summary>
public sealed class QueueViewModelTests
{
    private readonly FakeManagementClient _client = new();

    [Fact]
    public void Sin_ventana_abierta_el_auto_refresco_no_pregunta_por_la_pipe()
    {
        var vm = new QueueViewModel(_client) { WindowVisible = false };

        vm.Refresh();

        Assert.Empty(_client.Requests); // CERO llamadas (Doc/08 §8: CPU ~0 sin ventana)
        Assert.False(vm.ShouldAutoRefresh());
    }

    [Fact]
    public void Con_ventana_abierta_pide_queue_list_y_rellena_las_filas()
    {
        _client.RespondOk(ManagementProtocol.Operations.QueueList, new
        {
            limit = 50,
            total_jobs = 2,
            jobs = new object[]
            {
                new
                {
                    job_id = 1844L,
                    order_id = 778899L,
                    printer_role = "default",
                    copies = 2,
                    state = "done",
                    duplicate_suspected = false,
                    attempts = 1,
                    created_at = "2026-11-24T10:00:00.000Z",
                    updated_at = "2026-11-24T10:00:05.000Z",
                    printed_at = "2026-11-24T10:00:05.000Z",
                },
                new
                {
                    job_id = 1850L,
                    printer_role = "cocina",
                    copies = 1,
                    state = "error",
                    duplicate_suspected = false,
                    attempts = 2,
                    error_code = "orp_print_failed",
                    error_message = "La impresora no responde.",
                    created_at = "2026-11-24T11:00:00.000Z",
                    updated_at = "2026-11-24T11:00:10.000Z",
                },
            },
        });

        var vm = new QueueViewModel(_client) { WindowVisible = true };
        vm.Refresh();

        var op = Assert.Single(_client.Requests);
        Assert.Equal(ManagementProtocol.Operations.QueueList, op.Op);
        Assert.Equal(50, op.Args!.Value.GetProperty("limit").GetInt32());
        Assert.Equal(2, vm.FilteredRows.Count);
        Assert.Equal("Impreso", vm.FilteredRows[0].StateLabel);
        Assert.Equal("Error", vm.FilteredRows[1].StateLabel);
        Assert.Null(vm.LoadError);
    }

    [Fact]
    public void Servicio_caido_da_error_visible_y_no_lanza()
    {
        var vm = new QueueViewModel(_client) { WindowVisible = true };
        _client.ThrowOnNextSend = new ServiceNotRunningException(ManagementProtocol.PipeName);

        vm.Refresh();

        Assert.NotNull(vm.LoadError);
        Assert.Contains("no está en ejecución", vm.LoadError);
    }

    [Fact]
    public void El_banner_de_errores_no_vistos_persiste_hasta_marcarlos_vistos()
    {
        var vm = new QueueViewModel(_client) { WindowVisible = true };
        QueueWithOneError(vm, jobId: 1850);

        vm.Refresh();
        Assert.True(vm.BannerVisible);
        Assert.Contains("1 trabajo con incidencia", vm.BannerText);

        // El banner PERSISTE entre refrescos (Doc/08 §4.2: hasta que el usuario los abra).
        vm.Refresh();
        Assert.True(vm.BannerVisible);

        // Marcar vistos lo quita.
        vm.MarkAllSeenCommand.Execute(null);
        Assert.False(vm.BannerVisible);

        // Y UN refresco posterior no lo trae de vuelta (mismo trabajo, ya visto).
        vm.Refresh();
        Assert.False(vm.BannerVisible);
    }

    [Fact]
    public void Abrir_el_detalle_de_un_error_lo_marca_como_visto()
    {
        var vm = new QueueViewModel(_client) { WindowVisible = true };
        _client.RespondOk(ManagementProtocol.Operations.QueueItem, new
        {
            job = new { job_id = 1850L, state = "error" },
            document_parse_ok = true,
            document_text = "— documento —\nPizza Margarita",
        });
        QueueWithOneError(vm, jobId: 1850);
        vm.Refresh();
        Assert.True(vm.BannerVisible);

        vm.SelectedJob = vm.FilteredRows[0];

        Assert.False(vm.BannerVisible);          // visto
        Assert.Contains("Pizza Margarita", vm.DetailText);   // documento legible
        Assert.Contains("#1850", vm.DetailTitle);
        Assert.Equal(ManagementProtocol.Operations.QueueItem, _client.Requests[1].Op);
    }

    [Fact]
    public void El_filtro_y_la_busqueda_se_aplican_en_cliente()
    {
        _client.RespondOk(ManagementProtocol.Operations.QueueList, new
        {
            limit = 50,
            total_jobs = 3,
            jobs = new object[]
            {
                new { job_id = 1L, order_id = 100L, state = "error", duplicate_suspected = false },
                new { job_id = 2L, order_id = 200L, state = "error", duplicate_suspected = false },
                new { job_id = 3L, order_id = 300L, state = "done", duplicate_suspected = false },
            },
        });

        var vm = new QueueViewModel(_client) { WindowVisible = true, SearchText = "200" };
        vm.Refresh();

        // La búsqueda (200) se aplicó ya al rellenar: una fila.
        var fila = Assert.Single(vm.FilteredRows);
        Assert.Equal(2, fila.JobId);

        vm.SearchText = string.Empty;
        Assert.Equal(3, vm.FilteredRows.Count);

        vm.StateFilter = "error";
        Assert.Equal(2, vm.FilteredRows.Count);

        vm.StateFilter = "done";
        Assert.Single(vm.FilteredRows);
    }

    [Fact]
    public void Respuesta_de_error_del_servicio_queda_visible()
    {
        var vm = new QueueViewModel(_client) { WindowVisible = true };
        _client.RespondError(ManagementProtocol.Operations.QueueList, "explosión", "internal_error");

        vm.Refresh();

        Assert.Contains("explosión", vm.LoadError);
    }

    private void QueueWithOneError(QueueViewModel vm, long jobId)
    {
        _client.RespondOk(ManagementProtocol.Operations.QueueList, new
        {
            limit = 50,
            total_jobs = 1,
            jobs = new object[]
            {
                new
                {
                    job_id = jobId,
                    order_id = 500L,
                    printer_role = "default",
                    copies = 1,
                    state = "error",
                    duplicate_suspected = false,
                    attempts = 1,
                    error_code = "orp_print_failed",
                    error_message = "sin papel",
                    created_at = "2026-11-24T11:00:00.000Z",
                    updated_at = "2026-11-24T11:00:02.000Z",
                },
            },
        });
    }
}
