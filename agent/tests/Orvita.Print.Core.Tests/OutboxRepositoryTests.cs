using Microsoft.Data.Sqlite;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>
/// Cola local SQLite (Doc/03 §6.4, Doc/07 F2-03): inserción idempotente por job id,
/// transiciones received → printing → done/error, recuperación de arranque con
/// duplicate_suspected y exclusión mutua entre procesadores.
/// </summary>
public sealed class OutboxRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"orp-outbox-{Guid.NewGuid():N}.db");
    private readonly OutboxRepository _repository;
    private readonly List<string> _logs = [];

    public OutboxRepositoryTests()
    {
        _repository = new OutboxRepository(_path);
        _repository.Log = _logs.Add;
    }

    public void Dispose()
    {
        _repository.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(_path + suffix);
            }
            catch (IOException)
            {
            }
        }
    }

    private static Document SampleDocument(int copies = 1, string role = "default") => new()
    {
        Version = 1,
        Meta = new DocumentMeta { JobId = 1844, OrderId = 5678, LocationCode = "pueblo-b", Copies = copies, PrinterRole = role },
        Paper = new DocumentPaper { WidthMm = 80 },
        Blocks = [new TextBlock { Value = "Pizza Margarita" }],
    };

    private static Job SampleJob(long id, Document? document = null) => new()
    {
        Id = id,
        OrderId = 5678,
        PrinterRole = (document ?? SampleDocument()).Meta?.PrinterRole,
        Document = document ?? SampleDocument(),
    };

    // ---------------------------------------------------- idempotencia

    [Fact]
    public void Insertar_el_mismo_job_id_dos_veces_no_duplica()
    {
        Assert.True(_repository.Enqueue(SampleJob(7)));
        Assert.False(_repository.Enqueue(SampleJob(7)));

        var received = _repository.GetByState(OutboxJobState.Received);
        var job = Assert.Single(received);
        Assert.Equal(7, job.JobId);
        Assert.Contains(_logs, l => l.Contains("ya estaba en la cola"));
    }

    [Fact]
    public void Enqueue_guarda_rol_copias_y_documento_tipado()
    {
        var document = SampleDocument(copies: 3, role: "cocina");
        document.Blocks.Add(new DividerBlock());

        _repository.Enqueue(new Job { Id = 9, PrinterRole = "cocina", Document = document });

        var claimed = _repository.TryClaimNext();
        Assert.NotNull(claimed);
        Assert.Equal(9, claimed.JobId);
        Assert.Equal("cocina", claimed.PrinterRole);
        Assert.Equal(3, claimed.Copies);
        Assert.Equal(OutboxJobState.Printing, claimed.State);

        var parsed = claimed.TryParseDocument();
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.Blocks.Count);
        Assert.IsType<DividerBlock>(parsed.Blocks[1]);
    }

    [Fact]
    public void Copias_por_debajo_de_uno_se_ajustan_a_uno()
    {
        var document = SampleDocument(copies: 0);
        _repository.Enqueue(new Job { Id = 5, Document = document });
        Assert.Equal(1, _repository.Get(5)?.Copies);
    }

    // ---------------------------------------------------- transiciones

    [Fact]
    public void Transicion_received_printing_done()
    {
        _repository.Enqueue(SampleJob(1));

        var claimed = _repository.TryClaimNext();
        Assert.NotNull(claimed);
        Assert.Equal(OutboxJobState.Printing, claimed.State);
        Assert.Equal(1, claimed.Attempts);

        Assert.True(_repository.MarkDone(1));
        var done = _repository.Get(1);
        Assert.Equal(OutboxJobState.Done, done?.State);
        Assert.NotNull(done?.PrintedAt);
        Assert.Null(done?.ErrorCode);

        // Segundo MarkDone no re-transitiona ni relanza: solo desde printing.
        Assert.False(_repository.MarkDone(1));
        Assert.Null(_repository.TryClaimNext());
    }

    [Fact]
    public void Transicion_received_printing_error_con_codigo()
    {
        _repository.Enqueue(SampleJob(2));

        var claimed = _repository.TryClaimNext();
        Assert.NotNull(claimed);

        Assert.True(_repository.MarkError(2, "orp_printer_not_found", "No existe la impresora «COCINA»."));
        var error = _repository.Get(2);
        Assert.Equal(OutboxJobState.Error, error?.State);
        Assert.Equal("orp_printer_not_found", error?.ErrorCode);
        Assert.Equal("No existe la impresora «COCINA».", error?.ErrorMessage);
        Assert.Equal(1, error?.Attempts);
    }

    [Fact]
    public void Done_y_error_solo_transicionan_desde_printing()
    {
        _repository.Enqueue(SampleJob(3));

        // Aún en received: ningún cambio de estado directo.
        Assert.False(_repository.MarkDone(3));
        Assert.False(_repository.MarkError(3, "x", null));
        Assert.Equal(OutboxJobState.Received, _repository.Get(3)?.State);
    }

    [Fact]
    public void UpdateDocument_solo_mientras_el_trabajo_esta_en_received()
    {
        _repository.Enqueue(4, "default", 1, "{}");
        Assert.True(_repository.UpdateDocument(4, @"{""version"":1,""blocks"":[]}"));
        Assert.NotNull(_repository.Get(4)?.TryParseDocument());

        _ = _repository.TryClaimNext();
        Assert.False(_repository.UpdateDocument(4, @"{""version"":1,""blocks"":[]}"));
    }

    // ---------------------------------------------------- recuperación

    [Fact]
    public void Recuperacion_de_arranque_reencola_y_marca_duplicate_suspected()
    {
        var path = _path;
        using (var first = new OutboxRepository(path))
        {
            first.Enqueue(SampleJob(11));
            first.Enqueue(SampleJob(12));
            _ = first.TryClaimNext(); // 11 → printing
            _ = first.TryClaimNext(); // 12 → printing
            Assert.True(first.MarkDone(12)); // 12 termina; solo 11 queda «en vuelo»
        }
        // «Corte de luz»: se reabre la BD con otra instancia (simula el arranque del servicio).

        using (var second = new OutboxRepository(path))
        {
            second.Log = _logs.Add;
            var recovered = second.RecoverInterrupted();
            var job = Assert.Single(recovered);
            Assert.Equal(11, job.JobId);
            Assert.True(job.DuplicateSuspected);
            Assert.Equal(OutboxJobState.Received, job.State);

            // El trabajo recuperado se puede reclamar de nuevo y conserva el marcador y el intento.
            var claimed = second.TryClaimNext();
            Assert.NotNull(claimed);
            Assert.Equal(11, claimed.JobId);
            Assert.True(claimed.DuplicateSuspected);
            Assert.Equal(2, claimed.Attempts);
            Assert.True(second.MarkDone(11));
        }

        Assert.Contains(_logs, l => l.Contains("duplicate_suspected"));
        var done = _repository.Get(11);
        Assert.Equal(OutboxJobState.Done, done?.State);
        Assert.True(done?.DuplicateSuspected);
    }

    [Fact]
    public void Recuperacion_sin_trabajos_en_printing_no_toca_nada()
    {
        _repository.Enqueue(SampleJob(21));
        _repository.Enqueue(SampleJob(22));
        _ = _repository.TryClaimNext();
        Assert.True(_repository.MarkDone(21));

        var recovered = _repository.RecoverInterrupted();
        Assert.Empty(recovered);

        var received = _repository.Get(22);
        Assert.Equal(OutboxJobState.Received, received?.State);
        Assert.False(received?.DuplicateSuspected);
    }

    // ---------------------------------------------------- concurrencia

    [Fact]
    public async Task Dos_procesadores_reclaman_en_paralelo_sin_repetir_trabajo()
    {
        const int total = 12;
        for (var id = 1; id <= total; id++)
        {
            _repository.Enqueue(SampleJob(id));
        }

        var claimedBy = new List<long>[2];
        var tasks = Enumerable.Range(0, 2).Select(processor => Task.Run(() =>
        {
            var claimed = new List<long>();
            while (_repository.TryClaimNext() is { } job)
            {
                claimed.Add(job.JobId);
                Assert.True(_repository.MarkDone(job.JobId));
            }

            claimedBy[processor] = claimed;
        })).ToArray();

        await Task.WhenAll(tasks);

        var all = claimedBy.SelectMany(ids => ids).ToList();
        Assert.Equal(total, all.Count);
        Assert.Equal(total, all.Distinct().Count()); // ningún trabajo procesado dos veces
        Assert.All(Enumerable.Range(1, total), id => Assert.Equal(OutboxJobState.Done, _repository.Get(id)?.State));
    }

    // ---------------------------------------------------- configuración de SQLite

    [Fact]
    public void La_bd_se_abre_con_wal_y_registra_la_version_de_esquema()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        connection.Open();

        using (var mode = connection.CreateCommand())
        {
            mode.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", mode.ExecuteScalar());
        }

        using (var meta = connection.CreateCommand())
        {
            meta.CommandText = "SELECT value FROM outbox_meta WHERE key = 'schema_version';";
            Assert.Equal(OutboxRepository.SchemaVersion, meta.ExecuteScalar());
        }
    }

    [Fact]
    public void La_ruta_de_bd_es_inyectable_y_no_hay_ruta_fija()
    {
        var root = Path.Combine(Path.GetTempPath(), $"orp-outbox-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "sub", "dir", "outbox.db");
        try
        {
            using var repository = new OutboxRepository(nested);
            Assert.True(repository.Enqueue(SampleJob(1)));
            Assert.Equal(Path.GetFullPath(nested), repository.DatabasePath);
            Assert.Equal(1, repository.Get(1)?.JobId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
