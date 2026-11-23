using Microsoft.Extensions.Logging;
using Orvita.Print.Contracts;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>Resultado de un emparejamiento aplicado (lo muestra <c>orvitaprint pair</c>).</summary>
public sealed record PairOutcome(
    string ServerUrl,
    long? LocationId,
    string? LocationName,
    string? LocationCode,
    int? PollIntervalSeconds);

/// <summary>
/// Flujo de emparejamiento END-TO-END (F2-08 + F2-10, <c>orvitaprint pair --server &lt;url&gt; &lt;código&gt;</c>):
/// 1. valida la URL (S-03: HTTPS obligatorio salvo modo desarrollo explícito);
/// 2. <c>POST /pair</c> (Doc/04 §5.2) con el código, nombre y salidas ya configuradas;
/// 3. guarda URL + token CIFRADO (DPAPI, S-01) + local en el almacén local (F2-10);
/// 4. crea el <see cref="DirectTransport"/> (token claro SOLO en memoria, nunca a disco ni log, S-05)
///    y lo aplica a <see cref="ServerConnection"/> — el poller arranca sin reiniciar el proceso.
/// El token NUNCA vuelve por la pipe: la respuesta es «ok» sin eco (S-05).
/// </summary>
public sealed class PairingService
{
    private readonly ServerConnection _connection;
    private readonly LocalConfigStore _store;
    private readonly ISecretProtector _protector;
    private readonly PairingWaiter _waiter;
    private readonly AgentRuntimeStatus _runtime;
    private readonly IDestinationProvider _destinations;
    private readonly ILogger<PairingService> _logger;
    private readonly HttpClient? _httpClient; // pruebas (handler falso); null → HttpClient propio

    public PairingService(
        ServerConnection connection,
        LocalConfigStore store,
        ISecretProtector protector,
        PairingWaiter waiter,
        AgentRuntimeStatus runtime,
        IDestinationProvider destinations,
        ILogger<PairingService> logger,
        HttpClient? httpClient = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _waiter = waiter ?? throw new ArgumentNullException(nameof(waiter));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _destinations = destinations ?? throw new ArgumentNullException(nameof(destinations));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient;
    }

    /// <summary>Nombre por defecto del agente si el CLI no aporta uno (Doc/04 §5.2 agent_name).</summary>
    public static string DefaultAgentName() => $"TPV {Environment.MachineName}";

    /// <summary>
    /// Carga el emparejamiento guardado en el almacén al ARRANCAR el servicio (F2-10): descifra
    /// el token con DPAPI (S-01) y crea el transporte — el agente sobrevive a reinicios y cortes
    /// de luz sin reemparejar. El token claro va SOLO a memoria (S-05). false si no hay
    /// emparejamiento o el token no se puede descifrar (estado visible, el servicio sigue vivo).
    /// </summary>
    public bool TryLoadPairingFromStore()
    {
        if (_connection.Transport is not null)
        {
            return true;
        }

        var pairing = _store.GetPairing();
        if (!pairing.IsPaired)
        {
            return false;
        }

        if (!Uri.TryCreate(pairing.ServerUrl!.Trim(), UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            _logger.LogError(
                "El emparejamiento guardado tiene una URL no válida («{Url}»): reempareja con orvitaprint pair.",
                pairing.ServerUrl);
            return false;
        }

        var token = _protector.Unprotect(pairing.TokenProtected!);
        if (token is null)
        {
            _logger.LogError(
                "El token guardado no se puede descifrar en esta máquina (cambió la máquina, el usuario o la " +
                "instalación, S-01): reempareja con orvitaprint pair. El servicio sigue vivo.");
            return false;
        }

        _connection.SetTransport(url, token, allowInsecureHttp: url.Scheme == Uri.UriSchemeHttp, _httpClient);
        _runtime.SetPairPollInterval(null);
        _logger.LogInformation(
            "Emparejamiento cargado del almacén local (local «{Location}», guardado {Paired}): heartbeat y poller arrancan.",
            pairing.LocationName ?? pairing.LocationCode,
            pairing.PairedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        return true;
    }

    /// <summary>
    /// Aplica el emparejamiento completo. Lanza <see cref="PairingException"/> ante error
    /// contractual del servidor o <see cref="ArgumentException"/> ante URL inválida/insegura;
    /// en ambos casos el almacén queda como estaba.
    /// </summary>
    public async Task<PairOutcome> ApplyPairingAsync(
        string serverUrl,
        string code,
        string? agentName,
        bool allowInsecureHttp,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        // 1. S-03: HTTPS obligatorio. El CLI valida antes de enviar; el servicio NO confía en nadie.
        var url = ValidateServerUrl(serverUrl, allowInsecureHttp);

        // 2. POST /pair (sin token: es el único endpoint que no lo lleva).
        var request = new PairRequest
        {
            Code = code,
            AgentName = string.IsNullOrWhiteSpace(agentName) ? DefaultAgentName() : agentName.Trim(),
            Platform = "windows",
            AgentVersion = AgentVersion.Current,
            Destinations = [.. _destinations.GetAll().Select(ToDestinationInfo)],
        };

        using var client = new PairingClient(url, _httpClient);
        var response = await client.PairAsync(request, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(response.Token))
        {
            throw new PairingException("server_error", "El servidor no devolvió token de emparejamiento.");
        }

        // 3. Almacén: URL + token CIFRADO juntos (S-01: nunca en JSON plano). El token claro no
        //    vuelve a usarse salvo para el transporte en memoria del paso 4.
        var protectedToken = _protector.Protect(response.Token);
        _store.SetPairing(new PairingInfo
        {
            ServerUrl = client.ServerUrl.ToString(),
            TokenProtected = protectedToken,
            PairedAtUtc = DateTimeOffset.UtcNow,
            LocationId = response.Location?.Id,
            LocationCode = response.Location?.Code,
            LocationName = response.Location?.Name,
            AgentName = request.AgentName,
        });
        _runtime.SetPairPollInterval(response.PollIntervalSeconds);

        // 4. Transporte en memoria + señal al worker: heartbeat y poller arrancan ya (sin reinicio).
        //    El transporte comparte el HttpClient del pair (si hay): en pruebas es el handler falso.
        _connection.SetTransport(client.ServerUrl, response.Token, allowInsecureHttp, _httpClient);
        _waiter.NotifyPaired();

        _logger.LogInformation(
            "Emparejado con {Url} (local «{Location}»). Token cifrado en el almacén local (S-01); el poller arranca sin reiniciar.",
            client.ServerUrl,
            response.Location?.Name ?? response.Location?.Code);

        return new PairOutcome(
            client.ServerUrl.ToString(),
            response.Location?.Id,
            response.Location?.Name,
            response.Location?.Code,
            response.PollIntervalSeconds);
    }

    /// <summary>Valida la URL (S-03): absoluta, http(s) y HTTPS salvo modo desarrollo explícito.</summary>
    public static Uri ValidateServerUrl(string serverUrl, bool allowInsecureHttp)
    {
        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException(
                $"La URL del servidor «{serverUrl}» no es válida. Usa la forma https://tienda.example/wp-json/orp-print/v1",
                nameof(serverUrl));
        }

        if (url.Scheme != Uri.UriSchemeHttps && !allowInsecureHttp)
        {
            throw new ArgumentException(
                "La URL del servidor debe usar HTTPS (S-03, Doc/03 §8). Solo se permite HTTP en desarrollo " +
                "con la bandera explícita --allow-insecure-http (p. ej. wp-env).",
                nameof(serverUrl));
        }

        var builder = new UriBuilder(url);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static DestinationInfo ToDestinationInfo(DestinationOptions destination) => new()
    {
        Role = destination.Role,
        Label = destination.Label,
        PrinterName = destination.PrinterName,
        Copies = destination.Copies,
        Enabled = destination.Enabled,
    };
}
