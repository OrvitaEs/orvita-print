using Orvita.Print.Core;

namespace Orvita.Print.Gui.Management;

/// <summary>
/// Canal de transporte con el servicio para el protocolo de gestión JSON (Doc/08 §2, ADR-13):
/// en V1 es la named pipe «orvitaprint-management»; un futuro Linux/macOS será un Unix domain
/// socket con el MISMO protocolo (~1 día de adaptación). El contrato es el del CLI:
/// conectar, enviar «{id, op, args}», recibir «{id, ok, data|error, code}».
/// </summary>
public interface IManagementChannel : IDisposable
{
    /// <summary>Conecta al servicio o lanza. <see cref="ServiceNotRunningException"/> si nadie escucha.</summary>
    void Connect(TimeSpan? timeout = null);

    /// <summary>Envía una operación y espera su respuesta correlacionada.</summary>
    ManagementResponse Request(string op, object? args = null, CancellationToken cancellationToken = default);
}

/// <summary>Nadie escucha en la pipe: el servicio Orvita Print no está en ejecución (texto de Doc/08 §4.1).</summary>
public sealed class ServiceNotRunningException(string pipeName) : Exception(
    $"El servicio Orvita Print no está en ejecución: nadie escucha en la pipe «{pipeName}». " +
    "Arranca el servicio OrvitaPrint (o instálalo con el MSI).");
