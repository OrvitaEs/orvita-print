using Avalonia;
using System;

namespace Orvita.Print.Gui;

/// <summary>
/// Arranque de la GUI de bandeja (GUI-01, Doc/08 §2): instancia única por máquina (mutex
/// global) y lifetime de escritorio clásico — la ventana es la ÚNICA de la app. Cerrar la
/// ventana NO toca el servicio: solo termina la GUI (el poller vive en el servicio, sesión 0).
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Segunda instancia: el usuario volvió a hacer doble clic — la primera ya está en el
        // tray; abrimos la ventana de esa y salimos en silencio.
        if (!SingleInstance.TryAcquire(out var guard))
        {
            return;
        }

        using (guard)
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
