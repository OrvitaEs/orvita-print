using Orvita.Print.Cli;
using Orvita.Print.Core;

namespace Orvita.Print.Cli;

/// <summary>
/// Parser de argumentos de <c>orvitaprint</c> (F2-09), puro y sin efeitos: lo prueban las
/// xUnit sin servicio ni pipe. Valida S-03 aquí (el CLI valida la URL ANTES de enviarla al
/// servicio, Doc/03 §8) y genera la ayuda por comando. Sin Globalization ni parseos raros.
/// </summary>
public static class CliParser
{
    /// <summary>Parsea argv. Nunca lanza: los errores de uso van en el resultado (código 2).</summary>
    public static ParseOutcome Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return ParseOutcome.ShowHelp(HelpTexts.General());
        }

        // Opción global --pipe <nombre> (avanzada: pruebas y desarrollo; por defecto la estándar).
        var pipeName = ManagementProtocol.PipeName;
        var tokens = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--pipe" or "--pipe-name")
            {
                if (i + 1 >= args.Length)
                {
                    return ParseOutcome.UsageError("--pipe espera el nombre de la pipe (p. ej. orvitaprint-management).");
                }

                var candidate = args[++i].Trim();
                if (candidate.Length == 0 || candidate.Contains('\\') || candidate.Contains('/'))
                {
                    return ParseOutcome.UsageError($"El nombre de pipe «{candidate}» no es válido: solo el nombre corto (p. ej. orvitaprint-management).");
                }

                pipeName = candidate;
                continue;
            }

            tokens.Add(args[i]);
        }

        if (tokens.Count == 0)
        {
            return ParseOutcome.ShowHelp(HelpTexts.General(), pipeName);
        }

        var command = tokens[0].ToLowerInvariant();
        var rest = tokens.Skip(1).ToArray();

        // Ayuda: global, comando o subcomando («orvitaprint destination add --help»).
        var helpTopic = HelpTopicOf([.. tokens]);
        if (helpTopic is not null)
        {
            return ParseOutcome.ShowHelp(HelpTexts.For(helpTopic), pipeName);
        }

        try
        {
            return command switch
            {
                "pair" => ParsePair(rest, pipeName),
                "destination" => ParseDestination(rest, pipeName),
                "status" => ParseOutcome.Ok(new StatusCommand(), pipeName),
                "test" => ParseTest(rest, pipeName),
                "doctor" => ParseOutcome.Ok(new DoctorCommand(), pipeName),
                "restart-poller" => ParseOutcome.Ok(new RestartPollerCommand(), pipeName),
                _ => ParseOutcome.UsageError(
                    $"Comando desconocido «{args[0]}». Usa orvitaprint --help para ver los comandos disponibles.", pipeName),
            };
        }
        catch (UsageException ex)
        {
            return ParseOutcome.UsageError(ex.Message, pipeName);
        }
    }

    private static string? HelpTopicOf(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h") || args.Contains("-?"))
        {
            var topic = args.Length > 1 && !args[1].StartsWith('-') && args[1] != "--help" && args[1] != "-h"
                ? args[1].ToLowerInvariant()
                : null;
            return topic is null || topic == "help" ? args[0].ToLowerInvariant() : $"{args[0].ToLowerInvariant()} {topic}".Trim();
        }

        if (args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            return args.Length > 1 ? args[1].ToLowerInvariant() : null;
        }

        return null;
    }

    // --------------------------------------------------------------------------------- pair

    private static ParseOutcome ParsePair(string[] args, string pipeName)
    {
        string? server = null;
        string? code = null;
        string? agentName = null;
        var allowInsecure = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--server" when i + 1 < args.Length:
                    server = args[++i].Trim();
                    break;
                case "--name" or "--agent-name" when i + 1 < args.Length:
                    agentName = args[++i].Trim();
                    break;
                case "--allow-insecure-http":
                    allowInsecure = true;
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        throw new UsageException($"Opción no reconocida en pair: {args[i]}");
                    }

                    if (code is null)
                    {
                        code = args[i].Trim(); // <código> posicional
                    }
                    else
                    {
                        throw new UsageException($"Argumento inesperado «{args[i]}»: solo se espera el código (6 dígitos).");
                    }

                    break;
            }
        }

        if (string.IsNullOrEmpty(server))
        {
            throw new UsageException("Falta --server <url>. Ejemplo: orvitaprint pair --server https://tienda.example/wp-json/orp-print/v1 482913");
        }

        if (string.IsNullOrEmpty(code))
        {
            throw new UsageException("Falta el código de emparejamiento (6 dígitos, lo genera el panel del plugin). Ejemplo: orvitaprint pair --server https://… 482913");
        }

        // S-03 (Doc/03 §8): HTTPS obligatorio. HTTP solo con el modo de desarrollo explícito.
        // La URL NO se valida en profundidad aquí: eso lo hace el servicio al aplicar el pair;
        // el esquema sí, porque es la regla que evita enviar el código de emparejamiento por HTTP.
        if (!server.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !server.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException(
                $"La URL «{server}» no es válida: debe empezar por https:// (o http:// solo en desarrollo).");
        }

        if (server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !allowInsecure)
        {
            throw new UsageException(
                "HTTP sin TLS está prohibido (S-03, Doc/03 §8): el código de emparejamiento y el token nunca deben " +
                "viajar en claro. Si es un entorno de desarrollo (p. ej. wp-env), añade --allow-insecure-http de forma explícita.");
        }

        return ParseOutcome.Ok(new PairCommand(server, code, agentName, allowInsecure), pipeName);
    }

    // --------------------------------------------------------------------------- destination

    private static ParseOutcome ParseDestination(string[] args, string pipeName)
    {
        if (args.Length == 0)
        {
            throw new UsageException("Falta el subcomando: orvitaprint destination add|list|remove");
        }

        return args[0].ToLowerInvariant() switch
        {
            "add" => ParseDestinationAdd(args.Skip(1).ToArray(), pipeName),
            "list" => ParseOutcome.Ok(new DestinationListCommand(), pipeName),
            "remove" or "rm" or "delete" => ParseDestinationRemove(args.Skip(1).ToArray(), pipeName),
            _ => ParseOutcome.UsageError(
                $"Subcomando de destination desconocido «{args[0]}»: usa add, list o remove.", pipeName),
        };
    }

    private static ParseOutcome ParseDestinationAdd(string[] args, string pipeName)
    {
        string? printer = null;
        string? role = null;
        string? label = null;
        string? kind = null;
        string? address = null;
        var copies = 1;
        var enabled = true;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--printer" when i + 1 < args.Length:
                    printer = args[++i].Trim();
                    break;
                case "--role" when i + 1 < args.Length:
                    role = args[++i].Trim();
                    break;
                case "--label" when i + 1 < args.Length:
                    label = args[++i].Trim();
                    break;
                case "--copies" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var parsed) || parsed < 1 || parsed > 99)
                    {
                        throw new UsageException("--copies debe ser un número entre 1 y 99.");
                    }

                    copies = parsed;
                    break;
                case "--kind" when i + 1 < args.Length:
                    kind = args[++i].Trim().ToLowerInvariant();
                    if (kind is not ("auto" or "raw" or "gdi" or "tcp"))
                    {
                        throw new UsageException("--kind debe ser auto, raw, gdi o tcp.");
                    }

                    break;
                case "--address" when i + 1 < args.Length:
                    address = args[++i].Trim();
                    break;
                case "--enabled" when i + 1 < args.Length:
                    var value = args[++i].Trim().ToLowerInvariant();
                    enabled = value switch
                    {
                        "true" or "si" or "sí" or "1" or "on" => true,
                        "false" or "no" or "0" or "off" => false,
                        _ => throw new UsageException($"--enabled espera true o false (recibido «{value}»)."),
                    };
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        throw new UsageException($"Opción no reconocida en destination add: {args[i]}");
                    }

                    throw new UsageException($"Argumento inesperado «{args[i]}» en destination add.");
            }
        }

        if (string.IsNullOrWhiteSpace(printer) && string.IsNullOrWhiteSpace(address))
        {
            throw new UsageException(
                "Falta --printer \"nombre exacto de la impresora\". Consulta los nombres con orvitaprint destination list (Doc/03 §6.6).");
        }

        return ParseOutcome.Ok(new DestinationAddCommand(
            string.IsNullOrWhiteSpace(role) ? "default" : role.Trim(),
            label,
            string.IsNullOrWhiteSpace(printer) ? null : printer,
            copies,
            enabled,
            kind ?? "auto",
            address), pipeName);
    }

    private static ParseOutcome ParseDestinationRemove(string[] args, string pipeName)
    {
        string? role = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--role" when i + 1 < args.Length:
                    role = args[++i].Trim();
                    break;
                default:
                    throw new UsageException($"Argumento inesperado «{args[i]}» en destination remove (esperaba --role <rol>).");
            }
        }

        if (string.IsNullOrWhiteSpace(role))
        {
            throw new UsageException("Falta --role <rol> (la salida que se elimina).");
        }

        return ParseOutcome.Ok(new DestinationRemoveCommand(role), pipeName);
    }

    // ---------------------------------------------------------------------------------- test

    private static ParseOutcome ParseTest(string[] args, string pipeName)
    {
        string? role = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--role" when i + 1 < args.Length:
                    role = args[++i].Trim();
                    break;
                default:
                    throw new UsageException($"Argumento inesperado «{args[i]}» en test (esperaba --role <rol>).");
            }
        }

        return ParseOutcome.Ok(new TestCommand(role), pipeName);
    }

    /// <summary>Error de uso: el CLI lo imprime en stderr con código 2.</summary>
    private sealed class UsageException(string message) : Exception(message);
}
