using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Orvita.Print.Contracts;

namespace Orvita.Print.Core;

/// <summary>
/// Transporte directo contra el WordPress del cliente (Doc/03 §7). Autenticación
/// <c>Authorization: Bearer</c>, HTTPS obligatorio (S-03) y lectura de los códigos de
/// error del contrato (Doc/04 §6) en sus dos formas: cuerpo literal (409/410) y
/// envoltorio estándar de WordPress (401/429).
/// </summary>
public sealed class DirectTransport : ITransport, IDisposable
{
    /// <summary>Límite del servidor: Doc/04 §5.4, "limit" máximo 20.</summary>
    public const int MaxFetchLimit = 20;

    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly int _fetchLimit;

    public DirectTransport(Uri serverUrl, string token, HttpClient? httpClient = null, bool allowInsecureHttp = false, int fetchLimit = MaxFetchLimit)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var scheme = serverUrl.Scheme;
        if (scheme != Uri.UriSchemeHttps && scheme != Uri.UriSchemeHttp)
            throw new ArgumentException($"Esquema no soportado «{scheme}»: usa https:// (o http:// solo en desarrollo).", nameof(serverUrl));

        if (scheme != Uri.UriSchemeHttps && !allowInsecureHttp)
            throw new ArgumentException(
                "La URL del servidor debe usar HTTPS (S-03, Doc/03 §8). Para wp-env u otras pruebas de desarrollo " +
                "pasa allowInsecureHttp: true explícitamente.",
                nameof(serverUrl));

        var builder = new UriBuilder(serverUrl);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        _baseUri = builder.Uri;
        _fetchLimit = Math.Clamp(fetchLimit, 1, MaxFetchLimit);

        if (httpClient is null)
        {
            _http = new HttpClient
            {
                // El servidor puede tardar (hosting compartido); no cortamos a los 100 s por defecto.
                Timeout = TimeSpan.FromSeconds(60),
            };
            _ownsHttpClient = true;
        }
        else
        {
            _http = httpClient;
        }

        _http.DefaultRequestHeaders.Authorization ??= new AuthenticationHeaderValue("Bearer", token);
    }

    public Uri BaseUrl => _baseUri;

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<JobBatch> FetchAsync(Cursor cursor, CancellationToken ct)
    {
        var url = new Uri(_baseUri, $"jobs?after={cursor.Value}&limit={_fetchLimit}");
        using var response = await SendAsync(HttpMethod.Get, url, content: null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowForErrorAsync(response, ct, $"GET /jobs?after={cursor.Value}").ConfigureAwait(false);
        }

        return await ReadAsync<JobBatch>(response, ct).ConfigureAwait(false) ?? new JobBatch { Cursor = cursor.Value };
    }

    /// <inheritdoc />
    public async Task<Job?> ClaimAsync(long jobId, CancellationToken ct)
    {
        var url = new Uri(_baseUri, $"jobs/{jobId}/claim");
        using var response = await SendAsync(HttpMethod.Post, url, CreateEmptyContent(), ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return await ReadAsync<Job>(response, ct).ConfigureAwait(false);
        }

        // 409 «otro lo cogió» (cuerpo literal {"error":"orp_already_claimed"}),
        // 410 caducado, 404 ajeno: en los tres casos no hay trabajo que imprimir.
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            return null;
        }

        await ThrowForErrorAsync(response, ct, $"POST /jobs/{jobId}/claim").ConfigureAwait(false);
        return null;
    }

    /// <inheritdoc />
    public async Task AckAsync(long jobId, JobResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        var url = new Uri(_baseUri, $"jobs/{jobId}/ack");
        using var response = await SendAsync(HttpMethod.Post, url, CreateJsonContent(result), ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // El trabajo ya está terminal (expiró, fue cancelado o expiró mientras imprimíamos):
        // no hay nada que hacer, pero tampoco es un error del poller.
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            return;
        }

        await ThrowForErrorAsync(response, ct, $"POST /jobs/{jobId}/ack").ConfigureAwait(false);
    }

    /// <summary>GET /health (Doc/04 §5.1): diagnóstico y comprobación de compatibilidad de api_version.</summary>
    public async Task<HealthInfo> GetHealthAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, new Uri(_baseUri, "health"), content: null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowForErrorAsync(response, ct, "GET /health").ConfigureAwait(false);
        }

        return await ReadAsync<HealthInfo>(response, ct).ConfigureAwait(false)
            ?? throw new TransportProtocolException(response.StatusCode, ErrorCode.Unknown, "GET /health devolvió un cuerpo vacío.");
    }

    /// <summary>POST /heartbeat (Doc/04 §5.3): versión, pendientes locales y salidas (upsert por role).</summary>
    public async Task<HeartbeatResponse> SendHeartbeatAsync(HeartbeatRequest heartbeat, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        using var response = await SendAsync(HttpMethod.Post, new Uri(_baseUri, "heartbeat"), CreateJsonContent(heartbeat), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowForErrorAsync(response, ct, "POST /heartbeat").ConfigureAwait(false);
        }

        return await ReadAsync<HeartbeatResponse>(response, ct).ConfigureAwait(false)
            ?? throw new TransportProtocolException(response.StatusCode, ErrorCode.Unknown, "POST /heartbeat devolvió un cuerpo vacío.");
    }

    /// <summary>GET /config (Doc/04 §5.8): configuración común del documento; re-sincroniza cuando cambia config_version.</summary>
    public async Task<AgentConfig> GetConfigAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, new Uri(_baseUri, "config"), content: null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowForErrorAsync(response, ct, "GET /config").ConfigureAwait(false);
        }

        return await ReadAsync<AgentConfig>(response, ct).ConfigureAwait(false)
            ?? throw new TransportProtocolException(response.StatusCode, ErrorCode.Unknown, "GET /config devolvió un cuerpo vacío.");
    }

    // ---------------------------------------------------------------- internals

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TransportNetworkException($"No se pudo contactar con {_baseUri.Host}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TransportNetworkException("Tiempo de espera agotado al contactar con el servidor.", ex);
        }
    }

    private async Task ThrowForErrorAsync(HttpResponseMessage response, CancellationToken ct, string context)
    {
        var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        var envelope = ErrorEnvelope.Parse(body);
        var wireCode = envelope?.PrimaryCode;
        var code = ErrorCodes.TryFromWire(wireCode) ?? ErrorCode.Unknown;
        var message = BuildErrorMessage(context, response.StatusCode, wireCode, envelope, body);

        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                // Cualquier 401 detiene el poller (Doc/03 §6.3); si el cuerpo no trae
                // código reconocible asumimos token inválido.
                var stopCode = code is ErrorCode.OrpInvalidToken or ErrorCode.OrpTokenRevoked
                    ? code
                    : ErrorCode.OrpInvalidToken;
                throw new TransportUnauthorizedException(stopCode, message);

            case HttpStatusCode.TooManyRequests:
                throw new TransportRateLimitedException(
                    code,
                    message,
                    ParseRetryAfter(response));

            default:
                throw new TransportProtocolException(response.StatusCode, code, message);
        }
    }

    private static string BuildErrorMessage(string context, HttpStatusCode status, string? wireCode, ErrorEnvelope? envelope, string body)
    {
        var detail = envelope?.Message;
        if (string.IsNullOrWhiteSpace(detail) && !string.IsNullOrWhiteSpace(body) && body.Length < 300)
        {
            detail = body;
        }

        return $"[{context}] {(int)status} {status}" +
               (wireCode is null ? "" : $" ({wireCode})") +
               (string.IsNullOrWhiteSpace(detail) ? "" : $": {detail}");
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta;
        }

        if (header.Date is { } date)
        {
            var until = date.Date - DateTimeOffset.UtcNow;
            return until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }

        return null;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, OrpJson.Default, ct).ConfigureAwait(false);
    }

    private static HttpContent CreateEmptyContent() =>
        new StringContent(string.Empty, Encoding.UTF8, "application/json");

    private static HttpContent CreateJsonContent<T>(T value) =>
        new StringContent(JsonSerializer.Serialize(value, OrpJson.Default), Encoding.UTF8, "application/json");
}
