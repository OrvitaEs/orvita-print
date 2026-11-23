using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Orvita.Print.Core;

/// <summary>
/// Emparejamiento del agente con su servidor (Doc/03 §6.2.1): ServerUrl + token cifrado
/// (<see cref="TokenProtected"/>, opaco para el almacén) + el local devuelto por
/// <c>POST /pair</c> (Doc/04 §5.2). El token en claro JAMÁS pasa por aquí (S-01/S-05).
/// </summary>
public sealed record PairingInfo
{
    public string? ServerUrl { get; init; }

    /// <summary>Token tal cual se almacena (cifrado por ISecretProtector). Opaco para este almacén.</summary>
    public string? TokenProtected { get; init; }

    public DateTimeOffset? PairedAtUtc { get; init; }

    public long? LocationId { get; init; }

    public string? LocationCode { get; init; }

    public string? LocationName { get; init; }

    /// <summary>Nombre con el que el agente se presentó en el emparejamiento (Doc/04 §5.2).</summary>
    public string? AgentName { get; init; }

    /// <summary>true si hay URL y token protegido.</summary>
    public bool IsPaired =>
        !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(TokenProtected);
}

/// <summary>
/// Almacén local de configuración del agente (tarea F2-10, Doc/03 §6.2.1): capa «dinámica
/// local» — ServerUrl, token protegido (S-01) y salidas (destinos, Doc/03 §6.7). Sustituye a
/// la configuración bootstrap de appsettings para todo lo gestionable: solo escribe el
/// servicio (vía la pipe de gestión, F2-08); el CLI nunca toca este fichero directamente.
///
/// Decisión documentada: es la MISMA BD que la cola (<c>outbox.db</c>) con tablas propias
/// (<c>agent_config</c>, <c>agent_destinations</c>) — un solo fichero en
/// <c>%ProgramData%\OrvitaPrint</c>, un solo respaldo, un solo candado de escritura.
/// El token llega YA cifrado: este almacén no conoce DPAPI (portable, ADR-12).
/// </summary>
public sealed class LocalConfigStore : IDisposable
{
    /// <summary>Espera máxima ante bloqueo de otro proceso/hilo antes de fallar con SQLITE_BUSY.</summary>
    public const int BusyTimeoutMs = 5000;

    private readonly string _databasePath;
    private readonly string _connectionString;

    /// <summary>Log opcional (cambios de configuración); el Service lo conecta a su ILogger.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Ruta absoluta del fichero de BD (compartida con la cola).</summary>
    public string DatabasePath => _databasePath;

    /// <param name="databasePath">Ruta del fichero SQLite (la misma de la cola: outbox.db); se crea el directorio si falta.</param>
    public LocalConfigStore(string databasePath)
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

    // ------------------------------------------------------------- emparejamiento (config)

    /// <summary>Emparejamiento actual; uno sin datos (IsPaired = false) si nunca se emparejó.</summary>
    public PairingInfo GetPairing()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM agent_config;";
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = reader.GetString(0);
                values[key] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        values.TryGetValue("server_url", out var url);
        values.TryGetValue("token_protected", out var token);
        values.TryGetValue("location_code", out var locationCode);
        values.TryGetValue("location_name", out var locationName);
        values.TryGetValue("agent_name", out var agentName);

        long? locationId = values.TryGetValue("location_id", out var rawId) && long.TryParse(rawId, out var id)
            ? id
            : null;
        DateTimeOffset? pairedAt = values.TryGetValue("paired_at", out var rawAt) && rawAt is not null
            ? DateTimeOffset.Parse(rawAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

        return new PairingInfo
        {
            ServerUrl = url,
            TokenProtected = token,
            PairedAtUtc = pairedAt,
            LocationId = locationId,
            LocationCode = locationCode,
            LocationName = locationName,
            AgentName = agentName,
        };
    }

    /// <summary>
    /// Guarda (o reemplaza) el emparejamiento de forma atómica. El token llega YA cifrado por
    /// el llamador (S-01); un nuevo pair sustituye URL, token y local juntos.
    /// </summary>
    public void SetPairing(PairingInfo pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairing.ServerUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairing.TokenProtected);

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Upsert(connection, transaction, "server_url", pairing.ServerUrl);
        Upsert(connection, transaction, "token_protected", pairing.TokenProtected);
        Upsert(connection, transaction, "paired_at", (pairing.PairedAtUtc ?? DateTimeOffset.UtcNow)
            .ToString("O", CultureInfo.InvariantCulture));
        Upsert(connection, transaction, "location_id", pairing.LocationId?.ToString(CultureInfo.InvariantCulture));
        Upsert(connection, transaction, "location_code", pairing.LocationCode);
        Upsert(connection, transaction, "location_name", pairing.LocationName);
        Upsert(connection, transaction, "agent_name", pairing.AgentName);
        transaction.Commit();
        Log?.Invoke($"Emparejamiento guardado para «{pairing.LocationName ?? pairing.ServerUrl}» (token cifrado, S-01).");
    }

    /// <summary>Borra el emparejamiento (URL, token y local). Las salidas se conservan.</summary>
    public void ClearPairing()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM agent_config WHERE key IN ('server_url', 'token_protected', 'paired_at', 'location_id', 'location_code', 'location_name');
            """;
        _ = command.ExecuteNonQuery();
        Log?.Invoke("Emparejamiento borrado del almacén local.");
    }

    // ----------------------------------------------------------------------- destinos (§6.7)

    /// <summary>Todas las salidas configuradas (activas e inactivas), para heartbeat y panel.</summary>
    public IReadOnlyList<DestinationOptions> ListDestinations()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT role, label, printer_name, copies, enabled, kind, address
            FROM agent_destinations ORDER BY role;
            """;
        var destinations = new List<DestinationOptions>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            destinations.Add(new DestinationOptions
            {
                Role = reader.GetString(0),
                Label = reader.IsDBNull(1) ? null : reader.GetString(1),
                PrinterName = reader.IsDBNull(2) ? null : reader.GetString(2),
                Copies = reader.GetInt32(3),
                Enabled = reader.GetInt64(4) != 0,
                Kind = ParseKindName(reader.GetString(5)),
                Address = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }

        return destinations;
    }

    /// <summary>
    /// Crea o reemplaza la salida por su <c>role</c> (único, Doc/04 §2.5: upsert por role).
    /// Devuelve true si se creó y false si actualizó una existente.
    /// </summary>
    public bool UpsertDestination(DestinationOptions destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination.Role);
        var copies = Math.Clamp(destination.Copies, 1, 99);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(1) FROM agent_destinations WHERE role = $role;
            """;
        command.Parameters.AddWithValue("$role", destination.Role);
        var existed = Convert.ToInt64(command.ExecuteScalar()!) > 0;

        command.CommandText = """
            INSERT INTO agent_destinations (role, label, printer_name, copies, enabled, kind, address, updated_at)
            VALUES ($role, $label, $printer_name, $copies, $enabled, $kind, $address, $now)
            ON CONFLICT(role) DO UPDATE SET
                label = excluded.label,
                printer_name = excluded.printer_name,
                copies = excluded.copies,
                enabled = excluded.enabled,
                kind = excluded.kind,
                address = excluded.address,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$label", (object?)destination.Label ?? DBNull.Value);
        command.Parameters.AddWithValue("$printer_name", (object?)destination.PrinterName ?? DBNull.Value);
        command.Parameters.AddWithValue("$copies", copies);
        command.Parameters.AddWithValue("$enabled", destination.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$kind", ToKindName(destination.Kind));
        command.Parameters.AddWithValue("$address", (object?)destination.Address ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        _ = command.ExecuteNonQuery();

        Log?.Invoke($"Salida «{destination.Role}» {(existed ? "actualizada" : "creada")} en el almacén local.");
        return !existed;
    }

    /// <summary>Elimina la salida por role exacto; false si no existía.</summary>
    public bool RemoveDestination(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_destinations WHERE role = $role;";
        command.Parameters.AddWithValue("$role", role);
        var removed = command.ExecuteNonQuery() > 0;
        if (removed)
        {
            Log?.Invoke($"Salida «{role}» eliminada del almacén local.");
        }

        return removed;
    }

    public void Dispose()
    {
        // Suelta el pool para que el fichero compartido con la cola se libere al parar el servicio.
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
    }

    // -------------------------------------------------------------------------- infra SQLite

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // MISMAS garantías que la cola (Doc/03 §6.4): WAL resiste cortes de luz, busy_timeout
        // tolera la contention con OutboxRepository (misma BD), synchronous NORMAL.
        Execute(connection, $"PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = {BusyTimeoutMs};");
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS agent_config (
                key   TEXT PRIMARY KEY,
                value TEXT
            );
            CREATE TABLE IF NOT EXISTS agent_destinations (
                role         TEXT PRIMARY KEY,
                label        TEXT,
                printer_name TEXT,
                copies       INTEGER NOT NULL DEFAULT 1,
                enabled      INTEGER NOT NULL DEFAULT 1,
                kind         TEXT NOT NULL DEFAULT 'auto',
                address      TEXT,
                updated_at   TEXT NOT NULL
            );
            """);
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction transaction, string key, string? value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_config(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ nombres de transporte

    /// <summary>Nombre estable para la BD y el CLI (los nombres de enum son detalle interno).</summary>
    public static string ToKindName(PrintTransportKind kind) => kind switch
    {
        PrintTransportKind.RawSpooler => "raw",
        PrintTransportKind.Gdi => "gdi",
        PrintTransportKind.Tcp9100 => "tcp",
        PrintTransportKind.Pdf => "pdf",
        _ => "auto",
    };

    /// <summary>Lee el nombre guardado; lo desconocido degrada a Auto (comportamiento por defecto).</summary>
    public static PrintTransportKind ParseKindName(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "raw" => PrintTransportKind.RawSpooler,
        "gdi" => PrintTransportKind.Gdi,
        "tcp" => PrintTransportKind.Tcp9100,
        "pdf" => PrintTransportKind.Pdf,
        _ => PrintTransportKind.Auto,
    };
}
