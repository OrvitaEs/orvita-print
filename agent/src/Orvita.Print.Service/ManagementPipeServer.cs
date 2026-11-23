using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Servidor de la pipe de gestión (F2-08, ADR-13): UN solo servidor de pipe por proceso,
/// hospedado como hosted service del Worker. Protocolo JSON de líneas (una solicitud, una
/// respuesta, correlación por id — clientes secuenciales). Sirve UNA conexión cada vez: un
/// segundo cliente simultáneo obtiene «pipe ocupada» de Windows (el CLI lo traduce); con una
/// conexión atiende tantas solicitudes como mande el cliente antes de cerrar.
///
/// Seguridad: ACL de la pipe — solo el usuario que arranca el servicio, Administradores,
/// LocalSystem y NetworkService tienen acceso (S-05/F3-01). El token JAMÁS viaja por la pipe.
/// </summary>
public sealed class ManagementPipeServer : BackgroundService
{
    /// <summary>Nombre de la pipe (estable y documentado, Doc/03 §6.1: los clientes dependen de él).</summary>
    private readonly string _pipeName;

    private readonly ManagementRequestHandler _handler;
    private readonly ILogger<ManagementPipeServer> _logger;

    public ManagementPipeServer(
        ManagementRequestHandler handler,
        ILogger<ManagementPipeServer> logger,
        string? pipeName = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = logger;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? ManagementProtocol.PipeName : pipeName!;
    }

    /// <summary>Nombre completo de la pipe (\\.\pipe\…), para logs y diagnóstico.</summary>
    public string PipeFullName => $@"\\.\pipe\{_pipeName}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Pipe de gestión a la escucha en {Pipe}.", PipeFullName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServerStream(_pipeName);
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Cliente conectado a la pipe de gestión.");
                await ServeConnectionAsync(server, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Cliente colgado o línea rota (o la pipe ocupada por otra instancia en pruebas):
                // se registra y se vuelve a aceptar — el servicio no cae.
                _logger.LogWarning("La conexión de la pipe de gestión terminó: {Message}", ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                try
                {
                    if (server is { IsConnected: true })
                    {
                        server.Disconnect();
                    }

                    server?.Dispose();
                }
                catch (IOException)
                {
                    // El cliente ya se fue: nada que hacer.
                }
            }
        }

        _logger.LogInformation("Pipe de gestión cerrada.");
    }

    /// <summary>Atiende una conexión: lee solicitudes línea a línea y responde, hasta que el cliente cierre.</summary>
    private async Task ServeConnectionAsync(NamedPipeServerStream server, CancellationToken stoppingToken)
    {
        // leaveOpen: el NamedPipeServerStream lo dispone el bucle de aceptación (finally), no los readers.
        using var reader = new StreamReader(server, System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        await using var writer = new StreamWriter(server, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

        while (!stoppingToken.IsCancellationRequested && server.IsConnected)
        {
            var line = await reader.ReadLineAsync(stoppingToken).ConfigureAwait(false);
            if (line is null)
            {
                break; // el cliente cerró la conexión
            }

            var response = await ProcessLineAsync(line, stoppingToken).ConfigureAwait(false);
            await writer.WriteLineAsync(response).ConfigureAwait(false);
            await writer.FlushAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Procesa UNA línea: parsea, despacha y devuelve la respuesta serializada (nunca lanza).</summary>
    internal async Task<string> ProcessLineAsync(string line, CancellationToken cancellationToken)
    {
        ManagementRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<ManagementRequest>(line, ManagementProtocol.Options);
        }
        catch (JsonException)
        {
            // request queda null: respuesta de error con id 0.
        }

        if (request is null)
        {
            return JsonSerializer.Serialize(
                ManagementResponse.Failure(0, "La solicitud no es un JSON válido del protocolo ({"+"id,op,args"+"}).", "invalid_request"),
                ManagementProtocol.Options);
        }

        var response = await _handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        return JsonSerialize(response);
    }

    private static string JsonSerialize(ManagementResponse response) =>
        JsonSerializer.Serialize(response, ManagementProtocol.Options);

    /// <summary>
    /// Crea el stream del servidor con ACL restrictiva (F2-08): el usuario del servicio,
    /// Administradores, LocalSystem y NetworkService. Sin acceso a «Todos» ni a otros usuarios.
    /// </summary>
    internal static NamedPipeServerStream CreateServerStream(string pipeName)
    {
        var security = new PipeSecurity();

        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        // maxNumberOfServerInstances = 1: un segundo cliente simultáneo recibe ERROR_PIPE_BUSY.
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }
}
