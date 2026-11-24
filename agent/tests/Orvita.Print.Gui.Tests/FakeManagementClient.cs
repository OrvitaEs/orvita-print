using System.Text.Json;
using Orvita.Print.Core;
using Orvita.Print.Gui.Management;

namespace Orvita.Print.Gui.Tests;

/// <summary>
/// Doble de la pipe para las pruebas de viewmodels (GUI-08): graba las operaciones que pide
/// la GUI y responde con JSON precocinado — SIN servicio real ni pipe real.
/// </summary>
public sealed class FakeManagementClient : IManagementClient
{
    /// <summary>Respuestas por op: la última registrada gana (las pruebas las sobreescriben).</summary>
    public Dictionary<string, Func<JsonElement?, ManagementResponse>> Handlers { get; } = new();

    /// <summary>Operaciones recibidas, en orden, con sus argumentos (para asertar qué pidió la GUI).</summary>
    public List<(string Op, JsonElement? Args)> Requests { get; } = [];

    /// <summary>Excepción a lanzar en la siguiente llamada (p. ej. ServiceNotRunningException).</summary>
    public Exception? ThrowOnNextSend { get; set; }

    public ManagementResponse Send(string op, object? args = null, CancellationToken cancellationToken = default)
    {
        if (ThrowOnNextSend is { } error)
        {
            ThrowOnNextSend = null;
            throw error;
        }

        JsonElement? element = args switch
        {
            null => null,
            _ => JsonSerializer.SerializeToElement(args, ManagementProtocol.Options),
        };
        Requests.Add((op, element));
        return Handlers.TryGetValue(op, out var handler)
            ? handler(element)
            : ManagementResponse.Success(1, new { });
    }

    public ManagementResponse? TrySend(string op, object? args = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return Send(op, args, cancellationToken);
        }
        catch (Exception ex) when (ex is ServiceNotRunningException or IOException)
        {
            return null;
        }
    }

    /// <summary>Registra una respuesta <c>ok</c> con data serializable por el protocolo.</summary>
    public void RespondOk(string op, object data) =>
        Handlers[op] = _ => ManagementResponse.Success(1, data);

    public void RespondError(string op, string error, string code) =>
        Handlers[op] = _ => ManagementResponse.Failure(1, error, code);

    public void ResetRequests() => Requests.Clear();
}
