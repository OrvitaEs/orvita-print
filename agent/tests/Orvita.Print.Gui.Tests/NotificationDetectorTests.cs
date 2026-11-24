using Orvita.Print.Gui.Models;
using Xunit;

namespace Orvita.Print.Gui.Tests;

/// <summary>
/// Detección de balones (GUI-03, Doc/08 §4.6): error SIEMPRE; aciertos solo sin modo
/// discreto; la primera instantánea (arranque de la GUI) no notifica nada.
/// </summary>
public sealed class NotificationDetectorTests
{
    private static StatusSnapshot Snapshot(long jobId, string state, bool duplicate = false, string? error = null) => new()
    {
        Paired = true,
        PollerRunning = true,
        LastJob = new StatusLastJob { Id = jobId, State = state, DuplicateSuspected = duplicate, Error = error },
        LastPrintError = error,
    };

    [Fact]
    public void El_arranque_no_notifica_ni_los_errores_que_ya_habia()
    {
        var detector = new NotificationDetector();

        Assert.Null(detector.Consume(Snapshot(1, "error", error: "papel atascado"), notifySuccess: true));
    }

    [Fact]
    public void Un_trabajo_que_falla_dispara_balón_de_error_siempre()
    {
        var detector = new NotificationDetector();
        detector.Consume(Snapshot(1, "printing"), notifySuccess: false);

        var balloon = detector.Consume(Snapshot(1, "error", error: "La impresora no responde"), notifySuccess: false);

        Assert.NotNull(balloon);
        Assert.True(balloon!.Error);
        Assert.Contains("#1", balloon.Message);
        Assert.Contains("no responde", balloon.Message);
    }

    [Fact]
    public void La_reimpresion_tras_corte_tambien_bota_balón()
    {
        var detector = new NotificationDetector();
        detector.Consume(Snapshot(2, "received"), notifySuccess: false);

        var balloon = detector.Consume(Snapshot(2, "received", duplicate: true), notifySuccess: false);

        Assert.NotNull(balloon);
        Assert.True(balloon!.Error);
        Assert.Contains("reimpreso tras un corte", balloon.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void En_modo_discreto_el_exito_no_notifica()
    {
        var detector = new NotificationDetector();
        detector.Consume(Snapshot(3, "printing"), notifySuccess: false);

        Assert.Null(detector.Consume(Snapshot(3, "done"), notifySuccess: false));
    }

    [Fact]
    public void Con_notificaciones_de_exito_activadas_el_exito_notifica()
    {
        var detector = new NotificationDetector();
        detector.Consume(Snapshot(3, "printing"), notifySuccess: true);

        var balloon = detector.Consume(Snapshot(3, "done"), notifySuccess: true);

        Assert.NotNull(balloon);
        Assert.False(balloon!.Error);
        Assert.Contains("impreso correctamente", balloon.Message);
    }

    [Fact]
    public void Un_segundo_error_de_otro_trabajo_notifica_otra_vez()
    {
        var detector = new NotificationDetector();
        detector.Consume(Snapshot(4, "printing"), notifySuccess: false);
        Assert.NotNull(detector.Consume(Snapshot(4, "error", error: "sin papel"), notifySuccess: false));

        detector.Consume(Snapshot(4, "printing"), notifySuccess: false); // reintento en marcha
        var segundo = detector.Consume(Snapshot(5, "error", error: "otra vez"), notifySuccess: false);

        Assert.NotNull(segundo); // trabajo DISTINTO: balón nuevo
        Assert.Contains("#5", segundo!.Message);
    }

    [Fact]
    public void El_mismo_error_persistente_no_repite_balón()
    {
        var detector = new NotificationDetector();
        detector.Consume(Snapshot(6, "printing"), notifySuccess: false);
        Assert.NotNull(detector.Consume(Snapshot(6, "error", error: "sin papel"), notifySuccess: false));

        // El poller sigue; el trabajo SIGUE en error (la instantánea no cambia): sin balón.
        Assert.Null(detector.Consume(Snapshot(6, "error", error: "sin papel"), notifySuccess: false));
    }
}
