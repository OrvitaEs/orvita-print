using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orvita.Print.Contracts;
using Orvita.Print.Core;
using Orvita.Print.Printing.Windows;

namespace Orvita.Print.Service;

/// <summary>Resultado de una impresión de prueba (lo mostrará <c>orvitaprint test</c>).</summary>
public sealed record TestPrintResult(string Destination, int Copies, long ElapsedMs, int BytesPrinted);

/// <summary>
/// Impresión de prueba (<c>orvitaprint test</c>, Doc/03 §9 y §6.5, tarea F2-05): documento
/// fijo de <see cref="TestTicket"/> renderizado con <see cref="EscPosRenderer"/> y enviado por
/// el MISMO transporte que un trabajo real. La expondrá la pipe de gestión (F2-08) al CLI.
/// </summary>
public sealed class TestPrintService
{
    private readonly IDestinationProvider _destinations;
    private readonly IPrintTransportFactory _transportFactory;
    private readonly RemoteConfigState _configState;
    private readonly ILogger _logger;

    public TestPrintService(
        IDestinationProvider destinations,
        IPrintTransportFactory transportFactory,
        RemoteConfigState configState,
        ILogger? logger = null)
    {
        _destinations = destinations;
        _transportFactory = transportFactory;
        _configState = configState;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>Imprime el ticket de prueba en la salida <paramref name="role"/> (o «default»). Lanza <see cref="PrintTransportException"/> si la salida no existe.</summary>
    public async Task<TestPrintResult> PrintAsync(string? role, CancellationToken cancellationToken)
    {
        var wantedRole = string.IsNullOrWhiteSpace(role) ? "default" : role.Trim();
        var destination = _destinations.Resolve(wantedRole);
        if (destination is null || !destination.Enabled)
        {
            throw new PrintTransportException(
                PrintFailureKind.PrinterNotFound,
                $"La salida «{wantedRole}» no existe o está desactivada en este agente. " +
                "Configúrala con orvitaprint destination add (Doc/03 §9).");
        }

        var document = TestTicket.Create(destination.Label);
        var bytes = new EscPosRenderer(
            _configState.ToRendererOptions(),
            message => _logger.LogWarning("{Message}", message)).Render(document);

        var transport = _transportFactory.Create(destination);
        var clock = Stopwatch.StartNew();
        await transport.PrintAsync(
            new PrintRequest
            {
                JobId = 0,
                Destination = destination,
                Document = document,
                EscPos = bytes,
                Copies = Math.Max(1, destination.Copies),
            },
            cancellationToken).ConfigureAwait(false);

        var result = new TestPrintResult(
            destination.PrinterName ?? destination.Address ?? destination.Role,
            Math.Max(1, destination.Copies),
            clock.ElapsedMilliseconds,
            bytes.Length);
        _logger.LogInformation(
            "Impresión de prueba enviada a «{Destination}» ({Bytes} bytes, {Copies} copia(s), {Elapsed} ms).",
            result.Destination, result.BytesPrinted, result.Copies, result.ElapsedMs);
        return result;
    }
}
