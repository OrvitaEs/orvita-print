using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Proveedor de salidas del agente (Doc/03 §6.7): resuelve el <c>printer_role</c> de un
/// trabajo a su salida física. Hoy lee la configuración bootstrap (appsettings, modo
/// desarrollo); F2-10 la sustituye por el almacén SQLite gestionado por la pipe de gestión.
/// La interfaz vive aquí: es pieza del Service, no del núcleo portable.
/// </summary>
public interface IDestinationProvider
{
    /// <summary>Todas las salidas configuradas (activas e inactivas), para el heartbeat.</summary>
    IReadOnlyList<DestinationOptions> GetAll();

    /// <summary>Salida por rol exacto (un trabajo siempre trae su <c>printer_role</c>); null si no está configurada.</summary>
    DestinationOptions? Resolve(string? role);
}

/// <summary>Implementación sobre <see cref="AgentOptions"/> (appsettings bootstrap).</summary>
public sealed class ConfigDestinationProvider(AgentOptions options) : IDestinationProvider
{
    public IReadOnlyList<DestinationOptions> GetAll() => options.Destinations;

    public DestinationOptions? Resolve(string? role)
    {
        var wanted = string.IsNullOrEmpty(role) ? "default" : role;
        return options.Destinations.FirstOrDefault(
            d => string.Equals(d.Role, wanted, StringComparison.Ordinal));
    }
}
