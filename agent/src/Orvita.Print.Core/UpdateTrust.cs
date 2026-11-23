namespace Orvita.Print.Core;

/// <summary>
/// Confianza de actualización (F2-11): la clave pública Ed25519 que firma los manifiestos va
/// EMBEBIDA en el binario del agente — nunca en el manifiesto ni en configuración editable
/// (appsettings/almacén local): quien pudiera cambiarla invalidaría toda la cadena (Doc/03 §8:
/// mismo principio que la clave pública de licencia).
///
/// <para><b>SOLO DESARROLLO:</b> esta clave se generó para F2-11 y la MITAD PRIVADA se descartó
/// (no existe en ningún sitio). No hay firma válida posible hasta que F3-03 genere el par de
/// release — privada fuera del servidor y del repositorio — y reemplace este valor. El canal
/// de actualización, además, está desactivado por defecto (Update.ManifestUrl vacío).</para>
/// </summary>
public static class UpdateTrust
{
    /// <summary>Clave pública Ed25519 de los manifiestos <c>latest.json</c>, 32 bytes en crudo, base64.</summary>
    public const string ReleasePublicKeyBase64 = "xc3hv8J5U1VzXFPVV6UUJZwX5g73ev0KvgfLc+QfurM=";
}
