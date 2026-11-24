using System.Net;
using System.Text;
using Orvita.Print.Contracts;
using Orvita.Print.Core;

namespace Orvita.Print.Service.Tests;

/// <summary>Servidor falso (ITransport + release) para probar el pipeline sin WordPress.</summary>
public sealed class FakeServerTransport : ITransport, IJobReleaseTransport
{
    private readonly Dictionary<long, Job> _claimable = [];

    /// <summary>Trabajos que devuelve FetchAsync (carcasa sin documento, como el contrato §5.4).</summary>
    public Queue<Job> PendingFetch { get; } = new();

    /// <summary>Respuesta completa del claim por id (null → 409/410/404 simulado).</summary>
    public Dictionary<long, Job> ClaimResponses { get; set; } = [];

    public List<JobResult> Acks { get; } = [];

    /// <summary>Acks detallados con respuesta (GUI-15): el último comportamiento registrado gana.</summary>
    public List<(long JobId, JobResult Result)> DetailedAcks { get; } = [];

    /// <summary>Respuesta que AckDetailedAsync devuelve (null → el transporte no la lee).</summary>
    public DocumentAck? NextAckResponse { get; set; }

    /// <summary>Excepción que AckDetailedAsync lanza en la próxima llamada (p. ej. red caída).</summary>
    public Exception? ThrowOnNextAck { get; set; }

    public List<(long JobId, string Reason)> Releases { get; } = [];

    public int FetchCalls { get; private set; }

    public Task<JobBatch> FetchAsync(Cursor cursor, CancellationToken ct)
    {
        FetchCalls++;
        var jobs = new List<Job>();
        while (PendingFetch.Count > 0)
        {
            var job = PendingFetch.Dequeue();
            jobs.Add(job);
            _claimable[job.Id] = job;
        }

        var maxCursor = Math.Max(cursor.Value, jobs.Count > 0 ? jobs.Max(j => j.Id) : cursor.Value);
        return Task.FromResult(new JobBatch { Jobs = jobs, Cursor = maxCursor });
    }

    public Task<Job?> ClaimAsync(long jobId, CancellationToken ct)
    {
        if (ClaimResponses.TryGetValue(jobId, out var claimed))
        {
            return Task.FromResult<Job?>(claimed);
        }

        // Lote de fetch: el claim devuelve la carcasa + documento que tenga el job.
        return Task.FromResult(_claimable.TryGetValue(jobId, out var job) ? job : null);
    }

    public Task AckAsync(long jobId, JobResult result, CancellationToken ct)
    {
        Acks.Add(result);
        return Task.CompletedTask;
    }

    public async Task<DocumentAck?> AckDetailedAsync(long jobId, JobResult result, CancellationToken ct)
    {
        DetailedAcks.Add((jobId, result));
        if (ThrowOnNextAck is { } error)
        {
            ThrowOnNextAck = null;
            throw error;
        }

        await Task.CompletedTask;
        return NextAckResponse;
    }

    public Task ReleaseAsync(long jobId, string reason, CancellationToken ct)
    {
        Releases.Add((jobId, reason));
        return Task.CompletedTask;
    }
}

/// <summary>Transporte de impresión falso: cuenta llamadas y captura la última solicitud.</summary>
public sealed class FakePrintTransport(
    Queue<PrintFailureKind?>? script = null) : IPrintTransport
{
    public int Calls { get; private set; }

    /// <summary>Punto de partida para contar solo las llamadas de un tramo (pruebas GUI-15).</summary>
    public int CallsAtMark { get; private set; }

    public void MarkCalls() => CallsAtMark = Calls;

    public int CallsSinceMark => Calls - CallsAtMark;

    public List<PrintRequest> Requests { get; } = [];

    public PrintRequest? LastRequest => Requests.LastOrDefault();

    public Task PrintAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        Requests.Add(request);
        if (script is { Count: > 0 })
        {
            var next = script.Dequeue();
            if (next is { } kind)
            {
                throw new PrintTransportException(kind, $"fallo simulado ({kind})");
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>Decodificador PNG falso: cuenta la llamada y devuelve un raster fijo.</summary>
public sealed class FakePngDecoder : IPngDecoder
{
    public int Calls { get; private set; }

    public int? LastTargetWidth { get; private set; }

    public PngRaster? NextResult { get; set; } = new()
    {
        WidthDots = 8,
        HeightDots = 1,
        Bitmap1Bpp = [0x80],
    };

    public PngRaster? TryDecodeToRaster(string base64Data, int? targetWidthDots = null, Action<string>? log = null)
    {
        Calls++;
        LastTargetWidth = targetWidthDots;
        return NextResult;
    }
}

/// <summary>Proveedor de salidas fijo para pruebas.</summary>
public sealed class StaticDestinationProvider(params DestinationOptions[] destinations) : IDestinationProvider
{
    public IReadOnlyList<DestinationOptions> Destinations { get; } = destinations;

    public IReadOnlyList<DestinationOptions> GetAll() => Destinations;

    public DestinationOptions? Resolve(string? role)
    {
        var wanted = string.IsNullOrEmpty(role) ? "default" : role;
        return Destinations.FirstOrDefault(d => string.Equals(d.Role, wanted, StringComparison.Ordinal));
    }
}

/// <summary>Fábrica que devuelve siempre el mismo transporte falso.</summary>
public sealed class FakePrintTransportFactory(FakePrintTransport transport) : IPrintTransportFactory
{
    public FakePrintTransport Transport { get; } = transport;

    public IPrintTransport Create(DestinationOptions destination) => Transport;
}

public static class TestDocuments
{
    public static Document Ticket(long jobId = 1844, int copies = 1, string role = "default") => new()
    {
        Version = 1,
        Meta = new DocumentMeta { JobId = jobId, OrderId = 5678, Copies = copies, PrinterRole = role },
        Paper = new DocumentPaper { WidthMm = 80 },
        Blocks =
        [
            new TextBlock { Value = "PIZZERIA RIO ROSSO", Align = TextAlign.Center, Bold = true },
            new TextBlock { Value = "Pizza Margarita · jamón, piña, ñ, áéíóú" },
        ],
    };
}
