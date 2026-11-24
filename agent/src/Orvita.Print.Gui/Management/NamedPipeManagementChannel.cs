using System.ComponentModel;
using System.IO.Pipes;
using System.Text.Json;
using Orvita.Print.Core;

namespace Orvita.Print.Gui.Management;

/// <summary>
/// Canal concreto sobre la named pipe de gestión (mismo patrón que el cliente del CLI, F2-09):
/// JSON de líneas, una solicitud y una respuesta por conexión, correlación por id. El servicio
/// sirve UNA conexión cada vez, así que la GUI NO mantiene conexiones vivas: conecta, pregunta
/// y se va — igual que el CLI (de lo contrario bloquearía a orvitaprint).
/// </summary>
public sealed class NamedPipeManagementChannel : IManagementChannel
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public NamedPipeManagementChannel(string? pipeName = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? ManagementProtocol.PipeName : pipeName!;
    }

    public string PipeName => _pipeName;

    public void Connect(TimeSpan? timeout = null)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(5);
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect((int)wait.TotalMilliseconds);
        }
        catch (TimeoutException)
        {
            pipe.Dispose();
            throw new ServiceNotRunningException(_pipeName);
        }
        catch (IOException ex)
        {
            pipe.Dispose();
            throw ClassifyIoError(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            pipe.Dispose();
            throw new UnauthorizedAccessException(
                $"Acceso denegado a la pipe «{_pipeName}»: el servicio corre con otra cuenta. " +
                "Lanza la GUI con el mismo usuario del servicio o como administrador.", ex);
        }

        _pipe = pipe;
        _reader = new StreamReader(pipe, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        _writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
    }

    public ManagementResponse Request(string op, object? args = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_pipe is null, this);

        var request = new ManagementRequest
        {
            Id = Random.Shared.Next(1, int.MaxValue),
            Op = op,
            Args = args is null ? null : JsonSerializer.SerializeToElement(args, ManagementProtocol.Options),
        };
        var line = JsonSerializer.Serialize(request, ManagementProtocol.Options);

        ManagementResponse? response;
        try
        {
            _writer!.WriteLine(line);
            _writer.Flush();
            var responseLine = _reader!.ReadLineAsync(cancellationToken).GetAwaiter().GetResult();
            if (responseLine is null)
            {
                throw new IOException("El servicio cerró la conexión sin responder.");
            }

            response = JsonSerializer.Deserialize<ManagementResponse>(responseLine, ManagementProtocol.Options);
        }
        catch (IOException ex) when (ex.Message.Contains("cerró la conexión", StringComparison.Ordinal))
        {
            throw;
        }
        catch (IOException ex)
        {
            throw ClassifyIoError(ex);
        }

        if (response is null)
        {
            throw new IOException("La respuesta del servicio no se entiende (JSON inválido).");
        }

        if (response.Id != request.Id)
        {
            throw new IOException($"La respuesta no corresponde a la solicitud (id {response.Id} ≠ {request.Id}).");
        }

        return response;
    }

    private Exception ClassifyIoError(IOException ex)
    {
        var native = (ex.InnerException as Win32Exception)?.NativeErrorCode
                     ?? (ex.HResult == unchecked((int)0x80070002) ? 2 : 0);
        return native switch
        {
            2 => new ServiceNotRunningException(_pipeName), // ERROR_FILE_NOT_FOUND: nadie escucha
            231 => new IOException($"El servicio está atendiendo otra conexión en «{_pipeName}». Reintenta en unos segundos.", ex),
            5 => new UnauthorizedAccessException(
                $"Acceso denegado a la pipe «{_pipeName}»: el servicio corre con otra cuenta.", ex),
            _ => new IOException($"Error de pipe «{_pipeName}»: {ex.Message}", ex),
        };
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
    }
}
