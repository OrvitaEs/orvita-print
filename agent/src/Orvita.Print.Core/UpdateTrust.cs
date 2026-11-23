namespace Orvita.Print.Core;

/// <summary>
/// Confianza de actualización (F2-11): la clave pública Ed25519 que firma los manifiestos va
/// EMBEBIDA en el binario del agente — nunca en el manifiesto ni en configuración editable
/// (appsettings/almacén local): quien pudiera cambiarla invalidaría toda la cadena (Doc/03 §8:
/// mismo principio que la clave pública de licencia).
///
/// <para><b>CLAVE DE RELEASE (F3-03):</b> par real generado con <c>orvita-release genkey
/// --type ed25519</c> (F3-02). La SEMILLA PRIVADA vive fuera del repositorio y del servidor, con
/// copia de respaldo offline — ubicación documentada en
/// <c>agent/tools/Orvita.Print.ReleaseTools/README.md</c>. Toda firma de manifiestos de
/// actualización se hace con ella (F3-03, <c>genmanifest</c>); si se pierde, no se pueden
/// publicar updates y hay que rotar la clave embebida.</para>
/// </summary>
public static class UpdateTrust
{
    /// <summary>Clave pública Ed25519 de los manifiestos <c>latest.json</c>, 32 bytes en crudo, base64.</summary>
    public const string ReleasePublicKeyBase64 = "36eHq7nPgCs7Euprcdd2DQAv0r3OA38rv3yzUmCoF6c=";
}
