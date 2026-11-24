using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Orvita.Print.Gui.Management;
using Orvita.Print.Gui.Models;
using Orvita.Print.Gui.Preferences;
using Orvita.Print.Gui.Tray;
using Orvita.Print.Gui.ViewModels;

namespace Orvita.Print.Gui.Views;

/// <summary>
/// Ventana única de la GUI (GUI-01/03/04, Doc/08 §4): pestañas + barra de estado + icono de
/// bandeja. Temporizadores: <c>status</c> cada 5 s mientras la GUI está viva (la bandeja
/// refleja el estado sin abrir la ventana) y cola cada 3 s SOLO mientras la ventana está
/// ABIERTA (con la ventana cerrada u oculta, <see cref="QueueViewModel.WindowVisible"/> es
/// false y NO se pregunta por la pipe: CPU ~0, Doc/08 §8).
///
/// Cerrar la ventana la OCULTA al tray (el icono sigue visible); «Salir» del menú de bandeja
/// termina la GUI — y NUNCA toca el servicio ni el poller (Doc/08 §1.3).
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _queueTimer;
    private readonly TrayController? _tray;
    private readonly WindowsBalloonNotifier? _balloons;
    private bool _exitRequested;

    public MainWindow()
        : this(BuildDefaultViewModel())
    {
    }

    /// <summary>Ctor inyectado (pruebas headless: sin tray real ni registro).</summary>
    public MainWindow(MainViewModel viewModel, bool enableTray = true)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.BalloonRequested += OnBalloonRequested;

        // Bandeja (GUI-01/03): icono + menú. En pruebas headless se omite (sin shell real).
        if (enableTray)
        {
            _tray = new TrayController(
                openWindow: ShowFromTray,
                printTest: PrintTestFromTrayAsync,
                diagnostics: DiagnosticsFromTrayAsync,
                exit: ExitApplication);
            _balloons = new WindowsBalloonNotifier();
        }

        _statusTimer = new DispatcherTimer { Interval = MainViewModel.StatusInterval };
        _statusTimer.Tick += async (_, _) => await RefreshStatusTickAsync().ConfigureAwait(true);

        _queueTimer = new DispatcherTimer { Interval = QueueViewModel.RefreshInterval };
        _queueTimer.Tick += (_, _) =>
        {
            if (_viewModel.Queue.ShouldAutoRefresh())
            {
                _viewModel.Queue.Refresh();
            }
        };
    }

    internal MainViewModel ViewModel => _viewModel;

    private static MainViewModel BuildDefaultViewModel()
    {
        var preferences = new RegistryPreferenceStore();
        // GUI-07: auto-arranque con la sesión del usuario, activado por defecto SOLO en el
        // primer arranque (el MSI per-máquina no puede escribir el HKCU de cada usuario).
        WindowsAutoStart.ApplyFirstRunDefault(preferences);
        return new MainViewModel(new ManagementGuiClient(), preferences);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _viewModel.Queue.WindowVisible = true;
        _viewModel.Queue.Refresh();
        _ = RefreshStatusTickAsync();
        _statusTimer.Start();
        _queueTimer.Start();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_exitRequested)
        {
            return; // salida real desde el menú de bandeja
        }

        // Ocultar al tray: la GUI sigue mostrando el estado (y el servicio, vivo en sesión 0).
        e.Cancel = true;
        Hide();
        _viewModel.Queue.WindowVisible = false;
        _queueTimer.Stop(); // sin ventana: CERO polling de cola (Doc/08 §8)
    }

    private async Task RefreshStatusTickAsync()
    {
        // El refresco de status toca la pipe y toca el VM en el hilo de UI (temporizador).
        await Task.Run(_viewModel.RefreshStatus);
        if (_tray is not null)
        {
            _tray.Update(_viewModel.TrayState, _viewModel.TrayTooltip);
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _viewModel.Queue.WindowVisible = true;
        _viewModel.Queue.Refresh();
        _queueTimer.Start();
    }

    private async Task PrintTestFromTrayAsync()
    {
        var ok = await Task.Run(_viewModel.PrintTest);
        OnBalloonRequested(new TrayBalloon(
            ok ? "Orvita Print · prueba enviada" : "Orvita Print · prueba fallida",
            ok
                ? "Ticket de prueba enviado a la impresora de la salida por defecto."
                : "No se pudo imprimir el ticket de prueba: abre la ventana para ver el detalle.",
            Error: !ok));
    }

    private async Task DiagnosticsFromTrayAsync()
    {
        ShowFromTray();
        var zip = await Task.Run(_viewModel.CreateDiagnosticZip);
        if (zip is null)
        {
            OnBalloonRequested(new TrayBalloon(
                "Orvita Print · diagnóstico",
                "No se pudo generar el ZIP de diagnóstico: revisa la pestaña Diagnóstico.",
                Error: true));
        }
    }

    private void OnBalloonRequested(TrayBalloon balloon)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            _balloons?.Show(balloon.Title, balloon.Message, balloon.Error);
        }
        else
        {
            Dispatcher.UIThread.Post(() => _balloons?.Show(balloon.Title, balloon.Message, balloon.Error));
        }
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        _viewModel.Queue.WindowVisible = false;
        _statusTimer.Stop();
        _queueTimer.Stop();
        Close();
        Environment.ExitCode = 0;
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?
            .Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _statusTimer.Stop();
        _queueTimer.Stop();
        _viewModel.Queue.WindowVisible = false;
        _tray?.Dispose();
        _balloons?.Dispose();
    }
}
