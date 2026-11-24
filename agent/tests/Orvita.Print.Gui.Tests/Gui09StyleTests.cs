using Avalonia.Media;
using Orvita.Print.Core;
using Orvita.Print.Gui.Management;
using Orvita.Print.Gui.Models;
using Orvita.Print.Gui.Preferences;
using Orvita.Print.Gui.ViewModels;
using Xunit;

namespace Orvita.Print.Gui.Tests;

/// <summary>
/// GUI-09 (restyle): pruebas de las piezas nuevas — set de iconos embebido, punto de la barra
/// de estado, viewmodels de Configuración y Acerca de, y el almacén de preferencias JSON de
/// la GUI. La lógica de pipe/estados NO cambia (509 pruebas anteriores se conservan).
/// </summary>
public sealed class Gui09StyleTests
{
    private readonly FakeManagementClient _client = new();

    // ------------------------------------------------------------- iconos embebidos

    [Fact]
    public void El_set_de_iconos_embebido_esta_completo_y_parsea_como_geometria()
    {
        string[] keys =
        [
            "Icon.Printer", "Icon.List", "Icon.Link", "Icon.Pulse", "Icon.Gear", "Icon.Info",
            "Icon.Refresh", "Icon.Download", "Icon.Folder", "Icon.Warning", "Icon.CheckCircle",
            "Icon.Dot", "Icon.Store", "Icon.Document", "Icon.Save", "Icon.Edit", "Icon.Trash",
        ];

        Assert.NotNull(Avalonia.Application.Current);
        foreach (var key in keys)
        {
            var found = Avalonia.Application.Current!.TryGetResource(key, null, out var value);
            Assert.True(found, $"Falta el icono «{key}» en Views/Icons.axaml");
            Assert.IsType<StreamGeometry>(value);
        }
    }

    // --------------------------------------------------------- punto de la barra de estado

    [Fact]
    public void El_punto_de_estado_es_verde_solo_con_conexion_buena()
    {
        _client.RespondOk(ManagementProtocol.Operations.Status, new
        {
            paired = true,
            poller_running = true,
            location = new { name = "Cafetería Centro" },
            last_seen_by_server = DateTimeOffset.UtcNow.ToString("O"),
        });

        var vm = new MainViewModel(_client, new InMemoryPreferenceStore());
        vm.RefreshStatus();
        Assert.True(vm.IsConnected);

        // Sin conexión del poller con la tienda → rojo.
        _client.RespondOk(ManagementProtocol.Operations.Status, new
        {
            paired = true,
            poller_running = true,
            last_heartbeat_error = "404",
        });
        vm.RefreshStatus();
        Assert.False(vm.IsConnected);

        // Servicio caído → rojo.
        _client.ThrowOnNextSend = new ServiceNotRunningException(ManagementProtocol.PipeName);
        vm.RefreshStatus();
        Assert.False(vm.IsConnected);
    }

    // ------------------------------------------------------ Configuración (viewmodel nuevo)

    [Fact]
    public void Configuracion_lee_version_y_rutas_del_doctor()
    {
        _client.RespondOk(ManagementProtocol.Operations.Doctor, new
        {
            version = "1.0.0-dev",
            data_directory = @"C:\ProgramData\OrvitaPrint",
            log_directory = @"C:\ProgramData\OrvitaPrint\logs",
        });

        var settings = new SettingsViewModel(_client);
        settings.EnsureLoaded();

        Assert.Null(settings.LoadError);
        Assert.Equal("1.0.0-dev", settings.ServiceVersion);
        Assert.Equal(@"C:\ProgramData\OrvitaPrint", settings.DataDirectory);
        Assert.Equal(@"C:\ProgramData\OrvitaPrint\logs", settings.LogDirectory);
    }

    [Fact]
    public void Configuracion_carga_perezosa_solo_pide_un_doctor()
    {
        var settings = new SettingsViewModel(_client);
        settings.EnsureLoaded();
        settings.EnsureLoaded();

        Assert.Single(_client.Requests, r => r.Op == ManagementProtocol.Operations.Doctor);

        // El botón Actualizar fuerza una segunda lectura.
        settings.Refresh();
        Assert.Equal(2, _client.Requests.Count(r => r.Op == ManagementProtocol.Operations.Doctor));
    }

    [Fact]
    public void Configuracion_sin_servicio_deja_el_error_visible()
    {
        var settings = new SettingsViewModel(_client);
        _client.ThrowOnNextSend = new ServiceNotRunningException(ManagementProtocol.PipeName);

        settings.EnsureLoaded();

        Assert.NotNull(settings.LoadError);
        Assert.Contains("no está en ejecución", settings.LoadError);
        Assert.Null(settings.ServiceVersion);
    }

    // --------------------------------------------------------- Acerca de (viewmodel nuevo)

    [Fact]
    public void Acerca_de_muestra_la_version_del_servicio_y_la_suya()
    {
        var about = new AboutViewModel();
        Assert.False(string.IsNullOrWhiteSpace(about.GuiVersion));
        Assert.Equal("—", about.ServiceVersion);

        about.ApplyStatus(new StatusSnapshot { Version = "2.3.1" });
        Assert.Equal("2.3.1", about.ServiceVersion);

        about.ApplyStatus(null); // servicio caído: queda lo último, sin caerse
        Assert.Equal("2.3.1", about.ServiceVersion);
    }

    // ------------------------------------------- preferencias locales de la GUI (gui.json)

    [Fact]
    public void La_preferencia_de_notificaciones_persiste_en_el_json_de_la_gui()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orp-gui-{Guid.NewGuid():N}", "gui.json");
        try
        {
            var store = new GuiJsonPreferenceStore(path);
            Assert.False(store.GetBool("NotifySuccess", fallback: false));

            store.SetBool("NotifySuccess", true);
            Assert.True(store.GetBool("NotifySuccess", fallback: false));

            // Otra INSTANCIA (otro arranque de la GUI) lo lee del mismo fichero.
            Assert.True(new GuiJsonPreferenceStore(path).GetBool("NotifySuccess", fallback: false));

            // El valor por defecto solo aplica cuando la clave no está.
            Assert.True(store.GetBool("OtraClave", fallback: true));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void El_json_corrupto_no_rompe_y_la_escritura_lo_repara()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orp-gui-{Guid.NewGuid():N}", "gui.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ esto no es json");

            var store = new GuiJsonPreferenceStore(path);
            Assert.False(store.GetBool("NotifySuccess", fallback: false)); // corrupto → defecto, sin caída

            store.SetBool("NotifySuccess", true); // reparación: reescribe el fichero
            Assert.True(new GuiJsonPreferenceStore(path).GetBool("NotifySuccess", fallback: false));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { }
        }
    }

    // --------------------------------- MainViewModel: la casilla pinta en el almacén local

    [Fact]
    public void La_casilla_de_notificaciones_usa_el_almacen_local()
    {
        var registro = new InMemoryPreferenceStore();
        var local = new InMemoryPreferenceStore();

        var vm = new MainViewModel(_client, registro, local) { NotifySuccess = true };

        Assert.True(local.GetBool("NotifySuccess", fallback: false));
        Assert.False(registro.GetBool("NotifySuccess", fallback: false)); // el registro queda para AutoStart
        Assert.True(vm.Settings is not null && vm.About is not null);
    }
}
