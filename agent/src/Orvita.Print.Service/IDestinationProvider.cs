using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Proveedor de salidas del agente (Doc/03 §6.7): resuelve el <c>printer_role</c> de un
/// trabajo a su salida física. La interfaz vive aquí: es pieza del Service, no del núcleo
/// portable (ADR-12). La implementación activa es <see cref="StoreDestinationProvider"/>
/// (almacén local SQLite, F2-10); las pruebas usan proveedores estáticos.
/// </summary>
public interface IDestinationProvider
{
    /// <summary>Todas las salidas configuradas (activas e inactivas), para el heartbeat.</summary>
    IReadOnlyList<DestinationOptions> GetAll();

    /// <summary>Salida por rol exacto (un trabajo siempre trae su <c>printer_role</c>); null si no está configurada.</summary>
    DestinationOptions? Resolve(string? role);
}
