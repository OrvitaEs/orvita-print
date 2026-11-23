using System.IO.Pipes;
using System.Text.Json;
using Orvita.Print.Core;

namespace Orvita.Print.Cli;

/// <summary>No hay servidor escuchando en la pipe: el servicio Orvita Print no está en ejecución.</summary>
public sealed class ServiceNotRunningException(string pipeName) : Exception(
    $"El servicio Orvita Print no está en ejecución: nadie escucha en la pipe «{pipeName}». " +
    "Arranca el servicio de Orvita Print (o instálalo: es el requisito de F3-01).");

/// <summary>La pipe existe pero está ocupada (el servicio atiende otra conexión): reintenta en unos segundos.</summary>
public sealed class ServiceBusyException(string pipeName) : Exception(
    $"El servicio está atendiendo otra conexión en «{pipeName}». El CLI es secuencial: reintenta en unos segundos.");

/// <summary>
/// Cliente de la pipe de gestión (F2-09): JSON de líneas con <see cref="ManagementProtocol"/>
/// (Core). Una conexión por comando (el CLI es one-shot); el servicio sirve UNA conexión a la
/// vez. El CLI NUNCA lee el almacén local: status/pair/etc. responden por la pipe y el token
/// JAMÁS viaja de vuelta (S-05).
/// </summary>
public sealed class ManagementPipeClient : IDisposable
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public ManagementPipeClient(string? pipeName = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? ManagementProtocol.PipeName : pipeName!;
    }

    public string PipeName => _pipeName;

    /// <summary>Conecta al servicio; lanza <see cref="ServiceNotRunningException"/> o <see cref="ServiceBusyException"/>.</summary>
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
                "Lanza el CLI con el mismo usuario del servicio o como administrador.", ex);
        }

        _pipe = pipe;
        _reader = new StreamReader(pipe, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        _writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
    }

    /// <summary>Envía una operación y espera su respuesta correlacionada. Lanza en error de transporte.</summary>
    public ManagementResponse Request(string op, object? args = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_pipe is null, this);

        var request = new ManagementRequest
        {
            Id = Random.Shared.Next(1, int.MaxValue),
            Op = op,
            Args = args is null
                ? null
                : JsonSerializer.SerializeToElement(args, ManagementProtocol.Options),
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
            throw; // mensaje claro tal cual
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

    /// <summary>Mapea los errores Win32 de la pipe a excepciones claras para el operador.</summary>
    private Exception ClassifyIoError(IOException ex)
    {
        var native = (ex.InnerException as System.ComponentModel.Win32Exception)?.NativeErrorCode
                     ?? (ex.HResult == unchecked((int)0x80070002) ? 2 : 0);
        return native switch
        {
            2 => new ServiceNotRunningException(_pipeName),      // ERROR_FILE_NOT_FOUND: nadie escucha
            231 => new ServiceBusyException(_pipeName),          // ERROR_PIPE_BUSY
            5 => new UnauthorizedAccessException(
                $"Acceso denegado a la pipe «{_pipeName}»: lanza el CLI con el usuario del servicio o como administrador.", ex),
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
