using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Estado de la licencia del agente (F3-02, Doc/03 §10), cargado UNA VEZ al arrancar desde
/// <c>%ProgramData%\OrvitaPrint\license.dat</c> y validado LOCALMENTE con la clave pública
/// EMBEBIDA (<see cref="LicenseTrust"/>) — sin conexión NUNCA.
///
/// <para><b>Política V1 para el piloto (ratificable, decisión documentada en Doc/07):</b></para>
/// <list type="bullet">
/// <item><b>Sin fichero de licencia</b> (pizzería piloto, no ha pagado): el agente FUNCIONA —
/// modo «sin licencia» equivalente al SKU de 1 local — y el estado lo dice claro en
/// <c>status</c>/<c>doctor</c> para que el soporte lo vea. No bloquea emparejamiento ni impresión.</item>
/// <item><b>Licencia válida:</b> informa sku, max_locations, updates_until y cliente.</item>
/// <item><b><c>updates_until</c> pasado:</b> la licencia SIGUE siendo válida (el agente funciona)
/// y SOLO deja de aceptar actualizaciones automáticas (lo consulta <see cref="UpdateService"/>).</item>
/// <item><b>Fichero corrupto o firma inválida:</b> se registra el error con su código y el agente
/// sigue funcionando en el estado «invalid» (nunca se bloquea el servicio por un fichero de
/// licencia; la aplicación comercial de la licencia vive en el servidor).</item>
/// </list>
///
/// <para>La lectura es UNA SOLA en el arranque (cambiar de licencia = copiar la nueva y reiniciar
/// el servicio, documentado en la guía). Snapshot inmutable después de cargar: hilo-seguro por
/// construcción. Nunca lanza: cualquier problema queda reflejado en el estado.</para>
/// </summary>
public sealed class LicenseState
{
    /// <summary>Estado de la licencia: missing · valid · expired_updates · invalid.</summary>
    public string State { get; }

    public string? LicenseId { get; }
    public string? Sku { get; }
    public int? MaxLocations { get; }
    public string? UpdatesUntil { get; }
    public string? Customer { get; }
    public bool UpdatesExpired { get; }

    /// <summary>Error de validación con su código (license_invalid_format/license_signature_invalid) o null.</summary>
    public string? Error { get; }

    public string FilePath { get; }

    /// <summary>Constructor público: las pruebas construyen estados directamente; en producción se usa <see cref="Load"/>.</summary>
    public LicenseState(string filePath, string state, string? licenseId = null, string? sku = null,
        int? maxLocations = null, string? updatesUntil = null, string? customer = null,
        bool updatesExpired = false, string? error = null)
    {
        FilePath = filePath;
        State = state;
        LicenseId = licenseId;
        Sku = sku;
        MaxLocations = maxLocations;
        UpdatesUntil = updatesUntil;
        Customer = customer;
        UpdatesExpired = updatesExpired;
        Error = error;
    }

    /// <summary>
    /// Carga y verifica la licencia. La clave pública de verificación es la EMBEBIDA
    /// (<see cref="LicenseTrust"/>); el parámetro <paramref name="publicKey"/> existe para las
    /// pruebas (par efímero) — en producción NO se pasa.
    /// </summary>
    public static LicenseState Load(string filePath, DateTimeOffset nowUtc, ILogger? logger = null, ECDsa? publicKey = null)
    {
        if (!File.Exists(filePath))
        {
            logger?.LogInformation(
                "Sin fichero de licencia en {Path}: el agente funciona en modo «sin licencia» " +
                "(equivalente a 1 local, Doc/03 §10). Copia license.dat y reinicia para activarla.",
                filePath);
            return new LicenseState(filePath, "missing");
        }

        ECDsa? owned = null;
        try
        {
            var key = publicKey ?? (owned = LicenseTrust.CreatePublicKey());
            var json = File.ReadAllText(filePath);
            var license = LicenseDocument.ParseAndVerify(json, key);

            var expired = license.UpdatesExpired(nowUtc);
            var state = expired ? "expired_updates" : "valid";
            logger?.LogInformation(
                "Licencia válida ({LicenseId}): sku {Sku}, max_locations {Max}, updates_until {Until}{Expired}. " +
                "Cliente: {Customer}.",
                license.LicenseId, license.Sku, license.MaxLocations, license.UpdatesUntil,
                expired ? " — CADUCADA para actualizaciones (el agente sigue funcionando, Doc/03 §10)" : "",
                license.Customer);

            return new LicenseState(
                filePath, state,
                licenseId: license.LicenseId,
                sku: license.Sku,
                maxLocations: license.MaxLocations,
                updatesUntil: license.UpdatesUntil,
                customer: license.Customer,
                updatesExpired: expired);
        }
        catch (LicenseException ex)
        {
            logger?.LogWarning(
                "La licencia en {Path} NO es válida ({Code}): {Message}. El agente sigue funcionando " +
                "en modo «sin licencia» (Doc/03 §10).",
                filePath, ex.Code, ex.Message);
            return new LicenseState(filePath, "invalid", error: $"{ex.Code}: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                "No se pudo leer la licencia en {Path}: {Message}. El agente sigue funcionando en modo «sin licencia».",
                filePath, ex.Message);
            return new LicenseState(filePath, "invalid", error: $"license_read_failed: {ex.Message}");
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>Objeto para <c>status</c>/<c>doctor</c> y el diagnostics.json. Sin secretos (S-05): la licencia no contiene ninguno.</summary>
    public object ToWire() => new
    {
        state = State,
        license_id = LicenseId,
        sku = Sku,
        max_locations = MaxLocations,
        updates_until = UpdatesUntil,
        customer = Customer,
        updates_expired = UpdatesExpired,
        file = FilePath,
        error = Error,
        note = State switch
        {
            "missing" => "Sin licencia: el agente funciona con 1 local. Copia license.dat a la ruta indicada y reinicia el servicio.",
            "invalid" => "Licencia NO válida: el agente funciona con 1 local. Comprueba el fichero con orvitaprint doctor.",
            "expired_updates" => "Licencia válida; actualizaciones automáticas desactivadas al pasar updates_until (el agente sigue funcionando, Doc/03 §10).",
            _ => null,
        },
    };

    /// <summary>El modo «sin licencia»/inválido equivale al SKU de 1 local (política V1, piloto).</summary>
    public int EffectiveMaxLocations => MaxLocations ?? 1;

    /// <summary>Serialización de diagnóstico: JSON estable para las pruebas.</summary>
    public string ToJson() => JsonSerializer.Serialize(ToWire(), ManagementProtocol.Options);
}
