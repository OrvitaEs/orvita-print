using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orvita.Print.Core;
using Orvita.Print.Gui.Management;
using Orvita.Print.Gui.Models;

namespace Orvita.Print.Gui.ViewModels;

/// <summary>
/// Pestaña «Configuración» (GUI-09, NUEVA): preferencias de la PROPIA GUI — el servicio se
/// configura aparte (CLI / panel). Tres tarjetas: arranque con Windows (HKCU Run, ya existía
/// en <c>WindowsAutoStart</c>), notificaciones (modo discreto, preferencia local de la GUI)
/// y una tarjeta SOLO LECTURA con las rutas del servicio (datos/registros) y su versión,
/// pedidas por la pipe vía <c>doctor</c> — la GUI no lee appsettings ni la SQLite.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IManagementClient _client;
    private bool _loaded;

    [ObservableProperty]
    private string? _serviceVersion;

    [ObservableProperty]
    private string? _dataDirectory;

    [ObservableProperty]
    private string? _logDirectory;

    [ObservableProperty]
    private string? _loadError;

    public SettingsViewModel(IManagementClient client)
    {
        _client = client;
    }

    /// <summary>Carga perezosa: solo se pide <c>doctor</c> la primera vez que se abre la pestaña.</summary>
    public void EnsureLoaded()
    {
        if (!_loaded)
        {
            Load();
        }
    }

    /// <summary>Pide el doctor por la pipe y rellena la tarjeta informativa. No lanza.</summary>
    public void Load()
    {
        ManagementResponse response;
        try
        {
            response = _client.Send(ManagementProtocol.Operations.Doctor);
        }
        catch (Exception ex) when (MainViewModel.IsPipeFault(ex))
        {
            LoadError = "El servicio Orvita Print no está en ejecución: no se pueden leer sus rutas.";
            return;
        }

        if (!response.Ok || response.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            LoadError = response.Error is { } error ? $"No se pudo leer la información del servicio: {error}" : "No se pudo leer la información del servicio.";
            return;
        }

        _loaded = true;
        LoadError = null;
        var summary = DoctorSummary.FromJson(data);
        // GUI-11: versión CORTA en pantalla (hash a 8 caracteres) para que no desborde.
        ServiceVersion = VersionText.Shorten(summary.Version);
        DataDirectory = summary.DataDirectory ?? "—";
        LogDirectory = summary.LogDirectory ?? "—";
    }

    [RelayCommand]
    public void Refresh()
    {
        _loaded = false;
        Load();
    }
}
