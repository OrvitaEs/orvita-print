using System.Globalization;
using System.Text.Json;
using Orvita.Print.Core;

namespace Orvita.Print.Cli;

/// <summary>
/// Ejecución del CLI (F2-09): mapea cada comando a una operación de la pipe, formatea la
/// respuesta en español y devuelve códigos de salida para scripts (0 ok · 1 fallo · 2 uso ·
/// 3 servicio no en ejecución). Toda la salida de error va a <c>stderr</c>.
/// </summary>
public static class CliApp
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter errors)
    {
        var outcome = CliParser.Parse(args);

        if (outcome.Help is { } help)
        {
            await output.WriteLineAsync(help).ConfigureAwait(false);
            return ExitCodes.Ok;
        }

        if (outcome.Error is { } usageError)
        {
            await errors.WriteLineAsync($"ERROR: {usageError}").ConfigureAwait(false);
            await errors.WriteLineAsync("Usa «orvitaprint --help» para ver el uso correcto.").ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        try
        {
            return await ExecuteAsync(outcome.Command!, outcome.PipeName ?? ManagementProtocol.PipeName, output, errors)
                .ConfigureAwait(false);
        }
        catch (ServiceNotRunningException ex)
        {
            await errors.WriteLineAsync($"ERROR: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.ServiceNotRunning;
        }
        catch (ServiceBusyException ex)
        {
            await errors.WriteLineAsync($"ERROR: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
        catch (UnauthorizedAccessException ex)
        {
            await errors.WriteLineAsync($"ERROR: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
        catch (IOException ex)
        {
            await errors.WriteLineAsync($"ERROR de comunicación con el servicio: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }

    private static async Task<int> ExecuteAsync(CliCommand command, string pipeName, TextWriter output, TextWriter errors)
    {
        using var client = new ManagementPipeClient(pipeName);
        client.Connect();

        switch (command)
        {
            case PairCommand pair:
            {
                // El token NUNCA vuelve por la pipe (S-05): el servidor lo muestra una sola vez, en el panel.
                var response = client.Request(ManagementProtocol.Operations.Pair, new
                {
                    server = pair.ServerUrl,
                    code = pair.Code,
                    agent_name = pair.AgentName,
                    allow_insecure_http = pair.AllowInsecureHttp,
                });
                if (!response.Ok)
                {
                    return await PrintErrorAsync(errors, response).ConfigureAwait(false);
                }

                await output.WriteLineAsync("Emparejado correctamente.").ConfigureAwait(false);
                await output.WriteLineAsync($"  Servidor: {Data(response, "server_url")}").ConfigureAwait(false);
                var (locationLine, _) = Location(response);
                await output.WriteLineAsync($"  Local: {locationLine}").ConfigureAwait(false);
                var interval = IntOrNull(response, "poll_interval");
                if (interval is { } seconds)
                {
                    await output.WriteLineAsync($"  Intervalo de poll: {seconds} s.").ConfigureAwait(false);
                }

                await output.WriteLineAsync("El servicio arrancará el heartbeat y el poller sin reiniciarse.").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            case DestinationAddCommand add:
            {
                var response = client.Request(ManagementProtocol.Operations.DestinationAdd, new
                {
                    role = add.Role,
                    label = add.Label,
                    printer = add.PrinterName,
                    copies = add.Copies,
                    enabled = add.Enabled,
                    kind = add.Kind,
                    address = add.Address,
                });
                if (!response.Ok)
                {
                    return await PrintErrorAsync(errors, response).ConfigureAwait(false);
                }

                var action = StringOrNull(response, "action") == "created" ? "creada" : "actualizada";
                await output.WriteLineAsync($"Salida «{StringOrNull(response, "role")}» {action}.").ConfigureAwait(false);
                foreach (var warning in Warnings(response))
                {
                    await output.WriteLineAsync($"  AVISO: {warning}").ConfigureAwait(false);
                }

                return ExitCodes.Ok;            }

            case DestinationListCommand:
            {
                var response = client.Request(ManagementProtocol.Operations.DestinationList);
                if (!response.Ok)
                {
                    return await PrintErrorAsync(errors, response).ConfigureAwait(false);
                }

                return await PrintDestinationListAsync(response, output).ConfigureAwait(false);
            }

            case DestinationRemoveCommand remove:
            {
                var response = client.Request(ManagementProtocol.Operations.DestinationRemove, new { role = remove.Role });
                if (!response.Ok)
                {
                    return await PrintErrorAsync(errors, response).ConfigureAwait(false);
                }

                await output.WriteLineAsync($"Salida «{remove.Role}» eliminada.").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            case StatusCommand:
            {
                var response = client.Request(ManagementProtocol.Operations.Status);
                if (!response.Ok)
                {
                    return await PrintErrorAsync(errors, response).ConfigureAwait(false);
                }

                return await PrintStatusAsync(response, output).ConfigureAwait(false);
            }

            case TestCommand test:
            {
                var response = client.Request(ManagementProtocol.Operations.Test, new { role = test.Role });
                if (!response.Ok)
                {
                    return await PrintErrorAsync(errors, response).ConfigureAwait(false);
                }

                await output.WriteLineAsync(
                    $"Impresión de prueba enviada a «{StringOrNull(response, "destination")}» " +
                    $"({IntOrNull(response, "bytes_printed")} bytes, {IntOrNull(response, "copies")} copia(s), " +
                    $"{IntOrNull(response, "elapsed_ms")} ms).").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            case DoctorCommand:
            {
                var response = client.Request(ManagementProtocol.Operations.Doctor);
                if (!response.Ok)
                {
                    return await PrintErrorAsync(errors, response).ConfigureAwait(false);
                }

                return await PrintDoctorAsync(response, output).ConfigureAwait(false);
            }

            case RestartPollerCommand:
            {
                var response = client.Request(ManagementProtocol.Operations.RestartPoller);
                if (!response.Ok)
                {
                    return await PrintErrorAsync(errors, response).ConfigureAwait(false);
                }

                await output.WriteLineAsync($"Poller reiniciado (estado: {StringOrNull(response, "poller_state")}).").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            default:
                await errors.WriteLineAsync("ERROR: comando no soportado.").ConfigureAwait(false);
                return ExitCodes.Usage;
        }
    }

    // -------------------------------------------------------------------------- formateo

    private static async Task<int> PrintDestinationListAsync(ManagementResponse response, TextWriter output)
    {
        var data = response.Data!.Value;
        var destinations = data.TryGetProperty("destinations", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().ToList()
            : [];

        if (destinations.Count == 0)
        {
            await output.WriteLineAsync("No hay salidas configuradas: crea una con «orvitaprint destination add --printer …».").ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync($"Salidas del agente ({destinations.Count}):").ConfigureAwait(false);
            foreach (var destination in destinations)
            {
                var printer = destination.TryGetProperty("printer_name", out var pn) && pn.ValueKind == JsonValueKind.String
                    ? pn.GetString()
                    : null;
                var address = destination.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.String
                    ? addr.GetString()
                    : null;
                var target = string.IsNullOrWhiteSpace(printer) ? address ?? "(sin impresora)" : printer;
                var enabled = destination.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True
                    ? "activa"
                    : "INACTIVA";
                var label = destination.TryGetProperty("label", out var lb) && lb.ValueKind == JsonValueKind.String
                    ? lb.GetString()
                    : null;
                var copies = destination.TryGetProperty("copies", out var cp) && cp.ValueKind == JsonValueKind.Number
                    ? cp.GetInt32()
                    : 1;
                var kind = destination.TryGetProperty("kind", out var kd) && kd.ValueKind == JsonValueKind.String
                    ? kd.GetString()
                    : "auto";

                await output.WriteLineAsync(
                    $"  {(destination.TryGetProperty("role", out var role) ? role.GetString() : "?")} · {(string.IsNullOrEmpty(label) ? "(sin etiqueta)" : label)} → {target} ×{copies} [{kind}, {enabled}]")
                    .ConfigureAwait(false);
            }
        }

        if (data.TryGetProperty("no_printers_note", out var note) && note.ValueKind == JsonValueKind.String)
        {
            await output.WriteLineAsync($"  {note.GetString()}").ConfigureAwait(false);
        }

        var printers = data.TryGetProperty("printers", out var ps) && ps.ValueKind == JsonValueKind.Array
            ? ps.EnumerateArray().ToList()
            : [];
        if (printers.Count > 0)
        {
            await output.WriteLineAsync($"Impresoras instaladas ({printers.Count}) — usa el nombre EXACTO en --printer:").ConfigureAwait(false);
            foreach (var printer in printers)
            {
                var isDefault = printer.TryGetProperty("default", out var def) && def.ValueKind == JsonValueKind.True
                    ? " [predeterminada]"
                    : string.Empty;
                var hint = printer.TryGetProperty("hint", out var h) && h.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(h.GetString())
                    ? $" — pista: {h.GetString()}"
                    : string.Empty;
                await output.WriteLineAsync($"  {(printer.TryGetProperty("name", out var name) ? name.GetString() : "?")}{isDefault}{hint}")
                    .ConfigureAwait(false);
            }
        }

        return ExitCodes.Ok;
    }

    private static async Task<int> PrintStatusAsync(ManagementResponse response, TextWriter output)
    {
        var data = response.Data!.Value;
        var version = data.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "?";
        await output.WriteLineAsync($"Agente Orvita Print {version} (windows).").ConfigureAwait(false);

        var paired = data.TryGetProperty("paired", out var p) && p.ValueKind == JsonValueKind.True;
        if (paired)
        {
            var (locationLine, _) = Location(response);
            await output.WriteLineAsync($"Emparejado: sí — {Data(response, "server_url")} · local {locationLine}.").ConfigureAwait(false);
            await output.WriteLineAsync($"Última vez visto por el servidor: {Date(response, "last_seen_by_server")}.").ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync("Emparejado: NO — empareja con «orvitaprint pair --server https://… <código>».").ConfigureAwait(false);
        }

        var poller = data.TryGetProperty("poller_state", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : "idle";
        var configVersion = data.TryGetProperty("config_version", out var cv) && cv.ValueKind == JsonValueKind.Number
            ? cv.GetInt64().ToString(CultureInfo.InvariantCulture)
            : "—";
        await output.WriteLineAsync($"Poller: {poller} · config_version: {configVersion} · arranque del proceso: {Date(response, "uptime_started")}.").ConfigureAwait(false);

        var pending = IntOrNull(response, "pending_local") ?? 0;
        var done = IntOrNull(response, "jobs_done") ?? 0;
        var failed = IntOrNull(response, "jobs_error") ?? 0;
        await output.WriteLineAsync($"Cola local: {pending} pendiente(s), {done} impreso(s), {failed} con error.").ConfigureAwait(false);

        var heartbeatError = StringOrNull(response, "last_heartbeat_error");
        if (!string.IsNullOrWhiteSpace(heartbeatError))
        {
            await output.WriteLineAsync($"Último error de heartbeat: {heartbeatError}").ConfigureAwait(false);
        }

        if (data.TryGetProperty("last_job", out var lastJob) && lastJob.ValueKind == JsonValueKind.Object)
        {
            var id = lastJob.TryGetProperty("id", out var jid) && jid.ValueKind == JsonValueKind.Number ? jid.GetInt64() : 0;
            var state = lastJob.TryGetProperty("state", out var jstate) && jstate.ValueKind == JsonValueKind.String ? jstate.GetString() : "?";
            var role = lastJob.TryGetProperty("role", out var jrole) && jrole.ValueKind == JsonValueKind.String ? jrole.GetString() : "?";
            var updated = lastJob.TryGetProperty("updated_at", out var jup) && jup.ValueKind == JsonValueKind.String
                ? FormatDate(jup.GetString())
                : "—";
            var duplicate = lastJob.TryGetProperty("duplicate_suspected", out var dup) && dup.ValueKind == JsonValueKind.True
                ? " · DUPLICADO SOSPECHADO (reimpresión tras caída)"
                : string.Empty;
            await output.WriteLineAsync($"Último trabajo: #{id} ({role}) — {state} [{updated}]{duplicate}").ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync("Último trabajo: (la cola está vacía).").ConfigureAwait(false);
        }

        var lastPrintError = StringOrNull(response, "last_print_error");
        if (!string.IsNullOrWhiteSpace(lastPrintError))
        {
            await output.WriteLineAsync($"Último error de impresión: {lastPrintError}").ConfigureAwait(false);
        }

        var destinations = data.TryGetProperty("destinations", out var ds) && ds.ValueKind == JsonValueKind.Array
            ? ds.EnumerateArray().ToList()
            : [];
        if (destinations.Count == 0)
        {
            await output.WriteLineAsync("Salidas: (ninguna configurada — usa orvitaprint destination add).").ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync("Salidas:").ConfigureAwait(false);
            foreach (var destination in destinations)
            {
                var printer = destination.TryGetProperty("printer_name", out var pn) && pn.ValueKind == JsonValueKind.String
                    ? pn.GetString()
                    : null;
                var target = string.IsNullOrWhiteSpace(printer)
                    ? destination.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.String ? addr.GetString() : "(sin destino)"
                    : printer;
                var enabled = destination.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True ? "activa" : "INACTIVA";
                var role = destination.TryGetProperty("role", out var r) ? r.GetString() : "?";
                var copies = destination.TryGetProperty("copies", out var cp) && cp.ValueKind == JsonValueKind.Number ? cp.GetInt32() : 1;
                await output.WriteLineAsync($"  {role} → {target} ×{copies} [{enabled}]").ConfigureAwait(false);
            }
        }

        if (data.TryGetProperty("no_printers_note", out var note) && note.ValueKind == JsonValueKind.String)
        {
            await output.WriteLineAsync($"  {note.GetString()}").ConfigureAwait(false);
        }
        else if (data.TryGetProperty("printers", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            var names = ps.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).ToList();
            await output.WriteLineAsync($"Impresoras instaladas ({names.Count}): {string.Join(", ", names)}.").ConfigureAwait(false);
        }

        return ExitCodes.Ok;
    }

    private static async Task<int> PrintDoctorAsync(ManagementResponse response, TextWriter output)
    {
        var data = response.Data!.Value;

        await output.WriteLineAsync("DIAGNÓSTICO Orvita Print (resumen básico; el ZIP completo llega con F2-12):").ConfigureAwait(false);
        await output.WriteLineAsync($"  Versión del agente: {Data(response, "version")} · .NET: {Data(response, "net_version")}").ConfigureAwait(false);
        await output.WriteLineAsync($"  Sistema: {Data(response, "os")}").ConfigureAwait(false);

        await output.WriteLineAsync("  Rutas:");
        await output.WriteLineAsync($"    Datos:      {Data(response, "data_directory")}").ConfigureAwait(false);
        await output.WriteLineAsync($"    Base de datos: {Data(response, "database_path")}").ConfigureAwait(false);
        await output.WriteLineAsync($"    Registros:  {Data(response, "log_directory")} ({IntOrNull(response, "log_files")} fichero(s) de log).").ConfigureAwait(false);

        var paired = data.TryGetProperty("paired", out var p) && p.ValueKind == JsonValueKind.True;
        if (paired)
        {
            var (locationLine, _) = Location(response);
            await output.WriteLineAsync($"  Emparejado: sí — {Data(response, "server_url")} · local {locationLine}.").ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync("  Emparejado: NO (orvitaprint pair --server https://… <código>).").ConfigureAwait(false);
        }

        await output.WriteLineAsync($"  Poller: {Data(response, "poller_state")} · cola: {IntOrNull(response, "pending_local")} pendiente(s), {IntOrNull(response, "jobs_done")} impreso(s), {IntOrNull(response, "jobs_error")} error(es).").ConfigureAwait(false);

        var printers = data.TryGetProperty("printers", out var ps) && ps.ValueKind == JsonValueKind.Array
            ? ps.EnumerateArray().ToList()
            : [];
        if (data.TryGetProperty("no_printers_note", out var note) && note.ValueKind == JsonValueKind.String)
        {
            await output.WriteLineAsync($"  {note.GetString()}").ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync($"  Impresoras instaladas ({printers.Count}).").ConfigureAwait(false);
        }

        return ExitCodes.Ok;
    }

    private static async Task<int> PrintErrorAsync(TextWriter errors, ManagementResponse response)
    {
        await errors.WriteLineAsync($"ERROR: {response.Error}").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(response.Code))
        {
            await errors.WriteLineAsync($"  (código: {response.Code})").ConfigureAwait(false);
        }

        return ExitCodes.Failure;
    }

    private static (string Line, bool Found) Location(ManagementResponse response)
    {
        if (response.Data is not { } data || !data.TryGetProperty("location", out var location) || location.ValueKind != JsonValueKind.Object)
        {
            return ("(el servidor no lo informó)", false);
        }

        var name = location.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var code = location.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var id = location.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt64().ToString(CultureInfo.InvariantCulture) : "?";

        var label = !string.IsNullOrWhiteSpace(name) ? name : (!string.IsNullOrWhiteSpace(code) ? code : $"#{id}");
        return ($"«{label}» (#{id})", true);
    }

    private static string Data(ManagementResponse response, string property) =>
        response.Data is { } data && data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "—"
            : "—";

    private static string? StringOrNull(ManagementResponse response, string property) =>
        response.Data is { } data && data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> Warnings(ManagementResponse response)
    {
        if (response.Data is not { } data
            || !data.TryGetProperty("warnings", out var warnings)
            || warnings.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. warnings.EnumerateArray()
            .Where(w => w.ValueKind == JsonValueKind.String)
            .Select(w => w.GetString()!)
            .Where(w => !string.IsNullOrWhiteSpace(w))];
    }

    private static int? IntOrNull(ManagementResponse response, string property)
    {
        if (response.Data is not { } data || !data.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt32(out var parsed) ? parsed : (int)value.GetInt64();
    }

    private static string Date(ManagementResponse response, string property) => FormatDate(StringOrNull(response, property));

    private static string FormatDate(string? iso) =>
        string.IsNullOrWhiteSpace(iso)
            ? "—"
            : DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
                : iso;
}
