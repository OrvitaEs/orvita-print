namespace Orvita.Print.Core;

/// <summary>
/// Protección de secretos en reposo (S-01, Doc/03 §8). El almacén local guarda el token del
/// agente como texto opaco cifrado; NUNCA en claro ni en JSON plano (S-01/S-05).
///
/// La interfaz vive en Core (portable, ADR-12); la implementación DPAPI
/// (<c>System.Security.Cryptography.ProtectedData</c>, ámbito <c>LocalMachine</c>) vive en
/// Orvita.Print.Service, que es la pieza que ya es específica de Windows. Un futuro anfitrión
/// multiplataforma inyectará su propia implementación (permisos 600 + dueño del servicio).
/// </summary>
public interface ISecretProtector
{
    /// <summary>Cifra el texto y devuelve su forma almacenable (opaca). Nunca null.</summary>
    string Protect(string plaintext);

    /// <summary>Descifra lo devuelto por <see cref="Protect"/>; null si no puede (dato corrupto, clave distinta).</summary>
    string? Unprotect(string ciphertext);
}
