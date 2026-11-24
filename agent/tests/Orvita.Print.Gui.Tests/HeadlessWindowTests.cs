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

/// <summary>App headless para pruebas de UI de Avalonia (sin ventana real ni shell).</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new Avalonia.Headless.AvaloniaHeadlessPlatformOptions());
}

/// <summary>Humo de la ventana real (GUI-01): se construye, tiene las cuatro pestañas y la barra de estado.</summary>
public sealed class HeadlessWindowTests
{
    private readonly FakeManagementClient _client = new();
    [AvaloniaFact]
    public void La_ventana_unica_tiene_las_cuatro_pestanas_y_barra_de_estado()
    {
        var vm = new Orvita.Print.Gui.ViewModels.MainViewModel(
            new FakeManagementClient(),
            new Orvita.Print.Gui.Preferences.InMemoryPreferenceStore());
        var window = new MainWindow(vm, enableTray: false);
        window.Show();

        // Las pestañas (Doc/08 §4): Cola, Salidas, Emparejamiento, Diagnóstico.
        var tabNames = window.FindControl<Avalonia.Controls.TabControl>("Tabs")!.Items
            .OfType<Avalonia.Controls.TabItem>()
            .Select(tab => (string)tab.Header!)
            .ToList();
        Assert.Equal(new[] { "Cola", "Salidas", "Emparejamiento", "Diagnóstico" }, tabNames);

        // La barra de estado existe y arranca consultando.
        Assert.NotNull(window.FindControl<Avalonia.Controls.TextBlock>("StatusBar"));

        window.Close();
    }

    [AvaloniaFact]
    public void La_ventana_abierta_refresca_el_estado_y_la_barra_lo_muestra()
    {
        // Respuesta DETERMINISTA para cada status (el OnOpened de la ventana ya pide uno):
        _client.RespondOk(ManagementProtocol.Operations.Status, new
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
            _client,
            new Orvita.Print.Gui.Preferences.InMemoryPreferenceStore());
        var window = new MainWindow(vm, enableTray: false);

        window.Show();
        vm.RefreshStatus();

        Assert.False(vm.ServiceDown);
        Assert.Equal(TrayState.Running, vm.TrayState);
        Assert.Contains("Cafetería Centro", vm.StatusBarText);
        window.Close();
    }
}
