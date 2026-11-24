using Orvita.Print.Contracts;

namespace Orvita.Print.Core;

/// <summary>Estado visible del poller (para `orvitaprint status` y el panel; Doc/03 §6.3).</summary>
public enum PollerState
{
    /// <summary>Creado pero no arrancado.</summary>
    Idle,

    /// <summary>Ciclo normal: fetch → procesar → esperar 10 s ± 20 %.</summary>
    Running,

    /// <summary>Error transitorio (red, 429, 5xx): esperando backoff.</summary>
    Backoff,

    /// <summary>Detenido por 401: token inválido o revocado. Requiere reemparejamiento.</summary>
    StoppedUnauthorized,

    /// <summary>Detenido limpiamente por cancelación (parada del servicio).</summary>
    Stopped,
}

/// <summary>Parámetros del poller (Doc/03 §6.3 y ADR-03).</summary>
public sealed class PollerOptions
{
    /// <summary>Intervalo base entre rondas. El servidor puede ajustarlo vía GET /config (poll_interval).</summary>
    public TimeSpan BaseInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Jitter: ± 20 % del intervalo base (ADR-03).</summary>
    public double JitterFraction { get; init; } = 0.20;

    /// <summary>Primer backoff ante error de red: 10 s.</summary>
    public TimeSpan BackoffInitial { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Tope del backoff: 60 s (10 → 20 → 40 → 60 → 60…).</summary>
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Bucle del poller (Doc/03 §6.3): GET /jobs con cursor, entrega de cada trabajo al
/// procesador, espera de 10 s ± 20 % de jitter, backoff exponencial 10→20→40→60 s en
/// error de red, respeto de Retry-After en 429 y parada limpia ante 401 (token
/// inválido/revocado) o cancelación.
/// </summary>
public sealed class Poller
{
    private readonly ITransport _transport;
    private readonly PollerOptions _options;
    private readonly Func<double> _random;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly ServerPendingHint? _serverPending;

    /// <summary>Última generación del hint para la que ya se re-escaneó (una vez por reporte).</summary>
    private int _lastRescanGeneration;

    public Poller(
        ITransport transport,
        PollerOptions? options = null,
        Action<string>? log = null,
        Func<double>? random = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        ServerPendingHint? serverPending = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new PollerOptions();
        Log = log;
        _random = random ?? Random.Shared.NextDouble;
        _delayAsync = delayAsync ?? ((delay, token) => Task.Delay(delay, token));
        _serverPending = serverPending;
    }

    /// <summary>Log opcional (el Service lo conectará a su ILogger; en pruebas, a una lista).</summary>
    public Action<string>? Log { get; set; }

    public PollerState State { get; private set; } = PollerState.Idle;

    /// <summary>
    /// Ejecuta el bucle hasta cancelación o 401. Cada trabajo se entrega a
    /// <paramref name="onJob"/> en orden; una excepción en un trabajo no detiene el
    /// ciclo (el ack de fallo es responsabilidad del procesador, F2-03).
    /// </summary>
    public async Task RunAsync(Func<Job, CancellationToken, Task> onJob, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onJob);

        long cursor = 0;
        var backoff = _options.BackoffInitial;
        State = PollerState.Running;
        LogMessage("Poller iniciado.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    var batch = await _transport.FetchAsync(new Cursor(cursor), cancellationToken).ConfigureAwait(false);
                    if (batch.Cursor > cursor)
                    {
                        cursor = batch.Cursor;
                    }

                    State = PollerState.Running;

                    // Re-escaneo (GUI-15, decisión C): el heartbeat reporta pendientes que el
                    // cursor no ve (p. ej. un trabajo reencolado desde el panel conserva su id
                    // y el cursor solo avanza). Una vez por reporte: fetch extra desde after=0.
                    var jobs = batch.Jobs;
                    if (jobs.Count == 0 && cursor > 0 && ShouldRescan(out var hintGeneration))
                    {
                        LogMessage("re-escaneo: el servidor reporta pendientes que el cursor no ve; re-escaneo desde after=0.");
                        _lastRescanGeneration = hintGeneration;
                        var rescanned = await _transport.FetchAsync(new Cursor(0), cancellationToken).ConfigureAwait(false);
                        if (rescanned.Cursor > cursor)
                        {
                            cursor = rescanned.Cursor;
                        }

                        jobs = rescanned.Jobs;
                    }

                    foreach (var job in jobs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            await onJob(job, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // El fallo de un trabajo no para la cola: el procesador
                            // hará ack(failed) por su cuenta (F2-03).
                            LogMessage($"Error procesando el trabajo {job.Id}: {ex.Message}");
                        }
                    }

                    // Ronda limpia: reset del backoff y espera normal con jitter.
                    backoff = _options.BackoffInitial;
                    wait = ComputeJitteredInterval();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (TransportUnauthorizedException ex)
                {
                    State = PollerState.StoppedUnauthorized;
                    LogMessage($"Poller detenido ({ex.ErrorCode}): token inválido o revocado. Reempareja el agente.");
                    return;
                }
                catch (TransportRateLimitedException ex)
                {
                    wait = ex.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
                        ? retryAfter
                        : backoff;
                    backoff = EscalateBackoff(backoff);
                    State = PollerState.Backoff;
                    LogMessage($"429 orp_rate_limited: el servidor pide esperar {Format(wait)}.");
                }
                catch (TransportNetworkException ex)
                {
                    wait = backoff;
                    backoff = EscalateBackoff(backoff);
                    State = PollerState.Backoff;
                    LogMessage($"Error de red: {ex.Message} Reintento en {Format(wait)}.");
                }
                catch (TransportException ex)
                {
                    wait = backoff;
                    backoff = EscalateBackoff(backoff);
                    State = PollerState.Backoff;
                    LogMessage($"Error del servidor ({ex.StatusCode}): reintento en {Format(wait)}.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Respuesta 200 con cuerpo no contractual (p. ej. un notice de PHP delante
                    // del JSON) o cualquier fallo inesperado de la ronda: NO mata el poller.
                    // Antes la tarea del poller moría aquí para siempre (silencio total); ahora
                    // queda en backoff y reintenta como un error de red (incidente 2026-11-23).
                    wait = backoff;
                    backoff = EscalateBackoff(backoff);
                    State = PollerState.Backoff;
                    LogMessage($"Error inesperado en la ronda del poller: {ex.Message} Reintento en {Format(wait)}.");
                }

                await _delayAsync(wait, cancellationToken).ConfigureAwait(false);
            }

            State = PollerState.Stopped;
            LogMessage("Poller detenido limpiamente.");
        }
        finally
        {
            // Cancelación durante la espera o el fetch: dejar un estado coherente.
            if (State is PollerState.Running or PollerState.Backoff)
            {
                State = PollerState.Stopped;
            }
        }
    }

    private bool ShouldRescan(out int hintGeneration)
    {
        hintGeneration = 0;
        if (_serverPending is not { } hint)
        {
            return false;
        }

        var (pendingCount, generation) = hint.Snapshot();
        hintGeneration = generation;
        return pendingCount > 0 && generation != _lastRescanGeneration;
    }

    private TimeSpan ComputeJitteredInterval()
    {
        var fraction = Math.Clamp(_options.JitterFraction, 0, 1);
        var factor = 1.0 + ((2.0 * _random()) - 1.0) * fraction;
        factor = Math.Clamp(factor, 1.0 - fraction, 1.0 + fraction);
        return TimeSpan.FromMilliseconds(Math.Max(0, _options.BaseInterval.TotalMilliseconds * factor));
    }

    private TimeSpan EscalateBackoff(TimeSpan current)
    {
        var next = current * 2;
        return next > _options.BackoffMax ? _options.BackoffMax : next;
    }

    private void LogMessage(string message) => Log?.Invoke(message);

    private static string Format(TimeSpan span) => $"{span.TotalSeconds:0.#} s";
}
