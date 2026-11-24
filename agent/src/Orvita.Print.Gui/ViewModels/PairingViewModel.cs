using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Orvita.Print.Core;
using Orvita.Print.Gui.Management;
using Orvita.Print.Gui.Models;

namespace Orvita.Print.Gui.ViewModels;

/// <summary>
/// Validación de la URL del servidor en el cliente (S-03, misma regla que el CLI y que
/// PairingService.ValidateServerUrl del servicio): absoluta, http(s), y HTTPS salvo modo
/// desarrollo explícito. La GUI valida ANTES de enviar; el servicio NO confía en nadie.
/// </summary>
public static class ServerUrlValidator
{
    public static Uri? Validate(string? serverUrl, bool allowInsecureHttp)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)
            || !Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        if (url.Scheme != Uri.UriSchemeHttps && !allowInsecureHttp)
        {
            return null;
        }

        var builder = new UriBuilder(url);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }
}

/// <summary>
/// Pestaña «Emparejamiento» (GUI-05, Doc/08 §4.4): estado actual (ServerUrl sin token —
/// S-05: el token JAMÁS viaja por la pipe ni aparece en pantalla), flujo de emparejamiento
/// con el código de 6 dígitos del panel y «Desemparejar» con confirmación (equivale a
/// revocar desde el panel; queda como paso del administrador).
/// </summary>
public partial class PairingViewModel : ObservableObject
{
    private readonly IManagementClient _client;

    [ObservableProperty]
    private bool _paired;

    [ObservableProperty]
    private string _statusText = "Sin emparejar";

    [ObservableProperty]
    private string? _serverUrlInput;

    [ObservableProperty]
    private string? _codeInput;

    [ObservableProperty]
    private bool _allowInsecureHttp;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isError;

    /// <summary>Confirmación de desemparejar inyectada por la vista (diálogo modal).</summary>
    public Func<string, Task<bool>>? ConfirmUnpair { get; set; }

    public PairingViewModel(IManagementClient client)
    {
        _client = client;
    }

    /// <summary>Enriquece el estado con la última instantánea de status (la manda MainViewModel).</summary>
    public void ApplyStatus(StatusSnapshot status)
    {
        Paired = status.Paired;
        if (status.Paired)
        {
            var local = status.LocationName ?? status.LocationCode ?? "(local sin nombre)";
            var lastSeen = status.LastSeenByServer is { } seen
                ? $"Última conexión con la tienda: {seen.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : "Aún sin primera conexión confirmada con la tienda";
            StatusText = $"Emparejado con «{local}» · {status.ServerUrl}\n{lastSeen}";
        }
        else
        {
            StatusText = "Sin emparejar: pega la URL de tu tienda y el código de 6 dígitos del panel.";
        }
    }

    /// <summary>Empareja (op pair): valida URL (S-03) y código, manda por la pipe. No lanza.</summary>
    public async Task PairAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var url = ServerUrlValidator.Validate(ServerUrlInput, AllowInsecureHttp);
        if (url is null)
        {
            IsError = true;
            Message = AllowInsecureHttp
                ? "La URL del servidor no es válida. Usa la forma https://tienda.example/wp-json/orp-print/v1"
                : "La URL del servidor debe usar HTTPS (S-03). Solo en desarrollo, activa «HTTP sin cifrar».";
            return;
        }

        var code = CodeInput?.Trim() ?? string.Empty;
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            IsError = true;
            Message = "El código de emparejamiento son 6 dígitos (lo genera el panel del plugin).";
            return;
        }

        IsBusy = true;
        Message = null;
        try
        {
            ManagementResponse response = await Task.Run(() =>
                _client.Send(ManagementProtocol.Operations.Pair, new
                {
                    server = url.ToString(),
                    code,
                    allow_insecure_http = AllowInsecureHttp,
                })).ConfigureAwait(true);

            if (!response.Ok || response.Data is not { ValueKind: JsonValueKind.Object } data)
            {
                IsError = true;
                Message = GuiFriendlyPairError(response.Error) ?? "El emparejamiento falló.";
                return;
            }

            var location = data.TryGetProperty("location", out var loc) ? loc : default;
            var name = location.ValueKind == JsonValueKind.Object ? StatusSnapshot.GetString(location, "name") : null;
            var locationCode = location.ValueKind == JsonValueKind.Object ? StatusSnapshot.GetString(location, "code") : null;
            IsError = false;
            Message = $"Emparejado con el local «{name ?? locationCode ?? url.Host}». El agente ya imprime los pedidos de este local.";
            CodeInput = null;
            Paired = true;
        }
        catch (Exception ex) when (MainViewModel.IsPipeFault(ex))
        {
            IsError = true;
            Message = "El servicio Orvita Print no está en ejecución: arranca el servicio y reintenta.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// El error S-03 del servicio habla de la bandera del CLI («--allow-insecure-http»), que
    /// en la GUI no existe: ahí la bandera de desarrollo ES la casilla «Permitir HTTP sin
    /// cifrar». Se reescribe SOLO ese mensaje; el resto de errores del servicio pasan tal
    /// cual (Doc/04: los códigos contractuales no se traducen, este es solo texto de ayuda).
    /// </summary>
    public static string? GuiFriendlyPairError(string? error) =>
        error is null
            ? null
            : error.Contains("--allow-insecure-http")
                ? "La URL del servidor debe usar HTTPS (S-03). Solo se permite HTTP en desarrollo: " +
                  "marca la casilla «Permitir HTTP sin cifrar» para emparejar con http://."
                : error;

    /// <summary>Desempareja (op pair.remove, GUI-05): para el poller y borra URL+token+local.</summary>
    public async Task UnpairAsync()
    {
        if (!Paired || ConfirmUnpair is null)
        {
            return;
        }

        if (!await ConfirmUnpair(
            "¿Desemparejar este equipo? El agente dejará de recibir pedidos y el token se borra de este " +
            "equipo — equivale a revocar desde el panel, que el administrador deberá confirmar después.").ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            ManagementResponse response = await Task.Run(() =>
                _client.Send(ManagementProtocol.Operations.PairRemove)).ConfigureAwait(true);

            IsError = !response.Ok;
            Message = response.Ok
                ? "Agente desemparejado: ya no recibe pedidos. Para volver, empareja de nuevo con un código nuevo del panel."
                : response.Error ?? "No se pudo desemparejar.";
            if (response.Ok)
            {
                Paired = false;
                StatusText = "Sin emparejar";
            }
        }
        catch (Exception ex) when (MainViewModel.IsPipeFault(ex))
        {
            IsError = true;
            Message = "El servicio Orvita Print no está en ejecución.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
