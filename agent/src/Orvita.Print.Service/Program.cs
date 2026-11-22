using Orvita.Print.Service;
using Microsoft.Extensions.Hosting;

// Orvita Print — servicio headless (ADR-09/13). Todo el cableado vive en AgentHostBuilder
// (es lo que prueba el humo y lo que reutilizará la pipe de gestión, F2-08).

using var host = AgentHostBuilder.BuildHost(args);
await host.RunAsync();
