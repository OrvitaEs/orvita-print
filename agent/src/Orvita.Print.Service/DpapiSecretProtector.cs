using System.Security.Cryptography;
using System.Text;
using Orvita.Print.Core;

namespace Orvita.Print.Service;

/// <summary>
/// Protección DPAPI del token del agente (S-01, Doc/03 §8): <c>ProtectedData</c> con ámbito
/// <c>LocalMachine</c> — el token solo se descifra en esta máquina (y solo el servicio, que
/// corre como LocalSystem/usuario de servicio, lo tiene que descifrar: el CLI NUNCA lo lee,
/// todo pasa por la pipe). Este adaptador es la única pieza que conoce DPAPI: la interfaz
/// <see cref="ISecretProtector"/> vive en Core (portable, ADR-12).
///
/// Entropía adicional FIJA (documentada, no secreta): evita que otro proceso de la misma
/// máquina pueda descifrar el blob conociento solo DPAPI sin replicarla; la seguridad real
/// descansa en el ámbito LocalMachine + los permisos del directorio de datos (F3-01).
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>Entropía adicional fija de la aplicación (S-01). Cambiarla invalida los blobs guardados.</summary>
    public static readonly byte[] AdditionalEntropy = "OrvitaPrint/v1/agent-token"u8.ToArray();

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext),
            AdditionalEntropy,
            DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(cipher);
    }

    /// <inheritdoc />
    public string? Unprotect(string ciphertext)
    {
        if (string.IsNullOrWhiteSpace(ciphertext))
        {
            return null; // fila vacía en el almacén: no descifrado, tampoco error
        }

        try
        {
            var plain = ProtectedData.Unprotect(
                Convert.FromBase64String(ciphertext),
                AdditionalEntropy,
                DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        catch (FormatException)
        {
            // No era base64 (token en claro de una instalación antigua, o dato corrupto).
            return null;
        }
        catch (CryptographicException)
        {
            // Blob de otra máquina/usuario u otra entropía: no descifrable aquí.
            return null;
        }
    }
}
