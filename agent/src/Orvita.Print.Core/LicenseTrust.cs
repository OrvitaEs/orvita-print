using System.Security.Cryptography;

namespace Orvita.Print.Core;

/// <summary>
/// Confianza de licencia (F3-02, Doc/03 §8): la clave pública ECDSA P-256 que firma los ficheros
/// <c>license.dat</c> va EMBEBIDA en el binario del agente — nunca en el almacén local ni en
/// configuración editable: quien pudiera cambiarla invalidaría toda la cadena. Mismo principio
/// que la clave pública de manifiestos (<see cref="UpdateTrust"/>).
///
/// <para><b>CLAVE DE RELEASE (F3-02):</b> par real generado con <c>orvita-release genkey
/// --type ecdsa</c>. La MITAD PRIVADA (PKCS#8) vive FUERA del repositorio y del servidor, con
/// copia de respaldo offline — ubicación documentada en
/// <c>agent/tools/Orvita.Print.ReleaseTools/README.md</c>. Las licencias de los clientes se firman
/// con ella (<c>orvita-release genlicense</c>); si se pierde, no se pueden emitir licencias y hay
/// que rotar la clave embebida.</para>
/// </summary>
public static class LicenseTrust
{
    /// <summary>Clave pública ECDSA P-256 de las licencias, SubjectPublicKeyInfo DER en base64.</summary>
    public const string ReleasePublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAELN5i6nYvZFu4QpSbNZNq4CyyY1YKjyE+SQmSGYpDT4cizeK2GiyaZwXhJfg0cFReR4Wc/03lqIRlw+juSRUZpg==";

    /// <summary>Instancia la clave pública embebida para verificar licencias.</summary>
    public static ECDsa CreatePublicKey()
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ReleasePublicKeyBase64), out _);
        return key;
    }
}
