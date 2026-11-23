namespace Orvita.Print.Core;

/// <summary>
/// Verificador de firmas portable (ADR-12): abstrae el algoritmo de firma del manifiesto de
/// actualización (y de futuros usos, p. ej. licencia). Implementación en V1:
/// <see cref="Ed25519Verifier"/> (C# puro, sin dependencias del SO). Vive en Core para que el
/// <see cref="UpdateChecker"/> pueda verificar sin conocer la plataforma.
/// </summary>
public interface ISignatureVerifier
{
    /// <summary>Comprueba <paramref name="signature"/> sobre <paramref name="data"/> con la clave pública dada.</summary>
    bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature);
}

/// <summary>Verificador Ed25519 (RFC 8032) sobre la implementación portable de este mismo ensamblado.</summary>
public sealed class Ed25519Verifier : ISignatureVerifier
{
    public bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) =>
        Ed25519.Verify(publicKey, data, signature);
}
