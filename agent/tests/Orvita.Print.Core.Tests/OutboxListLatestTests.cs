using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary><see cref="OutboxRepository.ListLatest"/>: los últimos N trabajos del informe de diagnóstico (F2-12).</summary>
public sealed class OutboxListLatestTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"orp-outbox-latest-{Guid.NewGuid():N}.db");
    private readonly OutboxRepository _outbox;

    public OutboxListLatestTests() => _outbox = new OutboxRepository(_databasePath);

    public void Dispose()
    {
        _outbox.Dispose();
        File.Delete(_databasePath);
    }

    [Fact]
    public void Devuelve_los_ultimos_n_por_actividad_reciente()
    {
        for (var id = 1; id <= 60; id++)
        {
            Assert.True(_outbox.Enqueue(id, "default", 1, "{}"));
        }

        var latest = _outbox.ListLatest(50);

        Assert.Equal(50, latest.Count);
        Assert.Equal(60, latest[0].JobId); // el más reciente primero
        Assert.Equal(11, latest[^1].JobId); // y corta en el límite (60..11 = 50 trabajos)
        // Orden decreciente por job id (updated_at idéntico ⇒ desempata job_id DESC).
        Assert.True(latest.Zip(latest.Skip(1), (a, b) => a.JobId > b.JobId).All(x => x));
    }

    [Fact]
    public void Con_cola_vacia_devuelve_lista_vacia() => Assert.Empty(_outbox.ListLatest(50));

    [Fact]
    public void Limite_mayor_que_la_cola_devuelve_todo()
    {
        for (var id = 1; id <= 3; id++)
        {
            _outbox.Enqueue(id, "default", 1, "{}");
        }

        Assert.Equal(3, _outbox.ListLatest(50).Count);
    }
}
