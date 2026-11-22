using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Service;
using Xunit;

namespace Orvita.Print.Service.Tests;

/// <summary>
/// Humo del host en modo consola (misma compilación que el servicio, ADR-09): arranca y para
/// sin emparejar, crea la cola en el directorio de datos y deja el log en su sitio.
/// No instala ningún servicio de Windows (eso es F3, requiere admin).
/// </summary>
public sealed class HostSmokeTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"orp-service-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // El logger estático de Serilog puede tardar un instante en soltar el fichero:
            // la limpieza de %TEMP% es de buena fe, no del test.
        }
    }

    [Fact]
    public async Task Host_sin_emparejar_arranca_crea_cola_y_para_limpio()
    {
        using var host = AgentHostBuilder.BuildHost(
            ["--data-dir", _dataDirectory, "--console"]);

        await host.StartAsync();
        await Task.Delay(250); // deja correr el ExecuteAsync (recuperación + aviso de sin emparejar)
        await host.StopAsync();

        Assert.True(File.Exists(Path.Combine(_dataDirectory, "outbox.db")), "La cola local debe crearse al arrancar.");
        Assert.True(Directory.Exists(Path.Combine(_dataDirectory, "logs")), "El directorio de logs debe existir tras arrancar.");
    }

    [Fact]
    public async Task Host_emparejado_arranca_poller_y_heartbeat_con_transporte_falso()
    {
        // Emparejado por argumentos de desarrollo (URL+token): la puerta del worker se abre y
        // el transporte REAL de servidor se sustituye por el falso vía DI (el poller hace fetch).
        var server = new FakeServerTransport();
        using var host = AgentHostBuilder.BuildHost(
            [
                "--data-dir", _dataDirectory, "--console",
                "--server", "https://tienda.test/wp-json/orp-print/v1",
                "--token", "orp_token_de_prueba",
            ],
            services =>
            {
                services.RemoveAll<Func<ITransport>>();
                services.RemoveAll<Func<DirectTransport>>();
                services.AddSingleton<Func<ITransport>>(() => server);
                services.AddSingleton<Func<DirectTransport>>(() => throw new InvalidOperationException("sin heartbeat real en esta prueba"));
            });

        await host.StartAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (server.FetchCalls == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        await host.StopAsync();

        Assert.True(server.FetchCalls >= 1, "El poller debería haber hecho al menos un fetch al arrancar.");
    }
}
