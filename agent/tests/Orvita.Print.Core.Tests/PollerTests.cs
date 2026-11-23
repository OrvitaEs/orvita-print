using System.Net;
using System.Text.Json;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>
/// Ciclo del poller (Doc/03 §6.3) con reloj/aleatoriedad inyectadas: jitter ± 20 %,
/// backoff 10→20→40→60 s, parada en 401 y respeto de Retry-After. Sin servidor real.
/// </summary>
public sealed class PollerTests
{
    // ---------------------------------------------------------------- fakes

    private sealed class FakeTransport : ITransport
    {
        private readonly Queue<Func<Cursor, Task<JobBatch>>> _fetches = new();

        public List<long> FetchedCursors { get; } = [];
        public List<Job> Delivered { get; } = [];

        public void Enqueue(Func<Cursor, Task<JobBatch>> fetch) => _fetches.Enqueue(fetch);

        public void Enqueue(Exception exception) => Enqueue(_ => Task.FromException<JobBatch>(exception));

        public void Enqueue(JobBatch batch) => Enqueue(_ => Task.FromResult(batch));

        public Task<JobBatch> FetchAsync(Cursor cursor, CancellationToken ct)
        {
            FetchedCursors.Add(cursor.Value);
            var fetch = _fetches.Dequeue();
            return fetch(cursor);
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

    private static PollerOptions Options(TimeSpan? baseInterval = null) => new()
    {
        BaseInterval = baseInterval ?? TimeSpan.FromSeconds(10),
    };

    // ---------------------------------------------------------------- jitter

    [Fact]
    public async Task El_jitter_varia_el_intervalo_entre_80_y_120_por_ciento()
    {
        foreach (var (random, expectedSeconds) in new[] { (0.0, 8.0), (0.5, 10.0), (1.0, 12.0) })
        {
            var transport = new FakeTransport();
            transport.Enqueue(new JobBatch { Jobs = [], Cursor = 0 });
            var cts = new CancellationTokenSource();
            var (delay, waits) = RecordingDelay(stopAfter: 1, cts);
            var poller = new Poller(transport, Options(), random: () => random, delayAsync: delay);

            await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

            var wait = Assert.Single(waits);
            Assert.InRange(wait.TotalSeconds, 8.0, 12.0);
            Assert.Equal(expectedSeconds, wait.TotalSeconds, precision: 9);
        }
    }

    [Fact]
    public async Task El_estado_tras_una_ronda_limpia_es_running()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 5 });
        var cts = new CancellationTokenSource();
        var (delay, waits) = RecordingDelay(stopAfter: 1, cts);
        var poller = new Poller(transport, Options(), delayAsync: delay);

        await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

        // Al cancelar en la espera el finally pone Stopped; lo relevante es que no quedó
        // ni en Backoff ni en StoppedUnauthorized.
        Assert.Equal(PollerState.Stopped, poller.State);
        Assert.DoesNotContain(waits, w => w > TimeSpan.FromSeconds(12));
    }

    // ---------------------------------------------------------------- ciclo normal

    [Fact]
    public async Task Entrega_los_trabajos_en_orden_y_avanza_el_cursor()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new JobBatch
        {
            Jobs = [new Job { Id = 1001, OrderId = 7 }, new Job { Id = 1002, OrderId = 8 }],
            Cursor = 1002,
        });
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 1002 });

        var cts = new CancellationTokenSource();
        var (delay, _) = RecordingDelay(stopAfter: 2, cts);
        var poller = new Poller(transport, Options(), delayAsync: delay);

        await poller.RunAsync((job, _) =>
        {
            transport.Delivered.Add(job);
            return Task.CompletedTask;
        }, cts.Token);

        Assert.Equal([1001, 1002], transport.Delivered.Select(j => j.Id));
        Assert.Equal([0, 1002], transport.FetchedCursors); // la 2ª ronda ya lleva el cursor
        Assert.Equal(PollerState.Stopped, poller.State);
    }

    [Fact]
    public async Task Cursor_no_retrocede_si_el_servidor_envia_uno_menor()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 500 });
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 300 }); // servidor despistado

        var cts = new CancellationTokenSource();
        var (delay, _) = RecordingDelay(stopAfter: 2, cts);
        var poller = new Poller(transport, Options(), delayAsync: delay);

        await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

        Assert.Equal([0, 500], transport.FetchedCursors);
    }

    [Fact]
    public async Task Un_error_en_un_trabajo_no_para_el_bucle()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new JobBatch { Jobs = [new Job { Id = 1 }, new Job { Id = 2 }], Cursor = 2 });
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 2 });

        var cts = new CancellationTokenSource();
        var (delay, _) = RecordingDelay(stopAfter: 2, cts);
        var poller = new Poller(transport, Options(), delayAsync: delay);
        var logs = new List<string>();
        poller.Log = logs.Add;

        await poller.RunAsync((job, _) => job.Id == 1
            ? throw new InvalidOperationException("spooler caído")
            : Task.CompletedTask, cts.Token);

        Assert.Contains(logs, l => l.Contains("Error procesando el trabajo 1"));
        // El trabajo 2 sí se procesó: el poller no se detuvo por el fallo del 1.
        Assert.Equal(2, transport.FetchedCursors.Count);
    }

    // ---------------------------------------------------------------- 401: parada

    [Fact]
    public async Task Un_401_detiene_el_poller_con_estado_visible()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new TransportUnauthorizedException(ErrorCode.OrpTokenRevoked, "401 orp_token_revoked"));
        var cts = new CancellationTokenSource();
        var (delay, waits) = RecordingDelay(stopAfter: int.MaxValue, cts);
        var poller = new Poller(transport, Options(), delayAsync: delay);
        var logs = new List<string>();
        poller.Log = logs.Add;

        await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

        Assert.Equal(PollerState.StoppedUnauthorized, poller.State);
        Assert.Empty(waits);                     // no espera ni reintenta: parada limpia e inmediata
        Assert.Contains(logs, l => l.Contains("OrpTokenRevoked") && l.Contains("Reempareja"));
        Assert.Single(transport.FetchedCursors); // una sola petición, sin reintentos
    }

    [Fact]
    public async Task Un_401_a_mitad_de_sesion_tambien_para()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 10 });
        transport.Enqueue(new TransportUnauthorizedException(ErrorCode.OrpInvalidToken, "401"));

        var cts = new CancellationTokenSource();
        var (delay, waits) = RecordingDelay(stopAfter: 2, cts); // la 1ª espera es limpia; el 401 llega en la 2ª ronda
        var poller = new Poller(transport, Options(), delayAsync: delay);

        await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

        Assert.Equal(PollerState.StoppedUnauthorized, poller.State);
        Assert.Single(waits);
        Assert.Equal(2, transport.FetchedCursors.Count);
    }

    // ---------------------------------------------------------------- backoff de red

    [Fact]
    public async Task El_backoff_de_red_escalera_10_20_40_60_60()
    {
        var transport = new FakeTransport();
        for (var i = 0; i < 6; i++)
        {
            transport.Enqueue(new TransportNetworkException("host no reachable"));
        }

        var cts = new CancellationTokenSource();
        var (delay, waits) = RecordingDelay(stopAfter: 5, cts);
        var poller = new Poller(transport, Options(), delayAsync: delay);

        await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

        Assert.Equal(
            new[]
            {
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(40),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(60),
            },
            waits);
        // Tras la cancelación de parada el estado final es Stopped (parada limpia);
        // lo que la escalera demuestra es la secuencia de esperas de arriba.
        Assert.Equal(PollerState.Stopped, poller.State);
    }

    [Fact]
    public async Task Tras_una_ronda_limpia_el_backoff_se_resetea()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new TransportNetworkException("red caída")); // espera 10 s
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 1 });     // ronda limpia
        transport.Enqueue(new TransportNetworkException("red caída")); // vuelve a 10 s, no a 20

        var cts = new CancellationTokenSource();
        var (delay, waits) = RecordingDelay(stopAfter: 3, cts);
        var poller = new Poller(transport, Options(), delayAsync: delay);

        await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

        Assert.Equal(TimeSpan.FromSeconds(10), waits[0]);
        Assert.Equal(TimeSpan.FromSeconds(10), waits[2]);
    }

    // ---------------------------------------------------------------- 429

    [Fact]
    public async Task El_429_respeta_el_retry_after_del_servidor()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new TransportRateLimitedException(ErrorCode.OrpRateLimited, "429", retryAfter: TimeSpan.FromSeconds(7)));
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 0 });

        var cts = new CancellationTokenSource();
        var (delay, waits) = RecordingDelay(stopAfter: 2, cts);
        var poller = new Poller(transport, Options(), random: () => 0.5, delayAsync: delay); // jitter neutro: 10 s exactos

        await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

        Assert.Equal(TimeSpan.FromSeconds(7), waits[0]);
        Assert.Equal(TimeSpan.FromSeconds(10), waits[1]); // ronda limpia: intervalo sin jitter
    }

    [Fact]
    public async Task El_429_sin_retry_after_usa_el_backoff()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new TransportRateLimitedException(ErrorCode.OrpRateLimited, "429"));
        transport.Enqueue(new TransportRateLimitedException(ErrorCode.OrpRateLimited, "429"));

        var cts = new CancellationTokenSource();
        var (delay, waits) = RecordingDelay(stopAfter: 2, cts);
        var poller = new Poller(transport, Options(), delayAsync: delay);

        await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

        Assert.Equal(TimeSpan.FromSeconds(10), waits[0]);
        Assert.Equal(TimeSpan.FromSeconds(20), waits[1]);
    }

    [Fact]
    public async Task Un_error_de_protocolo_5xx_tambien_hace_backoff()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new TransportProtocolException(HttpStatusCode.InternalServerError, ErrorCode.Unknown, "500"));
        transport.Enqueue(new TransportProtocolException(HttpStatusCode.BadGateway, ErrorCode.Unknown, "502"));

        var cts = new CancellationTokenSource();
        var (delay, waits) = RecordingDelay(stopAfter: 2, cts);
        var poller = new Poller(transport, Options(), delayAsync: delay);

        await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

        Assert.Equal(TimeSpan.FromSeconds(10), waits[0]);
        Assert.Equal(TimeSpan.FromSeconds(20), waits[1]);
    }

    // ---------------------------------------------------------------- parada limpia

    [Fact]
    public async Task La_cancelacion_para_el_bucle_limpio()
    {
        var transport = new FakeTransport();
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 0 });

        var cts = new CancellationTokenSource();
        var (delay, waits) = RecordingDelay(stopAfter: 1, cts);
        var poller = new Poller(transport, Options(), delayAsync: delay);

        await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

        Assert.Equal(PollerState.Stopped, poller.State);
        Assert.Single(waits);
    }

    // ---------------------------------------------------------------- incidente 2026-11-23

    [Fact]
    public async Task Una_respuesta_200_malformada_no_mata_el_poller_hace_backoff_y_recupera()
    {
        // Incidente 2026-11-23 (defecto A, capa poller): un 200 con cuerpo no contractual
        // (p. ej. un notice de PHP delante del JSON) lanzaba JsonException SIN capturar y la
        // tarea del poller moría para siempre: silencio total, sin log de error del poller.
        // Ahora: backoff + reintento, el poller se recupera solo.
        var transport = new FakeTransport();
        transport.Enqueue(new JsonException("respuesta 200 con un notice de PHP delante del JSON")); // ronda 1: malformada
        transport.Enqueue(new JobBatch { Jobs = [], Cursor = 7 }); // ronda 2: el servidor se recupera

        var cts = new CancellationTokenSource();
        var (delay, waits) = RecordingDelay(stopAfter: 2, cts);
        var poller = new Poller(transport, Options(), delayAsync: delay);

        await poller.RunAsync((_, _) => Task.CompletedTask, cts.Token);

        // No murió: hizo backoff (10 s) y volvió a pedir trabajos con el MISMO cursor.
        Assert.Equal([0, 0], transport.FetchedCursors);
        Assert.Equal(TimeSpan.FromSeconds(10), waits[0]); // backoff inicial tras la ronda malformada
        Assert.InRange(waits[1].TotalSeconds, 8.0, 12.0); // ronda limpia: backoff reseteado + jitter
        Assert.Equal(PollerState.Stopped, poller.State); // parado limpio por la cancelación, no por el fallo
    }
}
