using Orvita.Print.Core;

namespace Orvita.Print.Gui.Management;

/// <summary>
/// Cliente de la pipe que usan los viewmodels (GUI-01): cada operación es una conexión nueva
/// (conecta → pregunta → cierra), igual que el CLI — el servicio atiende UNA conexión cada
/// vez y una conexión viva de la GUI bloquearía a <c>orvitaprint</c> (Doc/03 §6.1). Todo por
/// aquí; la GUI no abre la SQLite ni lee appsettings (regla de hierro de Doc/08 §1).
/// </summary>
public interface IManagementClient
{
    /// <summary>
    /// Ejecuta una operación. Si el servicio no responde, lanza
    /// <see cref="ServiceNotRunningException"/> (el llamador decide cómo mostrarlo).
    /// </summary>
    ManagementResponse Send(string op, object? args = null, CancellationToken cancellationToken = default);

    /// <summary>Ejecuta una operación y devuelve null si el servicio no está (errores de pipe en silencio).</summary>
    ManagementResponse? TrySend(string op, object? args = null, CancellationToken cancellationToken = default);
}

public sealed class ManagementGuiClient(Func<IManagementChannel>? channelFactory = null) : IManagementClient
{
    private readonly Func<IManagementChannel> _channelFactory = channelFactory ?? (() => new NamedPipeManagementChannel());

    public ManagementResponse Send(string op, object? args = null, CancellationToken cancellationToken = default)
    {
        using var channel = _channelFactory();
        channel.Connect();
        return channel.Request(op, args, cancellationToken);
    }

    public ManagementResponse? TrySend(string op, object? args = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return Send(op, args, cancellationToken);
        }
        catch (ServiceNotRunningException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return null;
        }
    }
}
