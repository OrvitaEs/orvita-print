using System.Numerics;
using System.Security.Cryptography;

namespace Orvita.Print.Core;

/// <summary>
/// Ed25519 (EdDSA sobre Curve25519, RFC 8032 §5.1) en C# puro, portable (ADR-12).
///
/// Por qué una implementación propia y no CNG ni un paquete de terceros (decisión F2-11):
/// - .NET 10 NO trae <c>System.Security.Cryptography.Ed25519</c>: el agente cifra con lo que
///   da el SO, y Windows CNG no implementa EdDSA (SymCrypt, Microsoft: «no lo esperamos»).
///   No hay API de .NET que lo alcance en Windows 10 22H2, que SÍ es objetivo soportado.
/// - Un paquete de terceros (BouncyCastle: superficie enorme; NSec: binario nativo libsodium,
///   rompe el MSI self-contained) no está justificado para verificar UNA firma al día.
/// - El agente SOLO VERIFICA (la privada de release vive fuera del servidor y del repo, F3-03);
///   <see cref="Sign"/> existe para el generador de manifiestos (F3-03) y las pruebas.
///
/// Aritmética afín con las fórmulas unificadas de twisted Edwards (a = -1), COMPLETAS en
/// edwards25519 (d no-cuadrado): nunca hay denominador nulo para puntos de la curva, y por
/// tanto no hay ramas frágiles. Corrección avalada por los vectores oficiales RFC 8032
/// (pruebas unitarias: TEST 1/2/3 y SHA(abc), sign+verify+clave pública derivada).
/// Rendimiento: irrelevante (una verificación al día; del orden de décimas de segundo).
/// </summary>
public static class Ed25519
{
    /// <summary>Tamaño de la clave pública/privada (semilla) en bytes.</summary>
    public const int KeySize = 32;

    /// <summary>Tamaño de la firma en bytes (R || S).</summary>
    public const int SignatureSize = 64;

    private static readonly BigInteger P = (BigInteger.One << 255) - 19;
    private static readonly BigInteger L = (BigInteger.One << 252)
        + BigInteger.Parse("27742317777372353535851937790883648493", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly BigInteger D = Mod(-121665 * ModInverse(121666));
    private static readonly BigInteger SqrtM1 = ModPow(2, (P - 1) / 4);

    /// <summary>
    /// Verifica una firma Ed25519 (RFC 8032, ecuación estricta <c>[S]B = R + [k]A</c>).
    /// Rechaza firmas no canónicas (<c>S ≥ L</c>), puntos no canónicos (<c>y ≥ p</c>) y puntos
    /// que no están en la curva. false ante cualquier entrada de tamaño incorrecto.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != KeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        if (!TryDecodePoint(publicKey, out var pointA))
        {
            return false;
        }

        var sigBytes = signature.ToArray();

        // S canónico: 0 ≤ S < L (RFC 8032 §5.1.7 paso 1, «S being out of range») — ANTES de
        // reducir módulo L, porque la reducción haría el chequeo imposible.
        if (FromLittleEndianUnsigned(sigBytes.AsSpan(32, 32).ToArray()) >= L)
        {
            return false;
        }

        var s = FromLittleEndian(sigBytes.AsSpan(32, 32).ToArray(), L);
        if (!TryDecodePoint(sigBytes.AsSpan(0, 32).ToArray(), out var pointR))
        {
            return false;
        }

        // Punto de orden bajo en A: la clave pública es [s]B con B de orden L, nunca torsión.
        // (Higiene: imposible con una clave embebida, pero evita sorpresas ante claves foráneas.)
        if (pointA.X.IsZero)
        {
            return false;
        }

        // k = SHA-512(R || A || M) mod L
        var k = Hash512AndReduce([.. sigBytes.AsSpan(0, 32), .. publicKey.ToArray(), .. message.ToArray()]);

        // [S]B == R + [k]A (estricto: rechaza malleabilidad de firma, que aquí no queremos).
        var left = ScalarMult(s, BasePoint);
        var right = PointAdd(pointR, ScalarMult(k, pointA));
        return PointEquals(left, right);
    }

    /// <summary>
    /// Firma (RFC 8032 §5.1.6) con la semilla privada de 32 bytes. Fuera del camino del agente:
    /// la usa el generador de manifiestos (F3-03, máquina de release) y las pruebas.
    /// </summary>
    public static byte[] Sign(ReadOnlySpan<byte> privateKeySeed, ReadOnlySpan<byte> message)
    {
        if (privateKeySeed.Length != KeySize)
        {
            throw new ArgumentException("La semilla Ed25519 debe tener 32 bytes.", nameof(privateKeySeed));
        }

        var seed = privateKeySeed.ToArray();
        var s = ClamppedScalarFromSeed(seed);
        var prefix = SHA512.HashData(seed)[32..].ToArray();

        var pointA = ScalarMult(s, BasePoint);
        var encodedA = EncodePoint(pointA);

        // r = SHA-512(prefix || M) mod L; R = [r]B
        var r = Hash512AndReduce([.. prefix, .. message.ToArray()]);
        var encodedR = EncodePoint(ScalarMult(r, BasePoint));

        // S = (r + k·s) mod L
        var k = Hash512AndReduce([.. encodedR, .. encodedA, .. message.ToArray()]);
        var signatureS = Mod(r + k * s, L);

        var result = new byte[SignatureSize];
        encodedR.CopyTo(result, 0);
        ToLittleEndian(signatureS).CopyTo(result, 32);
        return result;
    }

    /// <summary>Deriva la clave pública (32 bytes) desde la semilla: <c>[clamp(SHA-512(seed))]B</c> (RFC 8032 §5.1.5).</summary>
    public static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != KeySize)
        {
            throw new ArgumentException("La semilla Ed25519 debe tener 32 bytes.", nameof(seed));
        }

        return EncodePoint(ScalarMult(ClamppedScalarFromSeed(seed), BasePoint));
    }

    /// <summary>Escalar privado desde la semilla: SHA-512 de la semilla, primera mitad con clamp de RFC 8032.</summary>
    private static BigInteger ClamppedScalarFromSeed(ReadOnlySpan<byte> seed)
    {
        var hash = SHA512.HashData(seed);
        var clamped = hash[..32];
        clamped[0] &= 248;
        clamped[31] &= 127;
        clamped[31] |= 64;
        return FromLittleEndian(clamped, L);
    }

    // ------------------------------------------------------------------------ punto (afín)

    private readonly record struct AffinePoint(BigInteger X, BigInteger Y);

    private static AffinePoint BasePoint { get; } = DecodeBasePoint();

    /// <summary>
    /// Punto base, decodificado de su codificación canónica conocida
    /// (little-endian: <c>0x58</c> seguido de 31 bytes <c>0x66</c>) — sin constantes de 77
    /// dígitos tecleadas a mano, fuente clásica de errores.
    /// </summary>
    private static AffinePoint DecodeBasePoint()
    {
        var encoded = new byte[32];
        encoded[0] = 0x58;
        Array.Fill(encoded, (byte)0x66, 1, 31);
        if (!TryDecodePoint(encoded, out var point))
        {
            throw new InvalidOperationException("El punto base Ed25519 no se pudo decodificar (error interno).");
        }

        return point;
    }

    /// <summary>Suma unificada (completa para edwards25519): sirve también para doblar.</summary>
    private static AffinePoint PointAdd(AffinePoint p1, AffinePoint p2)
    {
        var xx = p1.X * p2.X;
        var yy = p1.Y * p2.Y;
        var dxxyy = D * xx * yy;

        var xNumerator = p1.X * p2.Y + p1.Y * p2.X;
        var yNumerator = yy + xx; // a = -1: y1·y2 − a·x1·x2 = y1·y2 + x1·x2

        return new AffinePoint(
            Mod(xNumerator * ModInverse(Mod(1 + dxxyy))),
            Mod(yNumerator * ModInverse(Mod(1 - dxxyy))));
    }

    private static AffinePoint ScalarMult(BigInteger scalar, AffinePoint point)
    {
        var result = new AffinePoint(0, 1); // identidad
        var accumulator = point;
        while (scalar > 0)
        {
            if (!scalar.IsEven)
            {
                result = PointAdd(result, accumulator);
            }

            accumulator = PointAdd(accumulator, accumulator);
            scalar >>= 1;
        }

        return result;
    }

    private static bool PointEquals(AffinePoint p1, AffinePoint p2) => p1.X == p2.X && p1.Y == p2.Y;

    /// <summary>
    /// Decodificación estricta (RFC 8032 §5.1.3): <c>y</c> canónico (&lt; p), bit de signo para
    /// <c>x</c>, y recuperación de <c>x</c> con la raíz cuadrática de la curva. false si el punto
    /// no está en la curva o la codificación no es canónica.
    /// </summary>
    private static bool TryDecodePoint(ReadOnlySpan<byte> encoded, out AffinePoint point)
    {
        point = default;
        if (encoded.Length != KeySize)
        {
            return false;
        }

        var buffer = encoded.ToArray();
        var xIsOdd = (buffer[31] & 0x80) != 0;
        buffer[31] &= 0x7F;
        var y = FromLittleEndian(buffer, P);

        // u = y² − 1, v = d·y² + 1 → x² = u/v
        var y2 = Mod(y * y);
        var u = Mod(y2 - 1);
        var v = Mod(D * y2 + 1);

        // x = u·v³·(u·v⁷)^((p−5)/8) — forma sin inversión de (u/v)^((p+3)/8).
        var v3 = ModPow(v, 3);
        var v7 = Mod(v3 * ModPow(v, 4));
        var x = Mod(Mod(u * v3) * ModPow(Mod(u * v7), (P - 5) / 8));

        var check = Mod(v * x * x);
        if (check != u)
        {
            if (check == Mod(-u))
            {
                x = Mod(x * SqrtM1);
            }
            else
            {
                return false; // no hay raíz: el punto no está en la curva
            }
        }

        if (x.IsZero && xIsOdd)
        {
            return false; // x = 0 no puede llevar bit de signo (RFC 8032 §5.1.3)
        }

        if (!x.IsEven != xIsOdd)
        {
            x = P - x;
        }

        point = new AffinePoint(x, y);
        return true;
    }

    private static byte[] EncodePoint(AffinePoint point)
    {
        var encoded = ToLittleEndian(point.Y);
        if (point.X.IsEven)
        {
            return encoded;
        }

        encoded[31] |= 0x80;
        return encoded;
    }

    // ----------------------------------------------------------------------- aritmética mod p

    private static BigInteger Mod(BigInteger value) => Mod(value, P);

    private static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        var reduced = value % modulus;
        return reduced.Sign >= 0 ? reduced : reduced + modulus;
    }

    private static BigInteger ModPow(BigInteger value, BigInteger exponent) => BigInteger.ModPow(value, exponent, P);

    private static BigInteger ModInverse(BigInteger value) => ModPow(Mod(value), P - 2);

    private static BigInteger Hash512AndReduce(byte[] data)
    {
        var digest = SHA512.HashData(data);
        return FromLittleEndian(digest, L);
    }

    private static byte[] ToLittleEndian(BigInteger value)
    {
        var bytes = value.ToByteArray(); // little-endian, con byte extra 0x00 en positivos grandes
        var result = new byte[32];
        Array.Copy(bytes, result, Math.Min(bytes.Length, 32));
        return result;
    }

    private static BigInteger FromLittleEndian(byte[] data, BigInteger modulus)
    {
        // Byte 0x00 extra: interpretación SIN signo (BigInteger es complemento a 2).
        var padded = new byte[data.Length + 1];
        Array.Copy(data, padded, data.Length);
        return Mod(new BigInteger(padded), modulus);
    }

    /// <summary>Entero sin signo desde bytes little-endian (sin reducción — para comparar con L).</summary>
    private static BigInteger FromLittleEndianUnsigned(byte[] data)
    {
        var padded = new byte[data.Length + 1];
        Array.Copy(data, padded, data.Length);
        return new BigInteger(padded);
    }
}
