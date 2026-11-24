using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>
/// Re-escaneo del poller (GUI-15, decisión C): cuando el heartbeat reporta pendientes que el
/// cursor no ve (trabajo reencolado desde el panel conserva su id), el poller re-escanea UNA
/// vez desde after=0 — seguro porque el claim es atómico y los terminales no van en pendientes.
/// </summary>
public sealed class PollerRescanTests
{
    private sealed class FakeTransport : ITransport
    {
        private readonly Queue<Func<Cursor, Task<JobBatch>>> _fetches = new();

        public List<long> FetchedCursors { get; } = [];
        public List<Job> Delivered { get; } = [];

        public void Enqueue(Func<Cursor, Task<JobBatch>> fetch) => _fetches.Enqueue(fetch);

        public void Enqueue(JobBatch batch) => Enqueue(_ => Task.FromResult(batch));

        public Task<JobBatch> FetchAsync(Cursor cursor, CancellationToken ct)
        {
            FetchedCursors.Add(cursor.Value);
            return _fetches.Dequeue()(cursor);
        }

        public Task<Job?> ClaimAsync(long jobId, CancellationToken ct) => Task.FromResult<Job?>(null);

        public Task AckAsync(long jobId, JobResult result, CancellationToken ct) => Task.CompletedTask;
    }

    private static (Func<TimeSpan, CancellationToken, Task> Delay, List<TimeSpan> Waits) RecordingDelay(int stopAfter, CancellationTokenSource cts)
    {
        var waits = new List<TimeSpan>();
        Func<TimeSpan, CancellationToken, Task> delay = (wait, _) =>
        {
            waits.Add(wait);
            if (waits.Count >= stopAfter)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        };
        return (delay, waits);
    }

    private static PollerOptions Options() => new() { BaseInterval = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task Un_trabajo_reencolado_que_el_cursor_no_ve_se_recupera_con_el_re_escaneo()
    {
        // El panel reencoló el trabajo 7 (id viejo); el heartbeat lo cuenta como pendiente
        // pero GET /jobs?after=5 devuelve vacío. El re-escaneo desde 0 lo reclama.
        var transport = new FakeTransport();
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 5 });            // ronda 1: vacío con cursor 5
        transport.Enqueue(new JobBatch { Jobs = [new Job { Id = 7, OrderId = 77 }], Cursor = 7 }); // re-escaneo desde 0: aparece el 7
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 7 });            // ronda 2: vacío, SIN nuevo re-escaneo

        var hint = new ServerPendingHint();
        hint.Report(1); // el servidor reporta 1 pendiente
        var cts = new CancellationTokenSource();
        var (delay, _) = RecordingDelay(stopAfter: 2, cts);
        var logs = new List<string>();
        var poller = new Poller(transport, Options(), log: logs.Add, delayAsync: delay, serverPending: hint);

        await poller.RunAsync((job, _) => { transport.Delivered.Add(job); return Task.CompletedTask; }, cts.Token);

        // La ronda 2 NO re-escanea de nuevo (una vez por reporte del heartbeat).
        Assert.Equal([0, 0, 7], transport.FetchedCursors);
        var job = Assert.Single(transport.Delivered);
        Assert.Equal(7, job.Id);
        Assert.Contains(logs, l => l.Contains("re-escaneo"));
    }

    [Fact]
    public async Task Sin_reporte_de_pendientes_o_sin_hint_no_hay_re_escaneo()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 5 });
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 5 });
        var cts = new CancellationTokenSource();
        var (delay, _) = RecordingDelay(stopAfter: 2, cts);
        var poller = new Poller(transport, Options(), delayAsync: delay, serverPending: new ServerPendingHint()); // nunca reporta

        await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

        Assert.Equal([0, 5], transport.FetchedCursors);
    }

    [Fact]
    public async Task Un_reporte_con_pendientes_en_cero_no_dispara_el_re_escaneo()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 5 });
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 5 });
        var hint = new ServerPendingHint();
        hint.Report(0);
        var cts = new CancellationTokenSource();
        var (delay, _) = RecordingDelay(stopAfter: 2, cts);
        var poller = new Poller(transport, Options(), delayAsync: delay, serverPending: hint);

        await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

        Assert.Equal([0, 5], transport.FetchedCursors);
    }

    [Fact]
    public async Task Un_nuevo_reporte_del_heartbeat_permite_re_escanear_otra_vez()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 5 });  // r1: vacío, cursor 5
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 5 });  // r1 re-escaneo del reporte 1: vacío
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 5 });  // r2: sin reporte nuevo → NO re-escanea
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 5 });  // r3: fetch vacío…
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 5 });  // r3 re-escaneo del reporte 2

        var hint = new ServerPendingHint();
        hint.Report(2);
        var cts = new CancellationTokenSource();
        var waits = 0;
        Func<TimeSpan, CancellationToken, Task> delay = (_, _) =>
        {
            waits++;
            if (waits == 2)
            {
                hint.Report(3); // segundo latido entre rondas
            }

            if (waits >= 3)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        };
        var logs = new List<string>();
        var poller = new Poller(transport, Options(), log: logs.Add, delayAsync: delay, serverPending: hint);

        await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

        // [r1 fetch, r1 re-escaneo, r2 (sin re-escaneo), r3 fetch, r3 re-escaneo]
        Assert.Equal([0, 0, 5, 5, 0], transport.FetchedCursors);
        Assert.Equal(2, logs.Count(l => l.Contains("re-escaneo")));
    }
}
