namespace Orvita.Print.Gui.Models;

/// <summary>Balón a mostrar (Doc/08 §4.6): error SIEMPRE, impresión correcta configurable.</summary>
public sealed record TrayBalloon(string Title, string Message, bool Error);

/// <summary>
/// Detección de notificaciones (GUI-03, Doc/08 §4.6) — PURA y testeable: compara la
/// instantánea de <c>status</c> anterior con la actual y decide qué balón toca.
/// <list type="bullet">
/// <item>Al FALLAR un trabajo (siempre): un trabajo pasa a <c>error</c>, o aparece
/// <c>duplicate_suspected</c> (reimpresión tras corte, Doc/03 §6.4).</item>
/// <item>Al IMPRIMIR un trabajo: solo si el usuario activó las notificaciones de éxito
/// (modo discreto por defecto: cero balones por aciertos, Doc/08 §4.6).</item>
/// <item>La primera instantánea NO notifica: la GUI arranca con lo que ya hay — un fallo
/// de ayer no dispara un balón al iniciar sesión.</item>
/// </list>
/// </summary>
public sealed class NotificationDetector
{
    private long? _lastJobId;
    private string? _lastState;
    private bool _lastDuplicate;
    private bool _primed;

    /// <summary>Devuelve el balón a mostrar para esta instantánea o null.</summary>
    /// <param name="snapshot">Instantánea recién llegada de la pipe.</param>
    /// <param name="notifySuccess">Preferencia del usuario (false = modo discreto).</param>
    public TrayBalloon? Consume(StatusSnapshot snapshot, bool notifySuccess = false)
    {
        var job = snapshot.LastJob;
        if (job?.Id is not { } jobId)
        {
            return null; // sin trabajos: nada que notificar
        }

        if (!_primed)
        {
            _primed = true;
            Remember(jobId, job);
            return null; // el arranque no notifica nada
        }

        var sameJob = jobId == _lastJobId;
        var wasIncident = _lastState is "error" || _lastDuplicate;
        var wasDone = _lastState == "done";
        var isIncident = job.State is "error" || job.DuplicateSuspected;

        Remember(jobId, job);

        if (isIncident && (!sameJob || !wasIncident))
        {
            return new TrayBalloon(
                "Orvita Print · incidencia de impresión",
                job.DuplicateSuspected
                    ? $"Trabajo #{jobId} reimpreso tras un corte: puede salir duplicado."
                    : $"Trabajo #{jobId} falló: {job.Error ?? snapshot.LastPrintError ?? "error de impresión"}.",
                Error: true);
        }

        if (!isIncident && job.State == "done" && (!sameJob || !wasDone))
        {
            return notifySuccess
                ? new TrayBalloon("Orvita Print · trabajo impreso", $"Trabajo #{jobId} impreso correctamente.", Error: false)
                : null; // modo discreto: la barra de estado de la ventana es la que cuenta
        }

        return null;
    }

    private void Remember(long jobId, StatusLastJob job)
    {
        _lastJobId = jobId;
        _lastState = job.State;
        _lastDuplicate = job.DuplicateSuspected;
    }
}
