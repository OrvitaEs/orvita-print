using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orvita.Print.Core;
using Orvita.Print.Printing.Windows;
using Orvita.Print.Service;
using Serilog;
using Serilog.Events;

namespace Orvita.Print.Service;

/// <summary>
/// Cableado del host (tarea F2-07). El mismo ejecutable corre como servicio de Windows
/// (instalador F3, ADR-09) y en consola para desarrollo/humo: <c>AddWindowsService</c> solo
/// aplica su ciclo de vida cuando el proceso es un servicio real (padre services.exe);
/// desde un terminal es consola normal — no hace falta detección propia.
///
/// Argumentos de DESARROLLO (no persistentes; en producción la URL/token viven cifrados en
/// SQLite, F2-10/S-01): <c>--server &lt;url&gt; --token &lt;token&gt; [--allow-insecure-http]</c>
/// para wp-env, y <c>--data-dir &lt;ruta&gt;</c> para no tocar ProgramData. El token NUNCA se
/// registra en el log (S-05).
/// </summary>
public static class AgentHostBuilder
{
    public const string ServiceName = "OrvitaPrint";

    public static IHost BuildHost(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder(args);

        var options = builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
        ApplyDevelopmentArgs(options, args);

        // ------------------------------------------------------ logging (Doc/03 §8 y §11)
        var logDirectory = Path.Combine(options.ResolveDataDirectory(), "logs");
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(ParseLevel(builder.Configuration["Serilog:MinimumLevel"]))
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(logDirectory, "orvitaprint-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7, // rotación diaria, 7 días (Doc/03 §11)
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        builder.Services.AddSerilog();

        // ------------------------------------------------------ DI (cableado del agente)
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<AgentRuntimeStatus>();
        builder.Services.AddSingleton<PairingWaiter>();
        builder.Services.AddSingleton<ServerConnection>();
        builder.Services.AddSingleton<RemoteConfigState>();

        // Almacén local F2-10: MISMA BD que la cola (un fichero, un respaldo), tablas propias.
        builder.Services.AddSingleton(sp =>
        {
            var store = new LocalConfigStore(Path.Combine(options.ResolveDataDirectory(), "outbox.db"))
            {
                Log = message => sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Orvita.Print.Core.LocalConfigStore")
                    .LogInformation("{Message}", message),
            };
            return store;
        });

        // S-01: DPAPI (LocalMachine + entropía fija) — el único punto que conoce ProtectedData.
        builder.Services.AddSingleton<ISecretProtector, DpapiSecretProtector>();

        // F2-10: las salidas viven en el almacén local (sustituye a la lectura de appsettings).
        builder.Services.AddSingleton<IDestinationProvider, StoreDestinationProvider>();
        builder.Services.AddSingleton<IPrinterEnumerator, WindowsPrinterEnumerator>();
        builder.Services.AddSingleton<IPrintTransportFactory, WindowsPrintTransportFactory>();
        builder.Services.AddSingleton<IPngDecoder, PngDecoder>();
        builder.Services.AddSingleton<Func<ITransport>>(sp =>
        {
            var connection = sp.GetRequiredService<ServerConnection>();
            return () => connection.Transport
                         ?? throw new InvalidOperationException("El agente no está emparejado: falta ServerUrl o token.");
        });
        builder.Services.AddSingleton<Func<DirectTransport>>(sp =>
        {
            var connection = sp.GetRequiredService<ServerConnection>();
            return () => connection.Transport
                         ?? throw new InvalidOperationException("El agente no está emparejado: falta ServerUrl o token.");
        });
        builder.Services.AddSingleton(sp =>
        {
            var outbox = new OutboxRepository(Path.Combine(options.ResolveDataDirectory(), "outbox.db"))
            {
                Log = message => sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Orvita.Print.Core.OutboxRepository")
                    .LogInformation("{Message}", message),
            };
            return outbox;
        });
        builder.Services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<JobProcessor>>();
            var configState = sp.GetRequiredService<RemoteConfigState>();
            return new JobProcessor(
                sp.GetRequiredService<Func<ITransport>>(),
                sp.GetRequiredService<OutboxRepository>(),
                sp.GetRequiredService<IDestinationProvider>(),
                sp.GetRequiredService<IPrintTransportFactory>(),
                rendererOptions: configState.ToRendererOptions,
                pngDecoder: sp.GetRequiredService<IPngDecoder>(),
                options: new JobProcessorOptions
                {
                    MaxTransientRetries = Math.Max(1, options.TransientPrintRetries),
                    TransientRetryDelay = TimeSpan.FromSeconds(Math.Max(1, options.TransientRetryDelaySeconds)),
                },
                logger: logger);
        });
        builder.Services.AddSingleton(sp => new HeartbeatSender(
            sp.GetRequiredService<Func<DirectTransport>>(),
            sp.GetRequiredService<OutboxRepository>(),
            sp.GetRequiredService<IDestinationProvider>(),
            sp.GetRequiredService<RemoteConfigState>(),
            sp.GetRequiredService<ILogger<HeartbeatSender>>(),
            sp.GetRequiredService<AgentRuntimeStatus>()));
        builder.Services.AddSingleton<TestPrintService>();

        // F2-08: emparejamiento por pipe + dueño del poller (re-pair/reinicio sin tirar el proceso).
        builder.Services.AddSingleton<PairingService>();
        builder.Services.AddSingleton(sp => new PollerController(
            sp.GetRequiredService<Func<ITransport>>(),
            sp.GetRequiredService<JobProcessor>(),
            intervalSeconds: () => sp.GetRequiredService<RemoteConfigState>().PollIntervalSeconds
                ?? sp.GetRequiredService<AgentRuntimeStatus>().PairPollIntervalSeconds
                ?? sp.GetRequiredService<AgentOptions>().PollIntervalSeconds,
            logger: sp.GetRequiredService<ILogger<PollerController>>()));
        builder.Services.AddSingleton<ManagementRequestHandler>();
        // Singleton explícito: el UpdateApplier (F2-11) debe poder parar la pipe limpiamente.
        builder.Services.AddSingleton<ManagementPipeServer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ManagementPipeServer>());

        // F2-11: canal de actualización — tarea en segundo plano con token propio, 1×/día.
        builder.Services.AddSingleton<UpdateStatus>();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<AgentOptions>().Update);
        builder.Services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<AgentOptions>();
            return new UpdateCheckerOptions
            {
                ManifestUrl = options.Update.ManifestUrl,
                // Clave pública EMBEBIDA (no configuración): quien pudiera cambiarla invalidaría la cadena.
                PublicKeyBase64 = UpdateTrust.ReleasePublicKeyBase64,
            };
        });
        builder.Services.AddSingleton<IUpdateChecker>(sp => new UpdateChecker(
            sp.GetRequiredService<UpdateCheckerOptions>(),
            verifier: new Ed25519Verifier(),
            log: message => sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Orvita.Print.Core.UpdateChecker")
                .LogInformation("{Message}", message)));
        builder.Services.AddSingleton<IUpdateApplier>(sp => new UpdateApplier(
            sp.GetRequiredService<PollerController>(),
            sp.GetRequiredService<ManagementPipeServer>(),
            sp.GetRequiredService<UpdateStatus>(),
            logger: sp.GetRequiredService<ILogger<UpdateApplier>>()));
        builder.Services.AddSingleton<UpdateOrchestrator>();
        builder.Services.AddHostedService<UpdateService>();

        // F2-12: ZIP de diagnóstico (doctor --zip), con el estado de updates dentro del informe.
        builder.Services.AddSingleton<DiagnosticsBundleBuilder>();

        builder.Services.AddHostedService<AgentWorker>();
        builder.Services.AddWindowsService(windowsServiceOptions =>
        {
            windowsServiceOptions.ServiceName = ServiceName;
        });

        configureServices?.Invoke(builder.Services);

        return builder.Build();
    }

    private static void ApplyDevelopmentArgs(AgentOptions options, string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server" when i + 1 < args.Length:
                    options.Server.Url = args[++i];
                    break;
                case "--token" when i + 1 < args.Length:
                    options.Server.Token = args[++i]; // solo en memoria (S-05: nunca a log)
                    break;
                case "--allow-insecure-http":
                    options.Server.AllowInsecureHttp = true; // S-03: solo desarrollo (wp-env)
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    options.DataDirectory = args[++i];
                    break;
                case "--console":
                    // Marcador inofensivo, documentado para lanzar en consola de forma explícita.
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Argumento no reconocido: {args[i]}");
                    }

                    break;
            }
        }
    }

    private static LogEventLevel ParseLevel(string? level) =>
        Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsed) ? parsed : LogEventLevel.Information;
}
