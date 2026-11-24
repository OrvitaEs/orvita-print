using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Orvita.Print.Gui.Models;

namespace Orvita.Print.Gui.Tray;

/// <summary>
/// Dueño del icono de bandeja (GUI-01/03, Doc/08 §4.1): crea el <c>TrayIcon</c> de Avalonia,
/// su menú nativo (Abrir, Imprimir prueba, Diagnóstico, Salir) y actualiza icono y tooltip
/// desde el estado derivado por <see cref="TrayStateMapper"/>. «Salir» SOLO cierra la GUI:
/// nunca toca el servicio ni el poller (Doc/08 §1.3).
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly TrayIcon _icon;
    private readonly Action _openWindow;
    private readonly Func<Task> _printTest;
    private readonly Func<Task> _diagnostics;
    private readonly Action _exit;

    public TrayController(
        Action openWindow,
        Func<Task> printTest,
        Func<Task> diagnostics,
        Action exit)
    {
        _openWindow = openWindow;
        _printTest = printTest;
        _diagnostics = diagnostics;
        _exit = exit;

        _icon = new TrayIcon
        {
            Icon = TrayIcons.For(TrayState.ServiceDown),
            ToolTipText = "Orvita Print",
            IsVisible = true,
            Menu = BuildMenu(),
        };
        _icon.Clicked += OnClicked;
        TrayIcon.SetIcons(Application.Current!, [_icon]);
    }

    private void OnClicked(object? sender, EventArgs e) => _openWindow();

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Abrir");
        open.Click += (_, _) => _openWindow();
        var test = new NativeMenuItem("Imprimir prueba");
        test.Click += async (_, _) => await _printTest().ConfigureAwait(false);
        var doctor = new NativeMenuItem("Diagnóstico (ZIP)");
        doctor.Click += async (_, _) => await _diagnostics().ConfigureAwait(false);
        var exit = new NativeMenuItem("Salir");
        exit.Click += (_, _) => _exit();

        menu.Items.Add(open);
        menu.Items.Add(test);
        menu.Items.Add(doctor);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    /// <summary>Aplica estado+tooltip derivados (GUI-03: la bandeja refleja el estado sin abrir la ventana).</summary>
    public void Update(TrayState state, string tooltip)
    {
        _icon.Icon = TrayIcons.For(state);
        _icon.ToolTipText = tooltip;
    }

    public void Dispose()
    {
        _icon.Clicked -= OnClicked;
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
