using Orvita.Print.Contracts;
using Orvita.Print.Core;

// Smoke test del ciclo completo del contrato contra wp-env (HTTP en local, flag de desarrollo).
var url = new Uri(args.Length > 0 ? args[0] : "http://localhost:8888/wp-json/orp-print/v1");
var token = args.Length > 1 ? args[1] : throw new ArgumentException("Falta el token como argumento 2");

using var transport = new DirectTransport(url, token, allowInsecureHttp: true);

var health = await transport.GetHealthAsync(CancellationToken.None);
Console.WriteLine($"health: ok={health.Ok} plugin={health.PluginVersion} api={health.ApiVersion} woo={health.Woocommerce} hpos={health.Hpos}");

var config = await transport.GetConfigAsync(CancellationToken.None);
Console.WriteLine($"config: version={config.ConfigVersion} paper={config.PaperWidthMm}mm enc={config.Encoding} poll={config.PollIntervalSeconds}s");

var batch = await transport.FetchAsync(0, CancellationToken.None);
Console.WriteLine($"fetch: {batch.Jobs.Count} trabajo(s), cursor={batch.Cursor}");

foreach (var job in batch.Jobs)
{
    var claimed = await transport.ClaimAsync(job.Id, CancellationToken.None);
    Console.WriteLine($"claim {job.Id}: {(claimed is null ? "no reclamado (409/410/404)" : $"ok, attempt={claimed.Attempt}, bloques={claimed.Document?.Blocks.Count}")}");

    if (claimed is not null)
    {
        var header = claimed.Document?.Blocks.OfType<TextBlock>().FirstOrDefault()?.Value;
        Console.WriteLine($"  cabecera del documento: «{header}»");

        var result = new JobResult
        {
            Status = JobResultStatus.Delivered,
            Attempt = claimed.Attempt,
            Printer = "SMOKE-TEST (sin impresora)",
            ElapsedMs = 1,
        };
        await transport.AckAsync(claimed.Id, result, CancellationToken.None);
        Console.WriteLine($"ack {claimed.Id}: delivered ✔");

        // Segundo ack idempotente: el trabajo ya está terminal, no debe lanzar excepción.
        await transport.AckAsync(claimed.Id, result, CancellationToken.None);
        Console.WriteLine($"ack repetido {claimed.Id}: ignorado sin error ✔");
    }
}

// Claim de un trabajo inexistente → 404 → null.
var missing = await transport.ClaimAsync(999999, CancellationToken.None);
Console.WriteLine($"claim 999999 (inexistente): {(missing is null ? "null ✔" : "INESPERADO")}");

Console.WriteLine("SMOKE OK");
