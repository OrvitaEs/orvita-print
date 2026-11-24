using System.Reflection;
using Microsoft.Extensions.Logging;
using Orvita.Print.Contracts;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>Versión del agente que se reporta en /pair y /heartbeat (Doc/04 §5.3).</summary>
public static class AgentVersion
{
    public static string Current { get; } =
        typeof(AgentVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0-dev";

    /// <summary>
    /// La versión del ensamblado en forma parseable para comparaciones de actualización (F2-11):
    /// se recortan sufijos («1.0.0-dev» → «1.0.0»). false si aun así no parsea (canal inactivo).
    /// </summary>
    public static bool TryParseCurrentVersion(out Version version)
    {
        version = new Version(0, 0);
        var raw = Current;
        var cut = raw.IndexOfAny(['-', '+']);
        var clean = cut >= 0 ? raw[..cut] : raw;
        return Version.TryParse(clean, out version!) && version.Major >= 0;
    }
}

/// <summary>
/// Envía el heartbeat (<c>POST /heartbeat</c>, Doc/04 §5.3) cada 60 s con: versión del agente,
/// trabajos pendientes en la cola local, último error y las salidas reportadas (upsert por
/// role en el servidor). Si <c>config_version</c> cambió, re-sincroniza con
/// <c>GET /config</c> (Doc/04 §5.8) y aplica la configuración al <see cref="RemoteConfigState"/>.
/// El acceso es al transporte concreto (<see cref="DirectTransport"/>): heartbeat/config son
/// métodos suyos; las pruebas lo prueban con un <see cref="HttpClient"/> con handler falso.
/// </summary>
public sealed class HeartbeatSender
{
    private readonly Func<DirectTransport> _server;
    private readonly OutboxRepository _outbox;
    private readonly IDestinationProvider _destinations;
    private readonly RemoteConfigState _configState;
    private readonly AgentRuntimeStatus? _runtime;
    private readonly ServerPendingHint? _pendingHint;
    private readonly ILogger _logger;

    public HeartbeatSender(
        Func<DirectTransport> server,
        OutboxRepository outbox,
        IDestinationProvider destinations,
        RemoteConfigState configState,
        ILogger? logger = null,
        AgentRuntimeStatus? runtime = null,
        ServerPendingHint? pendingHint = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _destinations = destinations ?? throw new ArgumentNullException(nameof(destinations));
        _configState = configState ?? throw new ArgumentNullException(nameof(configState));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _runtime = runtime;
        _pendingHint = pendingHint;
    }

    /// <summary>
    /// Un latido. Devuelve la config_version aplicada (null si el servidor no la informó).
    /// Las excepciones de transporte se propagan: quien llama decide (el worker solo registra).
    /// Registra en <see cref="AgentRuntimeStatus"/> el «última vez visto por el servidor» (Doc/03 §11).
    /// </summary>
    public async Task<long?> SendOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = await SendOnceCoreAsync(cancellationToken).ConfigureAwait(false);
            _runtime?.MarkHeartbeatSuccess();
            return version;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runtime?.RecordHeartbeatError(ex.Message);
            throw;
        }
    }

    private async Task<long?> SendOnceCoreAsync(CancellationToken cancellationToken)
    {
        var server = _server();
        var request = new HeartbeatRequest
        {
            AgentVersion = AgentVersion.Current,
            PendingLocal = _outbox.GetByState(OutboxJobState.Received).Count
                           + _outbox.GetByState(OutboxJobState.Printing).Count,
            LastError = _outbox.GetByState(OutboxJobState.Error)
                .OrderByDescending(j => j.UpdatedAt)
                .FirstOrDefault()?.ErrorMessage,
            Destinations = [.. _destinations.GetAll().Select(ToDestinationInfo)],
        };

        var response = await server.SendHeartbeatAsync(request, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Heartbeat aceptado: {Pending} pendiente(s) en el servidor, config_version {Version}.",
            response.PendingCount, response.ConfigVersion);

        // GUI-15 (decisión C): el poller necesita saber si el servidor ve pendientes que el
        // cursor no alcanza — si es el caso y el fetch vuelve vacío, re-escanea desde after=0.
        _pendingHint?.Report(response.PendingCount ?? 0);

        if (response.ConfigVersion is { } version && version != _configState.ConfigVersion)
        {
            var config = await server.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            _configState.Update(config, _logger);
        }

        return _configState.ConfigVersion;
    }

    private DestinationInfo ToDestinationInfo(DestinationOptions destination) =>
        DestinationWire.From(destination, warn => _logger.LogWarning("{Message}", warn));
}
