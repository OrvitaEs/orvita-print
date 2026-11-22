using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orvita.Print.Contracts;

namespace Orvita.Print.Core;

/// <summary>
/// Estado local de un trabajo en la cola (Doc/03 §6.4): <c>received → printing → done/error</c>.
/// No es el estado del trabajo en el servidor (<see cref="JobResultStatus"/>): es el de la cola local.
/// </summary>
public enum OutboxJobState
{
    Received,
    Printing,
    Done,
    Error,
}

/// <summary>
/// Trabajo en la cola local. <see cref="DocumentJson"/> es el documento neutro tal cual llegó
/// (Doc/04 §7); <see cref="TryParseDocument"/> da la forma tipada. <c>DuplicateSuspected</c> es un
/// marcador, no un estado: se activa cuando un trabajo quedado en <c>printing</c> se reimpresiona
/// tras un reinicio (criterio de producto: mejor un duplicado que un pedido perdido).
/// </summary>
public sealed class OutboxJob
{
    public long JobId { get; init; }

    public string? PrinterRole { get; init; }

    /// <summary>Copias congeladas al crear el trabajo (Doc/04 §2.8). El multiplicador lo aplica el transporte.</summary>
    public int Copies { get; init; }

    public required string DocumentJson { get; init; }

    public OutboxJobState State { get; set; }

    /// <summary>Intentos locales de impresión: se incrementa en cada claim (received → printing).</summary>
    public int Attempts { get; set; }

    public bool DuplicateSuspected { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? PrintedAt { get; set; }

    /// <summary>
    /// Documento tipado; null si el JSON no deserializa. El procesador debe marcar entonces el
    /// trabajo como error con <c>orp_unsupported_document</c>.
    /// </summary>
    public Document? TryParseDocument()
    {
        try
        {
            return JsonSerializer.Deserialize<Document>(DocumentJson, OrpJson.Default);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Cola local (outbox) sobre SQLite — <c>%ProgramData%\OrvitaPrint\outbox.db</c> en producción
/// (Doc/03 §6.2), ruta inyectada aquí. Solo guarda trabajos: <c>ServerUrl</c>, token y destinos
/// NO viven en esta BD (F2-10, Doc/03 §6.2.1).
///
/// Garantías clave:
/// - <b>Inserción idempotente</b> por job id: un reintento del poller no duplica trabajos.
/// - <b>Reclamo atómico</b>: el claim es un único <c>UPDATE … RETURNING</c>; dos procesadores
///   (o dos hilos) nunca llevan el mismo trabajo en paralelo.
/// - <b>Recuperación de arranque</b> (Doc/03 §6.4): los trabajos que quedaron en <c>printing</c>
///   sin resultado (caída, corte de luz) se reencolan marcados <c>duplicate_suspected</c>.
///
/// SQLite se abre con WAL (resiste cortes de luz sin corromper la BD y permite lecturas
/// concurrentes), <c>busy_timeout</c> y <c>synchronous=NORMAL</c>.
/// </summary>
public sealed class OutboxRepository : IDisposable
{
    /// <summary>Espera máxima ante bloqueo de otro proceso/hilo antes de fallar con SQLITE_BUSY.</summary>
    public const int BusyTimeoutMs = 5000;

    /// <summary>Versión del esquema local; sube con migraciones futuras.</summary>
    public const string SchemaVersion = "1";

    private readonly string _databasePath;
    private readonly string _connectionString;

    /// <summary>Log opcional (recuperaciones, duplicados); el Service lo conecta a su ILogger.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Ruta absoluta del fichero de BD.</summary>
    public string DatabasePath => _databasePath;

    /// <param name="databasePath">Ruta del fichero SQLite; se crea el directorio si falta. No hay ruta fija en el código.</param>
    public OutboxRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        Initialize();
    }

    /// <summary>Inserta el trabajo del servidor si no estaba; false si ya existía (idempotente por job id).</summary>
    public bool Enqueue(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var documentJson = job.Document is null
            ? "{}"
            : JsonSerializer.Serialize(job.Document, OrpJson.Default);
        return Enqueue(job.Id, job.PrinterRole, job.Document?.Meta?.Copies ?? 1, documentJson);
    }

    /// <summary>Inserta un trabajo si no existe (idempotente por job id). Devuelve true solo la primera vez.</summary>
    public bool Enqueue(long jobId, string? printerRole, int copies, string documentJson)
    {
        if (jobId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(jobId), "El job id del servidor es un entero positivo.");
        }

        ArgumentNullException.ThrowIfNull(documentJson);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO outbox_jobs
                (job_id, printer_role, copies, document_json, state, attempts, duplicate_suspected, created_at, updated_at)
            VALUES
                ($job_id, $printer_role, $copies, $document_json, 'received', 0, 0, $now, $now)
            ON CONFLICT(job_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$printer_role", ToDbValue(printerRole));
        command.Parameters.AddWithValue("$copies", Math.Max(1, copies));
        command.Parameters.AddWithValue("$document_json", documentJson);
        command.Parameters.AddWithValue("$now", Now());

        var inserted = command.ExecuteNonQuery() > 0;
        if (!inserted)
        {
            Log?.Invoke($"El trabajo {jobId} ya estaba en la cola; se ignora el duplicado (idempotencia por job id).");
        }

        return inserted;
    }

    /// <summary>
    /// Reclama atómicamente el siguiente trabajo en <c>received</c> (FIFO por job id) y lo pasa a
    /// <c>printing</c>. Dos llamadas concurrentes nunca devuelven el mismo trabajo: el reclamo es
    /// un único UPDATE…RETURNING, serializado por el candado de escritura de SQLite.
    /// Devuelve el trabajo con <see cref="OutboxJob.DocumentJson"/> y su <see cref="Document"/>
    /// tipado (null si el JSON no deserializa; el procesador debe marcar orp_unsupported_document).
    /// </summary>
    public OutboxJob? TryClaimNext()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outbox_jobs
            SET state = 'printing', attempts = attempts + 1, updated_at = $now
            WHERE job_id = (
                SELECT job_id FROM outbox_jobs WHERE state = 'received' ORDER BY job_id LIMIT 1
            )
            RETURNING job_id, printer_role, copies, document_json, state, attempts, duplicate_suspected,
                      error_code, error_message, created_at, updated_at, printed_at;
            """;
        command.Parameters.AddWithValue("$now", Now());

        using var reader = command.ExecuteReader();
        return reader.Read() ? MapJob(reader) : null;
    }

    /// <summary>
    /// Marca el trabajo como <c>done</c> (impreso). Solo transiciona desde <c>printing</c>: devuelve
    /// false si el trabajo no estaba en printing (p. ej. la recuperación de arranque lo reencoló).
    /// </summary>
    public bool MarkDone(long jobId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outbox_jobs
            SET state = 'done', printed_at = $now, updated_at = $now, error_code = NULL, error_message = NULL
            WHERE job_id = $job_id AND state = 'printing';
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$now", Now());
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Marca el trabajo como <c>error</c> con el código y mensaje legible. Solo transiciona desde
    /// <c>printing</c> (Doc/03 §6.4: received → printing → done/error); devuelve false en otro caso.
    /// </summary>
    public bool MarkError(long jobId, string? errorCode, string? errorMessage)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outbox_jobs
            SET state = 'error', updated_at = $now, error_code = $error_code, error_message = $error_message
            WHERE job_id = $job_id AND state = 'printing';
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$error_code", ToDbValue(errorCode));
        command.Parameters.AddWithValue("$error_message", ToDbValue(errorMessage));
        command.Parameters.AddWithValue("$now", Now());
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Recuperación de arranque (Doc/03 §6.4): reencola todo trabajo quedado en <c>printing</c> sin
    /// resultado (caída o corte de luz) y lo marca <c>duplicate_suspected</c>. Devuelve los trabajos
    /// recuperados, ya listos para ser reclamados de nuevo por <see cref="TryClaimNext"/> (se reimprimirán).
    /// </summary>
    public IReadOnlyList<OutboxJob> RecoverInterrupted()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outbox_jobs
            SET state = 'received', duplicate_suspected = 1, updated_at = $now
            WHERE state = 'printing'
            RETURNING job_id, printer_role, copies, document_json, state, attempts, duplicate_suspected,
                      error_code, error_message, created_at, updated_at, printed_at;
            """;
        command.Parameters.AddWithValue("$now", Now());

        var recovered = new List<OutboxJob>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var job = MapJob(reader);
            recovered.Add(job);
            Log?.Invoke(
                $"El trabajo {job.JobId} quedó en «printing» sin resultado (caída o corte de luz); " +
                "se reencola marcado duplicate_suspected: mejor un duplicado que un pedido perdido (Doc/03 §6.4).");
        }

        return recovered;
    }

    /// <summary>
    /// Reemplaza el documento de un trabajo aún no reclamado (el listado de /jobs llega sin payload y
    /// el claim con él completo; Doc/04 §5.4–5.5). Devuelve false si el trabajo no está en <c>received</c>.
    /// </summary>
    public bool UpdateDocument(long jobId, string documentJson)
    {
        ArgumentNullException.ThrowIfNull(documentJson);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE outbox_jobs
            SET document_json = $document_json, updated_at = $now
            WHERE job_id = $job_id AND state = 'received';
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        command.Parameters.AddWithValue("$document_json", documentJson);
        command.Parameters.AddWithValue("$now", Now());
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Trabajo por id, o null si no está en la cola.</summary>
    public OutboxJob? Get(long jobId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, printer_role, copies, document_json, state, attempts, duplicate_suspected,
                   error_code, error_message, created_at, updated_at, printed_at
            FROM outbox_jobs WHERE job_id = $job_id;
            """;
        command.Parameters.AddWithValue("$job_id", jobId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapJob(reader) : null;
    }

    /// <summary>Trabajos en un estado (FIFO por job id). Para `status`/`doctor` y pruebas.</summary>
    public IReadOnlyList<OutboxJob> GetByState(OutboxJobState state)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, printer_role, copies, document_json, state, attempts, duplicate_suspected,
                   error_code, error_message, created_at, updated_at, printed_at
            FROM outbox_jobs WHERE state = $state ORDER BY job_id;
            """;
        command.Parameters.AddWithValue("$state", ToDb(state));
        var jobs = new List<OutboxJob>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            jobs.Add(MapJob(reader));
        }

        return jobs;
    }

    public void Dispose()
    {
        // Devuelve al pool las conexiones de esta cadena para que el fichero .db se pueda
        // borrar en pruebas y el servicio libere el fichero al detenerse.
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // WAL: sobrevive a cortes de luz sin corromper la BD y permite leer mientras se escribe.
        // busy_timeout: ante contention con otro proceso, espera en vez de fallar. Es por conexión.
        Execute(connection, $"PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = {BusyTimeoutMs};");
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS outbox_jobs (
                job_id              INTEGER PRIMARY KEY,
                printer_role        TEXT,
                copies              INTEGER NOT NULL DEFAULT 1,
                document_json       TEXT NOT NULL,
                state               TEXT NOT NULL DEFAULT 'received',
                attempts            INTEGER NOT NULL DEFAULT 0,
                duplicate_suspected INTEGER NOT NULL DEFAULT 0,
                error_code          TEXT,
                error_message       TEXT,
                created_at          TEXT NOT NULL,
                updated_at          TEXT NOT NULL,
                printed_at          TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_outbox_jobs_state ON outbox_jobs(state, job_id);
            CREATE TABLE IF NOT EXISTS outbox_meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT OR IGNORE INTO outbox_meta(key, value) VALUES ('schema_version', '1');
            """);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private static OutboxJob MapJob(SqliteDataReader reader) => new()
    {
        JobId = reader.GetInt64(reader.GetOrdinal("job_id")),
        PrinterRole = reader.IsDBNull(reader.GetOrdinal("printer_role")) ? null : reader.GetString(reader.GetOrdinal("printer_role")),
        Copies = reader.GetInt32(reader.GetOrdinal("copies")),
        DocumentJson = reader.GetString(reader.GetOrdinal("document_json")),
        State = FromDb(reader.GetString(reader.GetOrdinal("state"))),
        Attempts = reader.GetInt32(reader.GetOrdinal("attempts")),
        DuplicateSuspected = reader.GetInt64(reader.GetOrdinal("duplicate_suspected")) != 0,
        ErrorCode = reader.IsDBNull(reader.GetOrdinal("error_code")) ? null : reader.GetString(reader.GetOrdinal("error_code")),
        ErrorMessage = reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message")),
        CreatedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = ParseTimestamp(reader.GetString(reader.GetOrdinal("updated_at"))),
        PrintedAt = reader.IsDBNull(reader.GetOrdinal("printed_at"))
            ? null
            : ParseTimestamp(reader.GetString(reader.GetOrdinal("printed_at"))),
    };

    private static string ToDb(OutboxJobState state) => state switch
    {
        OutboxJobState.Received => "received",
        OutboxJobState.Printing => "printing",
        OutboxJobState.Done => "done",
        OutboxJobState.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Estado de outbox desconocido."),
    };

    private static OutboxJobState FromDb(string value) => value switch
    {
        "printing" => OutboxJobState.Printing,
        "done" => OutboxJobState.Done,
        "error" => OutboxJobState.Error,
        "received" => OutboxJobState.Received,
        _ => OutboxJobState.Received,
    };

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Microsoft.Data.Sqlite no acepta null de C# en parámetros: NULL de SQL es DBNull.</summary>
    private static object ToDbValue(string? value) => (object?)value ?? DBNull.Value;

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
