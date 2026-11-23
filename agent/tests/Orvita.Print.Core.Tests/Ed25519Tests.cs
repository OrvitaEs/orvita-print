using Orvita.Print.Core;
using Xunit;

namespace Orvita.Print.Core.Tests;

/// <summary>
/// Ed25519 contra los vectores oficiales del RFC 8032 §7.1 (TEST 1, 2, 3 y SHA(abc)) más
/// casos de abuso: firma/manifiesto alterados, clave equivocada, scalar no canónico (S ≥ L)
/// y tallas incorrectas. La corrección del primitivo es la base de toda la tarea F2-11.
/// </summary>
public sealed class Ed25519Tests
{
    private const string TestMessage = "Pizza Margarita · jamón, piña, ñ, €, áéíóú";

    public static TheoryData<string, string, string, string> Rfc8032Vectors => new()
    {
        // seed, publicKey, mensaje (hex), firma (hex)
        {
            "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
            "",
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"
        },
        {
            "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
            "72",
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"
        },
        {
            "c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
            "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
            "af82",
            "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"
        },
        {
            "833fe62409237b9d62ec77587520911e9a759cec1d19755b7da901b96dca3d42",
            "ec172b93ad5e563bf4932c70e1245034c35467ef2efd4d64ebf819683467e2bf",
            "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
            "dc2a4459e7369633a52b1bf277839a00201009a3efbf3ecb69bea2186c26b58909351fc9ac90b3ecfdfbc7c66431e0303dca179c138ac17ad9bef1177331a704"
        },
    };

    [Theory]
    [MemberData(nameof(Rfc8032Vectors))]
    public void Verifica_los_vectores_oficiales_del_RFC_8032(string seedHex, string publicKeyHex, string messageHex, string signatureHex)
    {
        var publicKey = HexToBytes(publicKeyHex);
        var message = HexToBytes(messageHex);
        var signature = HexToBytes(signatureHex);

        Assert.True(Ed25519.Verify(publicKey, message, signature));
        // La clave pública derivada de la semilla debe coincidir con la oficial.
        Assert.Equal(publicKeyHex, BytesToHex(Ed25519.PublicKeyFromSeed(HexToBytes(seedHex))));
        // Y firmar el mismo mensaje con la semilla reproduce la firma oficial (determinista).
        Assert.Equal(signatureHex, BytesToHex(Ed25519.Sign(HexToBytes(seedHex), message)));
    }

    [Fact]
    public void Rechaza_un_byte_alterado_del_mensaje()
    {
        var seed = HexToBytes("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var message = System.Text.Encoding.UTF8.GetBytes(TestMessage);
        var signature = Ed25519.Sign(seed, message);

        Assert.True(Ed25519.Verify(publicKey, message, signature));

        var tampered = (byte[])message.Clone();
        tampered[0] ^= 0x01;
        Assert.False(Ed25519.Verify(publicKey, tampered, signature));
    }

    [Fact]
    public void Rechaza_un_byte_alterado_de_la_firma()
    {
        var seed = HexToBytes("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7");
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var message = HexToBytes("af82");
        var signature = Ed25519.Sign(seed, message);

        var tampered = (byte[])signature.Clone();
        tampered[10] ^= 0x40;
        Assert.False(Ed25519.Verify(publicKey, message, tampered));
    }

    [Fact]
    public void Rechaza_la_clave_publica_equivalocada()
    {
        var seed = HexToBytes("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");
        var otherKey = Ed25519.PublicKeyFromSeed(HexToBytes("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7"));
        var message = HexToBytes("72");

        Assert.False(Ed25519.Verify(otherKey, message, Ed25519.Sign(seed, message)));
    }

    [Fact]
    public void Rechaza_firmas_no_canonicas_S_mayor_o_igual_que_L()
    {
        var seed = HexToBytes("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var message = System.Text.Encoding.UTF8.GetBytes(TestMessage);
        var signature = Ed25519.Sign(seed, message);

        // S = firma[32..64] + L (little-endian): entero válido de 256 bits, pero no canónico.
        var s = new System.Numerics.BigInteger([.. signature[32..], 0]);
        var l = (System.Numerics.BigInteger.One << 252)
            + System.Numerics.BigInteger.Parse("27742317777372353535851937790883648493", System.Globalization.CultureInfo.InvariantCulture);
        var forged = (byte[])signature.Clone();
        sPlusLBytes(s + l).CopyTo(forged, 32);

        Assert.False(Ed25519.Verify(publicKey, message, forged));
    }

    private static byte[] sPlusLBytes(System.Numerics.BigInteger value)
    {
        var bytes = value.ToByteArray(); // little-endian (+byte de signo); S+L < 2^254 → caben en 32
        return bytes[..32];
    }

    [Fact]
    public void Rechaza_tallas_incorrectas()
    {
        var key = new byte[32];
        var message = new byte[] { 1, 2, 3 };

        Assert.False(Ed25519.Verify(key, message, new byte[63]));
        Assert.False(Ed25519.Verify(new byte[31], message, new byte[64]));
        Assert.Throws<ArgumentException>(() => Ed25519.Sign(new byte[31], message));
    }

    [Fact]
    public void Roundtrip_sign_verify_con_la_cadena_de_prueba_obligatoria()
    {
        var seed = new byte[32];
        Random.Shared.NextBytes(seed); // cualquier secuencia de 32 bytes es una semilla válida
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var message = System.Text.Encoding.UTF8.GetBytes(TestMessage);
        var signature = Ed25519.Sign(seed, message);

        Assert.True(Ed25519.Verify(publicKey, message, signature));
        Assert.False(Ed25519.Verify(Ed25519.PublicKeyFromSeed(new byte[32]), message, signature));
    }

    [Fact]
    public void Rechaza_puntos_no_canonicos_o_fuera_de_la_curva()
    {
        var seed = HexToBytes("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        var message = new byte[] { 0x01 };
        var signature = Ed25519.Sign(seed, message);

        // y ≥ p (codificación no canónica): todos los bytes a 0xFF.
        Assert.False(Ed25519.Verify(new byte[32].Select(_ => (byte)0xFF).ToArray(), message, signature));

        // x = 0 con bit de signo: el punto identidad con signo no existe.
        var identityWithSign = new byte[32];
        identityWithSign[31] = 0x80;
        Assert.False(Ed25519.Verify(identityWithSign, message, signature));

        // Un punto aleatorio fuera de la curva (y arbitrario) debe fallar al decodificar.
        var offCurve = new byte[32];
        offCurve[0] = 0x03;
        offCurve[1] = 0x7B;
        Assert.False(Ed25519.Verify(offCurve, message, signature));
    }

    internal static byte[] HexToBytes(string hex)
    {
        Assert.Equal(0, hex.Length % 2);
        return System.Convert.FromHexString(hex);
    }

    internal static string BytesToHex(byte[] bytes) =>
        System.Convert.ToHexString(bytes).ToLowerInvariant();
}
