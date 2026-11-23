using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Orvita.Print.Contracts;

namespace Orvita.Print.Core;

/// <summary>Error contractual del emparejamiento (Doc/04 §5.2 y §6).</summary>
public sealed class PairingException : Exception
{
    /// <summary>Código del contrato: orp_pairing_invalid, orp_pairing_expired, orp_pairing_locked…</summary>
    public string Code { get; }

    /// <summary>Código HTTP de la respuesta (0 si no hubo respuesta: red/DNS).</summary>
    public int StatusCode { get; }

    public PairingException(string code, string message, int statusCode = 0) : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }
}

/// <summary>
/// Cliente de <c>POST /pair</c> (Doc/04 §5.2): código de 6 dígitos + nombre del agente +
/// salidas ya configuradas; devuelve el token (que se muestra UNA sola vez), el local y el
/// intervalo de poll. NO usa el token Bearer (aún no existe): es el único endpoint sin él.
/// Los errores 400/410/429 se traducen a <see cref="PairingException"/> con su código del
/// contrato (Doc/04 §6); 401/404/5xx → server_error. La llamada vive en Core (portable).
/// </summary>
public sealed class PairingClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <param name="serverUrl">URL base del servidor (la misma que usará DirectTransport tras emparejar).</param>
    /// <param name="httpClient">Opcional (pruebas); si falta se crea uno propio (timeout 30 s).</param>
    public PairingClient(Uri serverUrl, HttpClient? httpClient = null)
    {
        ServerUrl = serverUrl ?? throw new ArgumentNullException(nameof(serverUrl));
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _ownsHttpClient = true;
        }
        else
        {
            _http = httpClient;
        }
    }

    /// <summary>URL base contra la que se empareja (con barra final, como DirectTransport).</summary>
    public Uri ServerUrl { get; }

    /// <summary>Envía la solicitud de emparejamiento. Lanza <see cref="PairingException"/> ante error contractual.</summary>
    public async Task<PairResponse> PairAsync(PairRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = new Uri(ServerUrl, "pair");
        using var content = new StringContent(
            JsonSerializer.Serialize(request, OrpJson.Default),
            Encoding.UTF8,
            "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            var paired = await response.Content.ReadFromJsonAsync<PairResponse>(OrpJson.Default, cancellationToken)
                .ConfigureAwait(false);
            if (paired is null || string.IsNullOrWhiteSpace(paired.Token))
            {
                throw new PairingException(
                    "server_error",
                    "El servidor no devolvió token de emparejamiento (el token se muestra UNA sola vez, Doc/04 §5.2).",
                    (int)response.StatusCode);
            }

            return paired;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var (code, message) = ParseError(body);
        throw new PairingException(code, message, (int)response.StatusCode);
    }

    /// <summary>Extrae el código de error del contrato en sus DOS formas (Doc/04 §5.2/§6): literal ({"error":…}) y envoltorio WP.</summary>
    private static (string Code, string Message) ParseError(string body)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body, OrpJson.Default);
            if (json.ValueKind == JsonValueKind.Object)
            {
                if (json.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                {
                    return (error.GetString()!, Describe(error.GetString()!));
                }

                if (json.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String &&
                    json.TryGetProperty("message", out var message))
                {
                    return (code.GetString()!, message.GetString() ?? Describe(code.GetString()!));
                }
            }
        }
        catch (JsonException)
        {
            // Cuerpo no JSON: mensaje genérico abajo.
        }

        return ("server_error", "El servidor rechazó el emparejamiento con una respuesta no estándar.");
    }

    private static string Describe(string code) => code switch
    {
        "orp_pairing_invalid" => "El código de emparejamiento no es válido (revisado y erróneo). Te quedan intentos; genera uno nuevo en el panel si falla de nuevo.",
        "orp_pairing_expired" => "El código de emparejamiento ha caducado (10 minutos, Doc/03 §8). Genera uno nuevo en el panel.",
        "orp_pairing_locked" => "El emparejamiento está bloqueado por demasiados intentos. Espera unos minutos o desbloquea desde el panel.",
        _ => "El servidor rechazó el emparejamiento.",
    };

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
        else
        {
            // HttpClient prestado (pruebas): no se toca.
        }
    }
}
