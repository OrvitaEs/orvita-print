using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Orvita.Print.Core;
using Orvita.Print.Gui.Management;
using Orvita.Print.Gui.Models;
using Orvita.Print.Gui.Preferences;

namespace Orvita.Print.Gui.ViewModels;

/// <summary>
/// ViewModel raíz de la ventana única (Doc/08 §4): barra de estado, estado del icono de
/// bandeja y las cuatro pestañas. Toda operación pasa por la pipe (regla de hierro de
/// Doc/08 §1); la GUI no abre la SQLite ni lee appsettings.
///
/// Polling (Doc/08 §2): <c>status</c> cada 5 s mientras la GUI está viva — el icono de
/// bandeja refleja el estado sin abrir la ventana. La cola (3 s) SOLO con la ventana
/// abierta: lo gestiona <see cref="QueueViewModel"/> con su propio temporizador.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>Intervalo de refresco de <c>status</c> (Doc/08 §2: cada 5 s).</summary>
    public static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(5);

    private readonly IManagementClient _client;
    private readonly NotificationDetector _notifications = new();
    private readonly IPreferenceStore _preferences;

    /// <summary>Preferencias locales de la GUI en fichero JSON propio (%LocalAppData%\OrvitaPrint\gui.json).</summary>
    private readonly IPreferenceStore _localPreferences;

    private StatusSnapshot? _status;

    [ObservableProperty]
    private bool _serviceDown = true;

    /// <summary>Punto de la barra de estado: verde solo con conexión buena (GUI-09).</summary>
    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private TrayState _trayState = TrayState.ServiceDown;

    [ObservableProperty]
    private string _trayTooltip = "El servicio Orvita Print no está en ejecución";

    [ObservableProperty]
    private string _statusBarText = "Consultando el servicio…";

    [ObservableProperty]
    private bool _notifySuccess;

    [ObservableProperty]
    private bool _autoStart;

    public QueueViewModel Queue { get; }

    public DestinationsViewModel Destinations { get; }

    public PairingViewModel Pairing { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    /// <summary>Pestaña «Configuración» (GUI-09): preferencias de la GUI + rutas del servicio.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Pestaña «Acerca de» (GUI-09): versiones y datos del producto (solo lectura).</summary>
    public AboutViewModel About { get; }

    /// <summary>Instantánea vigente de status (la reutilizan las pestañas).</summary>
    public StatusSnapshot? Status => _status;

    /// <summary>La vista muestra el balón en el tray; el VM no conoce Windows.</summary>
    public event Action<TrayBalloon>? BalloonRequested;

    /// <summary>
    /// Ctor con dos almacenes: <paramref name="preferences"/> (registro HKCU, arranque con
    /// Windows) y <paramref name="localPreferences"/> (gui.json, notificaciones). Si no se
    /// pasa el local, se usa el mismo almacén (comportamiento anterior: pruebas).
    /// </summary>
    public MainViewModel(IManagementClient client, IPreferenceStore preferences, IPreferenceStore? localPreferences = null)
    {
        _client = client;
        _preferences = preferences;
        _localPreferences = localPreferences ?? preferences;

        _notifySuccess = _localPreferences.GetBool("NotifySuccess", fallback: false);
        _autoStart = WindowsAutoStart.IsEnabled();

        Queue = new QueueViewModel(client);
        Destinations = new DestinationsViewModel(client);
        Pairing = new PairingViewModel(client);
        Diagnostics = new DiagnosticsViewModel(client);
        Settings = new SettingsViewModel(client);
        About = new AboutViewModel();
    }

    /// <summary>
    /// Refresca el estado con una llamada <c>status</c> (5 s). Devuelve el balón a mostrar
    /// (o null). Nunca lanza — el servicio caído es un estado visible, no una excepción.
    /// </summary>
    public TrayBalloon? RefreshStatus()
    {
        ManagementResponse? response;
        try
        {
            response = _client.Send(ManagementProtocol.Operations.Status);
        }
        catch (Exception ex) when (IsPipeFault(ex))
        {
            ServiceDown = true;
            IsConnected = false;
            ApplyTray(null);
            return null;
        }

        ServiceDown = false;
        if (!response.Ok || response.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            ApplyTray(null);
            return null;
        }

        var snapshot = StatusSnapshot.FromJson(data);
        _status = snapshot;

        // Reparte la instantánea a las pestañas que la enriquecen.
        Pairing.ApplyStatus(snapshot);
        About.ApplyStatus(snapshot);

        ApplyTray(snapshot);
        var balloon = _notifications.Consume(snapshot, NotifySuccess);
        if (balloon is not null)
        {
            BalloonRequested?.Invoke(balloon);
        }

        return balloon;
    }

    /// <summary>Imprimir prueba desde el menú de bandeja (misma op que el CLI, Doc/08 §4.1).</summary>
    public bool PrintTest()
    {
        try
        {
            return _client.Send(ManagementProtocol.Operations.Test, null).Ok;
        }
        catch (Exception ex) when (IsPipeFault(ex))
        {
            return false;
        }
    }

    /// <summary>Diagnóstico rápido desde el menú de bandeja: genera el ZIP y devuelve su ruta.</summary>
    public string? CreateDiagnosticZip()
    {
        try
        {
            var response = _client.Send(ManagementProtocol.Operations.Doctor, new { zip = true });
            return response.Ok && response.Data is { } data && data.TryGetProperty("zip_path", out var zip)
                ? zip.GetString()
                : null;
        }
        catch (Exception ex) when (IsPipeFault(ex))
        {
            return null;
        }
    }

    partial void OnNotifySuccessChanged(bool value) => _localPreferences.SetBool("NotifySuccess", value);

    partial void OnAutoStartChanged(bool value)
    {
        _preferences.SetBool("AutoStart", value);
        try
        {
            WindowsAutoStart.SetEnabled(value);
        }
        catch (PlatformNotSupportedException)
        {
            // Solo-Windows en V1: en otra plataforma la casilla no aplica.
        }
    }

    private void ApplyTray(StatusSnapshot? snapshot)
    {
        var now = DateTimeOffset.Now;
        var (state, tooltip) = TrayStateMapper.Map(new TrayInputs
        {
            ServiceReachable = snapshot is not null,
            Paired = snapshot?.Paired ?? false,
            PollerRunning = snapshot?.PollerRunning ?? false,
            LastSeenByServer = snapshot?.LastSeenByServer,
            LastHeartbeatError = snapshot?.LastHeartbeatError,
            RecentIncident = TrayInputsBuilder.HasRecentIncident(snapshot, now, TimeSpan.FromHours(24)),
            Now = now,
        });

        TrayState = state;
        TrayTooltip = tooltip;
        StatusBarText = StatusBarLine(snapshot, state);
        // Punto de la barra de estado (GUI-09): verde SOLO con el servicio emparejado y el
        // poller corriendo; rojo en cualquier otro caso (caído, sin emparejar, sin conexión,
        // con incidencia abierta).
        IsConnected = state == TrayState.Running;
    }

    /// <summary>Barra de estado de la ventana (Doc/08 §4.6: «una sola barra de estado»). Con
    /// conexión buena añade la hora de la última respuesta de la tienda (GUI-09).</summary>
    private static string StatusBarLine(StatusSnapshot? status, TrayState state) => state switch
    {
        TrayState.ServiceDown => "El servicio Orvita Print no está en ejecución",
        TrayState.Unpaired => "Sin emparejar: ve a la pestaña «Emparejamiento»",
        TrayState.Incident => $"Incidencia de impresión — {status?.LastPrintError ?? "abre la cola para ver detalles"}",
        TrayState.NoConnection => "Sin conexión con la tienda; reintentando",
        _ => status is null
            ? "Emparejado: imprimiendo normalmente"
            : $"Emparejado con «{status.LocationName ?? status.LocationCode ?? status.ServerUrl}» · pendientes: {status.PendingLocal} · impresas: {status.JobsDone} · errores: {status.JobsError}"
              + (status.LastSeenByServer is { } seen ? $" · última respuesta {seen.ToLocalTime():HH:mm}" : string.Empty),
    };

    internal static bool IsPipeFault(Exception ex) =>
        ex is ServiceNotRunningException or IOException or UnauthorizedAccessException or ObjectDisposedException;
}
