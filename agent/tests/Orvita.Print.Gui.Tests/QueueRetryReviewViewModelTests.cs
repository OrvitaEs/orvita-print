using System.Text.Json;
using Orvita.Print.Core;
using Orvita.Print.Gui.Management;
using Orvita.Print.Gui.Models;
using Orvita.Print.Gui.ViewModels;
using Xunit;

namespace Orvita.Print.Gui.Tests;

/// <summary>
/// Acciones de fila de la cola (GUI-15): «Reimprimir» (op queue.retry, con confirmación en
/// los done) y «Marcar como revisada» (op queue.review). El banner cuenta solo errores NO
/// revisados; un re-fallo tras reimprimir rearma el banner y dispara el balón.
/// </summary>
public sealed class QueueRetryReviewViewModelTests
{
    private readonly FakeManagementClient _client = new();

    private static object WireJob(long jobId, string state, object? extra = null) =>
        WireJobDictionary(jobId, state, extra);

    private static object WireJobDictionary(long jobId, string state, object? extra)
    {
        var dict = new Dictionary<string, object?>
        {
            ["job_id"] = jobId,
            ["printer_role"] = "default",
            ["copies"] = 1,
            ["state"] = state,
            ["duplicate_suspected"] = false,
            ["attempts"] = 1,
            ["created_at"] = "2026-11-25T10:00:00.000Z",
            ["updated_at"] = "2026-11-25T10:00:05.000Z",
        };
        if (extra is IEnumerable<KeyValuePair<string, object?>> extras)
        {
            foreach (var (key, value) in extras)
            {
                dict[key] = value;
            }
        }

        return dict;
    }

    private QueueViewModel NewVm()
    {
        var vm = new QueueViewModel(_client) { WindowVisible = true };
        vm.Refresh();
        return vm;
    }

    private void RespondQueueList(params object[] jobs) =>
        _client.RespondOk(ManagementProtocol.Operations.QueueList, new { limit = 50, total_jobs = jobs.Length, jobs });

    // ------------------------------------------------------------- botones según estado

    [Fact]
    public void Los_botones_aparecen_segun_el_estado_de_la_fila()
    {
        Assert.True(new QueueJobRow { State = "error" }.ShowRetry);
        Assert.True(new QueueJobRow { State = "error" }.ShowReview);
        Assert.True(new QueueJobRow { State = "done" }.ShowRetry);
        Assert.False(new QueueJobRow { State = "done" }.ShowReview);
        Assert.False(new QueueJobRow { State = "received" }.ShowRetry);
        Assert.False(new QueueJobRow { State = "printing" }.ShowRetry);

        // Un error ya revisado no ofrece «Marcar como revisada» otra vez.
        Assert.False(new QueueJobRow { State = "error", ReviewedAt = DateTimeOffset.Now }.ShowReview);
    }

    [Fact]
    public void La_marca_revisada_cambia_el_badge_a_error_revisado()
    {
        var fila = new QueueJobRow { State = "error" };
        Assert.Equal("Error", fila.StateLabel);
        Assert.Equal("#C62828", fila.StateColor);

        var revisada = new QueueJobRow { State = "error", ReviewedAt = DateTimeOffset.Now };
        Assert.Equal("Error · revisado", revisada.StateLabel);
        Assert.Equal("#9A6A6E", revisada.StateColor);
    }

    // ------------------------------------------------------------------- reimprimir

    [Fact]
    public async Task Reimprimir_pide_queue_retry_y_pinta_el_estado_final()
    {
        RespondQueueList(WireJob(1850, "error", new Dictionary<string, object?>
        {
            ["error_code"] = "orp_printer_not_found",
            ["error_message"] = "La impresora no existe.",
        }));
        var vm = NewVm();
        _client.RespondOk(ManagementProtocol.Operations.QueueRetry, WireJob(1850, "done", new Dictionary<string, object?>
        {
            ["printed_at"] = "2026-11-25T10:01:00.000Z",
        }));

        await vm.RequestRetryAsync(vm.FilteredRows.Single(r => r.JobId == 1850));

        var op = _client.Requests.Single(r => r.Op == ManagementProtocol.Operations.QueueRetry);
        Assert.Equal(1850, op.Args!.Value.GetProperty("job_id").GetInt64());
        var fila = vm.FilteredRows.Single(r => r.JobId == 1850);
        Assert.Equal("done", fila.State);
        Assert.Equal("Impreso", fila.StateLabel);
        Assert.False(vm.RetryInProgress);
    }

    [Fact]
    public async Task Reimprimir_que_vuelve_a_fallar_reama_el_banner_y_notifica()
    {
        RespondQueueList(WireJob(1850, "error", new Dictionary<string, object?> { ["error_message"] = "sin papel" }));
        var vm = NewVm();
        vm.SelectedJob = vm.FilteredRows[0]; // el usuario la abre: pasa a «vista»
        Assert.False(vm.BannerVisible);

        var balones = new List<TrayBalloon>();
        vm.BalloonRequested += balones.Add;
        _client.RespondOk(ManagementProtocol.Operations.QueueRetry, WireJob(1850, "error", new Dictionary<string, object?>
        {
            ["error_message"] = "sigue sin papel",
        }));

        await vm.RequestRetryAsync(vm.FilteredRows[0]);

        Assert.True(vm.BannerVisible);              // banner otra vez (GUI-15)
        Assert.Contains("1 trabajo con incidencia", vm.BannerText);
        var balon = Assert.Single(balones);
        Assert.True(balon.Error);
    }

    [Fact]
    public async Task Reimprimir_un_done_exige_confirmacion_y_respetar_el_no()
    {
        RespondQueueList(WireJob(1900, "done"));
        var vm = NewVm();
        var pedidos = new List<string>();
        vm.ConfirmReprintDone = message =>
        {
            pedidos.Add(message);
            return Task.FromResult(false); // el usuario se arrepiente
        };

        await vm.RequestRetryAsync(vm.FilteredRows[0]);

        var mensaje = Assert.Single(pedidos);
        Assert.Contains("OTRA copia", mensaje);
        Assert.DoesNotContain(_client.Requests, r => r.Op == ManagementProtocol.Operations.QueueRetry);

        vm.ConfirmReprintDone = _ => Task.FromResult(true);
        _client.RespondOk(ManagementProtocol.Operations.QueueRetry, WireJob(1900, "done"));
        await vm.RequestRetryAsync(vm.FilteredRows[0]);
        Assert.Contains(_client.Requests, r => r.Op == ManagementProtocol.Operations.QueueRetry);
    }

    [Fact]
    public async Task Reimprimir_con_el_servicio_caido_da_error_visible_sin_lanzar()
    {
        RespondQueueList(WireJob(1850, "error"));
        var vm = NewVm();
        _client.ThrowOnNextSend = new ServiceNotRunningException(ManagementProtocol.PipeName);

        await vm.RequestRetryAsync(vm.FilteredRows[0]);

        Assert.Contains("no se pudo reimprimir", vm.LoadError);
        Assert.False(vm.RetryInProgress);
        Assert.Equal("error", vm.FilteredRows.Single(r => r.JobId == 1850).State);
    }

    // -------------------------------------------------------------- marcar como revisada

    [Fact]
    public async Task Marcar_como_revisada_pide_queue_review_y_saca_la_fila_del_banner()
    {
        RespondQueueList(WireJob(1850, "error", new Dictionary<string, object?> { ["error_message"] = "sin papel" }));
        var vm = NewVm();
        Assert.True(vm.BannerVisible);

        _client.RespondOk(ManagementProtocol.Operations.QueueReview, WireJob(1850, "error", new Dictionary<string, object?>
        {
            ["error_message"] = "sin papel",
            ["reviewed_at"] = "2026-11-25T10:02:00.000Z",
        }));

        await vm.RequestReviewAsync(vm.FilteredRows[0]);

        var op = _client.Requests.Single(r => r.Op == ManagementProtocol.Operations.QueueReview);
        Assert.Equal(1850, op.Args!.Value.GetProperty("job_id").GetInt64());
        Assert.True(op.Args!.Value.GetProperty("reviewed").GetBoolean());

        var fila = vm.FilteredRows.Single(r => r.JobId == 1850);
        Assert.Equal("Error · revisado", fila.StateLabel);
        Assert.False(vm.BannerVisible);  // sale del contador del banner (decisión D)
        Assert.False(vm.HasUnseenIncidents);
    }

    [Fact]
    public void El_banner_cuenta_solo_errores_no_revisados()
    {
        RespondQueueList(
            WireJob(1, "error"),                                                                    // sin revisar: cuenta
            WireJob(2, "error", new Dictionary<string, object?> { ["reviewed_at"] = "2026-11-25T10:00:00.000Z" }), // revisado: NO
            WireJob(3, "done", new Dictionary<string, object?> { ["duplicate_suspected"] = true })); // reimpreso tras corte: cuenta
        var vm = NewVm();

        Assert.True(vm.BannerVisible);
        Assert.Contains("Hay 2 trabajos", vm.BannerText);
        Assert.False(vm.FilteredRows.Single(r => r.JobId == 2).ShowReview); // revisada: ya no ofrece el botón
    }

    [Fact]
    public async Task Marcar_como_revisada_no_necesita_confirmacion()
    {
        RespondQueueList(WireJob(1850, "error"));
        var vm = NewVm();
        _client.RespondOk(ManagementProtocol.Operations.QueueReview, WireJob(1850, "error", new Dictionary<string, object?>
        {
            ["reviewed_at"] = "2026-11-25T10:02:00.000Z",
        }));

        // Sin ConfirmReprintDone inyectado: la revisión no consulta nada y funciona igual.
        await vm.RequestReviewAsync(vm.FilteredRows[0]);

        Assert.Equal("Error · revisado", vm.FilteredRows[0].StateLabel);
    }
}
