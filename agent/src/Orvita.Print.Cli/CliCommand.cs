using System.Text.Json;

namespace Orvita.Print.Cli;

/// <summary>Comandos que entiende <c>orvitaprint</c> (F2-09). El parser los produce sin tocar la pipe.</summary>
public abstract record CliCommand;

public sealed record PairCommand(string ServerUrl, string Code, string? AgentName, bool AllowInsecureHttp) : CliCommand;

public sealed record DestinationAddCommand(
    string Role,
    string? Label,
    string? PrinterName,
    int Copies,
    bool Enabled,
    string Kind,
    string? Address) : CliCommand;

public sealed record DestinationListCommand : CliCommand;

public sealed record DestinationRemoveCommand(string Role) : CliCommand;

public sealed record StatusCommand : CliCommand;

public sealed record TestCommand(string? Role) : CliCommand;

public sealed record DoctorCommand : CliCommand;

public sealed record RestartPollerCommand : CliCommand;

/// <summary>Resultado del parseo: comando, error de uso (con código 2), texto de ayuda o nombre de pipe alternativo.</summary>
public sealed record ParseOutcome(
    CliCommand? Command,
    string? Error,
    string? Help,
    string? PipeName)
{
    public static ParseOutcome Ok(CliCommand command, string? pipeName) => new(command, null, null, pipeName);

    public static ParseOutcome UsageError(string error, string? pipeName = null) => new(null, error, null, pipeName);

    public static ParseOutcome ShowHelp(string help, string? pipeName = null) => new(null, null, help, pipeName);
}

/// <summary>Códigos de salida para scripts (Doc/07 F2-09): 0 ok · 1 fallo · 2 uso · 3 servicio parado.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int ServiceNotRunning = 3;
}
