using Orvita.Print.Contracts;
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

/// <summary>
/// Mapeo DestinationOptions → DestinationInfo (Doc/04 §5.2/§5.3) compartido por /pair y
/// /heartbeat. El contrato exige <c>printer_name</c> NO vacío: el plugin descarta en silencio
/// toda salida sin él (<c>DestinationRepository::upsert_many</c>, incidente 2026-11-23 — la
/// tabla del servidor quedaba vacía sin rastro). Una salida TCP 9100 configurada solo con
/// <c>--address</c> no tiene impresora: se reporta la dirección (host:puerto) y, como último
/// recurso, el propio rol — siempre visible en el servidor, nunca descartada sin rastro.
/// El <c>kind</c> y la <c>address</c> completas NO viajan: no existen en el contrato v1;
/// el panel muestra lo que le llega y <c>orvitaprint status</c> la verdad local.
/// </summary>
public static class DestinationWire
{
    public static DestinationInfo From(DestinationOptions destination, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var printerName = destination.PrinterName;
        if (string.IsNullOrWhiteSpace(printerName))
        {
            printerName = string.IsNullOrWhiteSpace(destination.Address)
                ? destination.Role
                : destination.Address;

            warn?.Invoke(
                $"La salida «{destination.Role}» no tiene impresora; se reporta «{printerName}» al servidor " +
                "(el contrato Doc/04 §5.3 exige printer_name y el plugin descarta en silencio las salidas sin él).");
        }

        return new DestinationInfo
        {
            Role = destination.Role,
            Label = destination.Label,
            PrinterName = printerName,
            Copies = destination.Copies,
            Enabled = destination.Enabled,
        };
    }
}
