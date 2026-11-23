using System.Globalization;
using System.Security.Cryptography;
using Orvita.Print.Core;

namespace Orvita.Print.ReleaseTools;

/// <summary>
/// <c>orvita-release</c> — herramienta de release de Orvita Print (F3-02/F3-03). Es NUESTRA
/// herramienta interna: NO se distribuye en el MSI ni llega al cliente.
///
/// <para>Subcomandos:</para>
/// <list type="bullet">
/// <item><c>genkey --type ecdsa|ed25519 --out-dir &lt;dir&gt;</c> — par de claves nuevo: ECDSA
/// P-256 para firmar licencias (PEM PKCS#8/SPKI) o Ed25519 para firmar manifiestos
/// <c>latest.json</c> (semilla cruda base64). La privada NUNCA va al repositorio.</item>
/// <item><c>genlicense</c> — genera <c>license.dat</c> firmado (ECDSA P-256, Doc/03 §10).</item>
/// <item><c>genmanifest</c> — genera <c>latest.json</c> firmado (Ed25519) desde un MSI + su
/// SHA-256, y actualiza el fichero de checksums del release.</item>
/// </list>
///
/// <para>Convención de firma: la MISMA que el agente verifica — los campos firmados en orden
/// canónico (JSON compacto UTF-8, escapado mínimo), excluyendo <c>signature</c> y campos extra;
/// el verificador re-canonicaliza los valores PARSEADOS, así que el formato del fichero publicado
/// es libre mientras los VALORES no cambien.</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Help();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "genkey" => GenKey(args[1..]),
                "genlicense" => GenLicense(args[1..]),
                "genmanifest" => GenManifest(args[1..]),
                _ => Fail($"Subcomando desconocido «{args[0]}». Usa genkey, genlicense o genmanifest (orvita-release --help)."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    // ----------------------------------------------------------------------------- genkey

    private static int GenKey(string[] args)
    {
        var type = Option(args, "--type") ?? throw new UsageException("Falta --type ecdsa|ed25519.");
        var outDir = Option(args, "--out-dir") ?? throw new UsageException("Falta --out-dir <directorio>.");
        var force = Has(args, "--force");

        if (type is not ("ecdsa" or "ed25519"))
        {
            throw new UsageException($"--type debe ser «ecdsa» (licencias) o «ed25519» (manifiestos), no «{type}».");
        }

        Directory.CreateDirectory(outDir);

        if (type == "ecdsa")
        {
            return GenKeyEcdsa(outDir, force);
        }

        return GenKeyEd25519(outDir, force);
    }

    /// <summary>ECDSA P-256 para licencias: PKCS#8 (privada) + SPKI (pública) en PEM estándar.</summary>
    private static int GenKeyEcdsa(string outDir, bool force)
    {
        var privatePath = Path.Combine(outDir, "orp-license-private.pem");
        var publicPath = Path.Combine(outDir, "orp-license-public.pem");
        GuardNotExisting([privatePath, publicPath], force);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());

        Console.WriteLine("Par ECDSA P-256 para firmar LICENCIAS generado:");
        Console.WriteLine($"  Privada (NUNCA al repo/servidor): {privatePath}");
        Console.WriteLine($"  Pública:                          {publicPath}");
        Console.WriteLine();
        Console.WriteLine("Clave pública (SubjectPublicKeyInfo DER, base64 de una línea) para EMBEBER en el agente:");
        Console.WriteLine($"  {Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}");
        Console.WriteLine();
        Console.WriteLine("→ Pégala en Orvita.Print.Core/LicenseTrust.ReleasePublicKeyBase64, compila y publica.");
        Console.WriteLine("→ Guarda la privada con copia de seguridad offline; si se pierde, no puedes emitir licencias.");
        return 0;
    }

    /// <summary>Ed25519 para manifiestos latest.json: semilla cruda de 32 bytes en base64.</summary>
    private static int GenKeyEd25519(string outDir, bool force)
    {
        var seedPath = Path.Combine(outDir, "orp-manifest-private-seed.key");
        var publicPath = Path.Combine(outDir, "orp-manifest-public.key");
        GuardNotExisting([seedPath, publicPath], force);

        var seed = RandomNumberGenerator.GetBytes(Ed25519.KeySize);
        var publicKey = Ed25519.PublicKeyFromSeed(seed);

        File.WriteAllText(seedPath, Convert.ToBase64String(seed) + Environment.NewLine);
        File.WriteAllText(publicPath, Convert.ToBase64String(publicKey) + Environment.NewLine);

        Console.WriteLine("Par Ed25519 para firmar MANIFIESTOS (latest.json) generado:");
        Console.WriteLine($"  Semilla privada (NUNCA al repo/servidor): {seedPath}");
        Console.WriteLine($"  Pública:                                  {publicPath}");
        Console.WriteLine();
        Console.WriteLine("Clave pública (32 bytes en crudo, base64) para EMBEBER en el agente:");
        Console.WriteLine($"  {Convert.ToBase64String(publicKey)}");
        Console.WriteLine();
        Console.WriteLine("→ Pégala en Orvita.Print.Core/UpdateTrust.ReleasePublicKeyBase64, compila y publica.");
        Console.WriteLine("→ Guarda la semilla con copia de seguridad offline; sin ella no puedes publicar updates.");
        return 0;
    }

    // ------------------------------------------------------------------------- genlicense

    private static int GenLicense(string[] args)
    {
        var keyPath = Option(args, "--key") ?? throw new UsageException("Falta --key <orp-license-private.pem>.");
        var sku = Option(args, "--sku") ?? throw new UsageException("Falta --sku <1|10|unlimited>.");
        var customer = Option(args, "--customer") ?? throw new UsageException("Falta --customer \"Nombre del cliente\".");
        var updatesUntil = Option(args, "--updates-until") ?? throw new UsageException("Falta --updates-until <yyyy-MM-dd>.");
        var outPath = Option(args, "--out") ?? throw new UsageException("Falta --out <license.dat>.");
        var licenseId = Option(args, "--license-id") ?? $"orp_{Guid.NewGuid():N}";

        if (sku is not ("1" or "10" or "unlimited"))
        {
            throw new UsageException($"--sku debe ser «1» (1 local), «10» (10 locales) o «unlimited» (ilimitado), no «{sku}».");
        }

        if (!DateOnly.TryParseExact(updatesUntil, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until))
        {
            throw new UsageException($"--updates-until debe ser una fecha yyyy-MM-dd (p. ej. 2027-11-19), no «{updatesUntil}».");
        }

        var maxLocations = sku switch { "1" => 1, "10" => 10, _ => 0 }; // 0 = ilimitado (Doc/03 §10)

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(keyPath));

        var json = LicenseDocument.BuildSignedJson(licenseId, sku, maxLocations, updatesUntil, customer, ecdsa);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(outPath, json + Environment.NewLine);

        Console.WriteLine("license.dat generado:");
        Console.WriteLine($"  Fichero:         {Path.GetFullPath(outPath)}");
        Console.WriteLine($"  license_id:      {licenseId}");
        Console.WriteLine($"  sku:             {sku} (max_locations = {maxLocations}{(maxLocations == 0 ? ", ilimitado" : "")})");
        Console.WriteLine($"  customer:        {customer}");
        Console.WriteLine($"  updates_until:   {updatesUntil} (actualizaciones automáticas hasta ese día INCLUIDO)");
        Console.WriteLine();
        Console.WriteLine("→ Entrega el fichero al cliente junto al MSI: se copia a %ProgramData%\\OrvitaPrint\\license.dat");
        Console.WriteLine("  (la validación es LOCAL con la clave pública embebida: sin conexión, Doc/03 §10).");
        return 0;
    }

    // ------------------------------------------------------------------------- genmanifest

    private static int GenManifest(string[] args)
    {
        var keyPath = Option(args, "--key") ?? throw new UsageException("Falta --key <orp-manifest-private-seed.key>.");
        var msiPath = Option(args, "--msi") ?? throw new UsageException("Falta --msi <ruta del MSI>.");
        var url = Option(args, "--url") ?? throw new UsageException("Falta --url <https://.../OrvitaPrint-<versión>.msi>.");
        var version = Option(args, "--version") ?? throw new UsageException("Falta --version <1.2.0>.");
        var minSupported = Option(args, "--min-supported") ?? version;
        var outPath = Option(args, "--out") ?? "latest.json";
        var checksumOut = Option(args, "--checksum-out") ?? Path.Combine("install", "checksum.txt");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) || parsedUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new UsageException($"--url debe ser HTTPS absoluta (S-03, Doc/03 §8), no «{url}».");
        }

        var msiFullPath = Path.GetFullPath(msiPath);
        if (!File.Exists(msiFullPath))
        {
            throw new UsageException($"No existe el MSI «{msiFullPath}».");
        }

        var seed = ReadSeed(keyPath);

        // SHA-256 EN STREAMING (el MSI puede ser grande).
        var sha256 = HashFile(msiFullPath);

        var json = UpdateManifest.BuildSignedJson(version, url, sha256, minSupported, seed);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(outPath, json + Environment.NewLine);
        UpdateChecksumFile(checksumOut, msiFullPath, sha256);

        Console.WriteLine("latest.json firmado (Ed25519, convención canónica de UpdateManifest):");
        Console.WriteLine($"  Manifiesto:    {Path.GetFullPath(outPath)}");
        Console.WriteLine($"  MSI:           {msiFullPath}");
        Console.WriteLine($"  sha256:        {sha256}");
        Console.WriteLine($"  versión:       {version} (min_supported {minSupported})");
        Console.WriteLine($"  url:           {url}");
        Console.WriteLine($"  Checksums:     {Path.GetFullPath(checksumOut)} (línea actualizada)");
        Console.WriteLine();
        Console.WriteLine("→ Sube los TRES ficheros juntos (Doc/05 §7): el MSI, latest.json y el checksum,");
        Console.WriteLine("  a la ubicación estática del canal de updates (Orvita:Update:ManifestUrl).");
        return 0;
    }

    /// <summary>Lee la semilla Ed25519 (base64 de 32 bytes; tolera whitespace y línea final).</summary>
    private static byte[] ReadSeed(string path)
    {
        var text = File.ReadAllText(path).Trim();
        if (Convert.FromBase64String(text) is not { Length: Ed25519.KeySize } seed)
        {
            throw new UsageException($"«{path}» no contiene una semilla Ed25519 (base64 de 32 bytes).");
        }

        return seed;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>Formato sha256sum («hash␠␠nombre»); reemplaza la línea de ese fichero si ya estaba.</summary>
    private static void UpdateChecksumFile(string checksumOut, string msiFullPath, string sha256)
    {
        var fileName = Path.GetFileName(msiFullPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(checksumOut));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = File.Exists(checksumOut)
            ? File.ReadAllLines(checksumOut).Where(l => !string.IsNullOrWhiteSpace(l) && !l.EndsWith($"  {fileName}", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        lines.Add($"{sha256}  {fileName}");
        File.WriteAllLines(checksumOut, lines);
    }

    // ---------------------------------------------------------------------------- helpers

    private static void GuardNotExisting(IReadOnlyList<string> paths, bool force)
    {
        var existing = paths.Where(File.Exists).ToList();
        if (existing.Count > 0 && !force)
        {
            throw new UsageException(
                "Ya existen claves («" + string.Join("», «", existing) + "»). Usa --force para SOBREESCRIBIRLAS: " +
                "todas las licencias/manifiestos firmados con el par anterior dejarían de verificarse. " +
                "Si el par anterior ya está embebido en un agente publicado, NO lo sobrescribas.");
        }
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool Has(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"ERROR: {message}");
        return 2;
    }

    private static void Help()
    {
        Console.WriteLine("""
            orvita-release — herramientas de release de Orvita Print (NO se distribuye al cliente)

            Uso:
              orvita-release genkey --type ecdsa|ed25519 --out-dir <dir> [--force]
                  Par nuevo: ecdsa firma LICENCIAS (PEM PKCS#8/SPKI), ed25519 firma MANIFIESTOS
                  latest.json (semilla base64 de 32 bytes). Imprime la clave pública para embeber.

              orvita-release genlicense --key <orp-license-private.pem> --sku <1|10|unlimited>
                                        --customer "Nombre del cliente" --updates-until <yyyy-MM-dd>
                                        --out <license.dat> [--license-id <id>]
                  Genera license.dat (Doc/03 §10) firmado con ECDSA P-256. max_locations: 1, 10 o 0
                  (ilimitado). El cliente lo copia a %ProgramData%\OrvitaPrint\license.dat.

              orvita-release genmanifest --key <orp-manifest-private-seed.key> --msi <ruta.msi>
                                         --url <https://…/OrvitaPrint-<v>.msi> --version <1.2.0>
                                         [--min-supported <1.0.0>] [--out latest.json]
                                         [--checksum-out install/checksum.txt]
                  Calcula el SHA-256 del MSI, firma el manifiesto con Ed25519 y actualiza el
                  fichero de checksums del release.

            Seguridad: las claves PRIVADAS viven fuera del repositorio y del servidor, con copia de
            respaldo offline. Si se pierden, no se pueden emitir licencias ni publicar updates.
            """);
    }
}

/// <summary>Error de uso del comando: mensaje claro y código 2.</summary>
internal sealed class UsageException(string message) : Exception(message);
