using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Orvita.Print.Core;
using Orvita.Print.Gui;
using Orvita.Print.Gui.Models;
using Orvita.Print.Gui.Views;
using Xunit;

// GUI-08: pruebas headless de Avalonia (sin shell real). Dos piezas: el framework xunit de
// Avalonia (AvaloniaTestFramework) y QUIÉN construye la app (AvaloniaTestApplication →
// TestAppBuilder, más abajo).
[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(Orvita.Print.Gui.Tests.TestAppBuilder))]
[assembly: AvaloniaTestFramework]

namespace Orvita.Print.Gui.Tests;

/// <summary>App headless para pruebas de UI de Avalonia (sin ventana real ni shell).
/// GUI-12: UseHeadlessDrawing=false → render con Skia + shaping real de texto; con el dibujo
/// headless puro las métricas de texto son falsas (un título de 7 caracteres medía 340 px) y
/// la cabecera desbordaba solo en pruebas, no en la GUI real.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new Avalonia.Headless.AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Humo de la ventana real (GUI-01 + restyle GUI-09): se construye, tiene las SEIS
/// pestañas (barra lateral), la barra de estado con su punto y contenido en pantalla.</summary>
public sealed class HeadlessWindowTests
{
    [AvaloniaFact]
    public void La_ventana_unica_tiene_las_seis_pestanas_y_barra_de_estado()
    {
        var vm = new Orvita.Print.Gui.ViewModels.MainViewModel(
            new FakeManagementClient(),
            new Orvita.Print.Gui.Preferences.InMemoryPreferenceStore());
        var window = new MainWindow(vm, enableTray: false);
        window.Show();

        // Barra lateral (Doc/08 §4 + GUI-09): Cola, Salidas, Emparejamiento, Diagnóstico,
        // Configuración y Acerca de.
        var tabs = window.FindControl<Avalonia.Controls.TabControl>("Tabs")!;
        var tabNames = tabs.Items
            .OfType<Avalonia.Controls.TabItem>()
            .Select(tab => (string)tab.Header!)
            .ToList();
        Assert.Equal(
            new[] { "Cola", "Salidas", "Emparejamiento", "Diagnóstico", "Configuración", "Acerca de" },
            tabNames);

        // La barra de estado existe (punto + texto) y arranca consultando.
        Assert.NotNull(window.FindControl<Avalonia.Controls.TextBlock>("StatusBar"));
        Assert.NotNull(window.FindControl<Avalonia.Controls.Shapes.Ellipse>("StatusDot"));

        // Hay contenido seleccionado en pantalla (el TabControl re-tematizado lo muestra).
        Assert.NotNull(tabs.SelectedContent);

        // Las SEIS páginas se construyen sin excepción (los XAML nuevos también).
        for (var i = 0; i < tabNames.Count; i++)
        {
            tabs.SelectedIndex = i;
            Assert.NotNull(tabs.SelectedContent);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void La_ventana_abierta_refresca_el_estado_y_la_barra_lo_muestra()
    {
        // Respuesta DETERMINISTA para cada status (el OnOpened de la ventana ya pide uno):
        var client = new FakeManagementClient();
        client.RespondOk(ManagementProtocol.Operations.Status, new
        {
            paired = true,
            poller_running = true,
            location = new { name = "Cafetería Centro" },
            last_seen_by_server = DateTimeOffset.UtcNow.ToString("O"),
            pending_local = 1,
            jobs_done = 3,
            jobs_error = 0,
        });
        var vm = new Orvita.Print.Gui.ViewModels.MainViewModel(
            client,
            new Orvita.Print.Gui.Preferences.InMemoryPreferenceStore());
        var window = new MainWindow(vm, enableTray: false);

        window.Show();
        vm.RefreshStatus();

        Assert.False(vm.ServiceDown);
        Assert.Equal(TrayState.Running, vm.TrayState);
        Assert.True(vm.IsConnected);
        Assert.Contains("Cafetería Centro", vm.StatusBarText);
        Assert.Contains("última respuesta", vm.StatusBarText);
        window.Close();
    }

    [AvaloniaFact]
    public void Al_abrir_configuracion_se_pide_el_doctor_una_vez_por_la_pipe()
    {
        var client = new FakeManagementClient();
        client.RespondOk(ManagementProtocol.Operations.Doctor, new
        {
            version = "1.0.0-dev",
            data_directory = @"C:\ProgramData\OrvitaPrint",
            log_directory = @"C:\ProgramData\OrvitaPrint\logs",
        });
        var vm = new Orvita.Print.Gui.ViewModels.MainViewModel(
            client,
            new Orvita.Print.Gui.Preferences.InMemoryPreferenceStore());
        var window = new MainWindow(vm, enableTray: false);
        window.Show();

        window.FindControl<Avalonia.Controls.TabControl>("Tabs")!.SelectedIndex = 4;
        window.FindControl<Avalonia.Controls.TabControl>("Tabs")!.SelectedIndex = 4; // segunda vez: sin nueva llamada

        Assert.Equal(1, client.Requests.Count(r => r.Op == ManagementProtocol.Operations.Doctor));
        Assert.Equal("1.0.0-dev", vm.Settings.ServiceVersion);
        window.Close();
    }
}
