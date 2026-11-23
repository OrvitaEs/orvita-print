using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Proveedor de salidas sobre el almacén local SQLite (F2-10, Doc/03 §6.7): sustituye a la
/// lectura de appsettings. Consulta el almacén EN CADA llamada, de modo que lo añadido o
/// quitado por la pipe (<c>orvitaprint destination …</c>) es visible de inmediato para el
/// procesador de trabajos y el heartbeat, sin reiniciar ni refrescar nada.
/// </summary>
public sealed class StoreDestinationProvider(LocalConfigStore store) : IDestinationProvider
{
    public IReadOnlyList<DestinationOptions> GetAll() => store.ListDestinations();

    public DestinationOptions? Resolve(string? role)
    {
        var wanted = string.IsNullOrEmpty(role) ? "default" : role;
        return store.ListDestinations().FirstOrDefault(
            d => string.Equals(d.Role, wanted, StringComparison.Ordinal));
    }
}
