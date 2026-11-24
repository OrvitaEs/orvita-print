using Orvita.Print.Core;
using Orvita.Print.Gui.Management;
using Orvita.Print.Gui.Models;
using Orvita.Print.Gui.Preferences;
using Orvita.Print.Gui.ViewModels;
using Xunit;

namespace Orvita.Print.Gui.Tests;

/// <summary>
/// Pestaña «Diagnóstico» (GUI-06) contra un doble de la pipe + preferencias de la GUI
/// (auto-arranque con la clave Run del usuario) + MainViewModel (bandeja/prefrencias).
/// </summary>
public sealed class DiagnosticsAndPreferencesTests : IDisposable
{
    private readonly string _logDirectory;
    private readonly FakeManagementClient _client = new();
    private readonly InMemoryPreferenceStore _preferences = new();

    public DiagnosticsAndPreferencesTests()
    {
        _logDirectory = Path.Combine(Path.GetTempPath(), $"orp-gui-log-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_logDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ------------------------------------------------------------------ resumen doctor

    [Fact]
    public void Resumen_del_doctor_se_formatea_legible_y_sin_secretos()
    {
        _client.RespondOk(ManagementProtocol.Operations.Doctor, new
        {
            version = "1.0.0-dev",
            net_version = "10.0.0",
            os = "Microsoft Windows NT 10.0.26100",
            paired = true,
            location = new { id = 1L, code = "CENTRO", name = "Cafetería Centro" },
            poller_state = "running",
            pending_local = 2,
            jobs_done = 41,
            jobs_error = 1,
            printers = new[] { new { name = "EPSON", @default = true } },
            data_directory = @"C:\ProgramData\OrvitaPrint",
            log_directory = _logDirectory,
            log_files = 1,
            license = new { status = "valid", days_left = 300 },
        });

        var vm = new DiagnosticsViewModel(_client);
        vm.LoadSummary();

        Assert.Null(vm.Error);
        Assert.Contains("1.0.0-dev", vm.SummaryText);
        Assert.Contains("Cafetería Centro", vm.SummaryText);
        Assert.Contains("running", vm.SummaryText);
        Assert.Contains("Licencia: valid", vm.SummaryText);
        Assert.Contains(_logDirectory, vm.SummaryText);
        // S-05: el resumen no menciona token alguno (el doctor del servicio tampoco lo manda).
        Assert.DoesNotContain("token", vm.SummaryText, StringComparison.OrdinalIgnoreCase);
        // La ruta del log queda lista para el visor.
        Assert.NotNull(vm.LogPath);
    }

    [Fact]
    public void Doctor_sin_licencia_no_rompe_y_lo_dice()
    {
        _client.RespondOk(ManagementProtocol.Operations.Doctor, new
        {
            version = "1.0.0-dev",
            paired = false,
            poller_state = "idle",
        });

        var vm = new DiagnosticsViewModel(_client);
        vm.LoadSummary();

        Assert.Contains("(sin información)", vm.SummaryText);
        Assert.Contains("NO — pendiente", vm.SummaryText);
    }

    [Fact]
    public void Generar_zip_muestra_la_ruta_que_devuelve_la_pipe()
    {
        _client.RespondOk(ManagementProtocol.Operations.Doctor, new
        {
            version = "1.0.0-dev",
            zip_path = @"C:\ProgramData\OrvitaPrint\diagnostics-20261124.zip",
            zip_bytes = 12345L,
        });

        var vm = new DiagnosticsViewModel(_client);
        vm.CreateZip();

        Assert.Null(vm.Error);
        Assert.Equal(@"C:\ProgramData\OrvitaPrint\diagnostics-20261124.zip", vm.ZipPath);
    }

    [Fact]
    public void Fallo_del_zip_queda_visible()
    {
        _client.RespondError(ManagementProtocol.Operations.Doctor, "No se pudo generar el ZIP de diagnóstico: sin espacio.", "diagnostics_failed");

        var vm = new DiagnosticsViewModel(_client);
        vm.CreateZip();

        Assert.Contains("sin espacio", vm.Error);
        Assert.Null(vm.ZipPath);
    }

    // --------------------------------------------------------------------- visor de log

    [Fact]
    public void El_visor_lee_las_ultimas_lineas_del_log_mas_reciente()
    {
        File.WriteAllText(Path.Combine(_logDirectory, "orvita-20261123.log"), "viejo\n");
        File.WriteAllText(Path.Combine(_logDirectory, "orvita-20261124.log"), string.Join('\n', Enumerable.Range(1, 300).Select(i => $"línea {i}")));
        _client.RespondOk(ManagementProtocol.Operations.Doctor, new { log_directory = _logDirectory });

        var vm = new DiagnosticsViewModel(_client) { LogPath = Path.Combine(_logDirectory, "*.log") };
        vm.LoadLog();

        Assert.EndsWith("línea 300", vm.LogText, StringComparison.Ordinal);
        Assert.Contains("línea 101", vm.LogText);  // las 200 últimas
        Assert.DoesNotContain("línea 100\n", vm.LogText + "\n"); // las anteriores no
        Assert.EndsWith("orvita-20261124.log", vm.LogPath);
        // El visor NO muestra nada de la BD ni del token (solo el log, S-05).
        Assert.DoesNotContain("outbox.db", vm.LogText);
    }

    [Fact]
    public void Sin_logs_aun_el_visor_no_rompe()
    {
        var vm = new DiagnosticsViewModel(_client) { LogPath = Path.Combine(_logDirectory, "*.log") };

        vm.LoadLog();

        Assert.Contains("sin registros", vm.LogText);
    }

    [Fact]
    public void ReadTail_respeta_el_fichero_en_uso_por_el_servicio()
    {
        var path = Path.Combine(_logDirectory, "orvita-en-uso.log");
        File.WriteAllText(path, string.Join('\n', Enumerable.Range(1, 50).Select(i => $"[{i}]")));
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            var lines = DiagnosticsViewModel.ReadTail(path, 10);
            Assert.Equal(10, lines.Length);
            Assert.StartsWith("[41]", lines[0]);
            Assert.EndsWith("[50]", lines[^1]);
        }
    }

    // ---------------------------------------------------------------- preferencias GUI

    [Fact]
    public void Notificar_aciertos_se_guarda_en_las_preferencias()
    {
        var vm = new MainViewModel(_client, _preferences) { NotifySuccess = true };

        Assert.True(vm.NotifySuccess);
        Assert.True(_preferences.GetBool("NotifySuccess", fallback: false));

        vm.NotifySuccess = false;
        Assert.False(_preferences.GetBool("NotifySuccess", fallback: true));
    }

    [Fact]
    public void Modo_discreto_por_defecto_sin_nada_guardado()
    {
        var vm = new MainViewModel(_client, _preferences);

        Assert.False(vm.NotifySuccess); // Doc/08 §4.6: sin balones por aciertos por defecto
    }

    [Fact]
    public void Servicio_caido_al_refrescar_deja_estado_visible_y_no_lanza()
    {
        var vm = new MainViewModel(_client, _preferences);
        _client.ThrowOnNextSend = new ServiceNotRunningException(ManagementProtocol.PipeName);

        var balloon = vm.RefreshStatus();

        Assert.True(vm.ServiceDown);
        Assert.Equal(TrayState.ServiceDown, vm.TrayState);
        Assert.Equal("El servicio Orvita Print no está en ejecución", vm.TrayTooltip);
        Assert.Null(balloon);
    }

    [Fact]
    public void El_estado_de_bandeja_sale_del_payload_de_status()
    {
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
        var vm = new MainViewModel(_client, _preferences);

        vm.RefreshStatus();

        Assert.False(vm.ServiceDown);
        Assert.Equal(TrayState.Running, vm.TrayState);
        Assert.Contains("Cafetería Centro", vm.StatusBarText);
    }

    [Fact]
    public void Un_trabajo_con_error_en_status_manda_bandeja_roja_y_balón_tras_el_arranque()
    {
        _client.RespondOk(ManagementProtocol.Operations.Status, new
        {
            paired = true,
            poller_running = true,
            location = new { name = "Centro" },
            last_seen_by_server = DateTimeOffset.UtcNow.ToString("O"),
            last_print_error = "sin papel",
            last_job = new
            {
                id = 99L,
                state = "error",
                duplicate_suspected = false,
                updated_at = DateTimeOffset.UtcNow.ToString("O"),
            },
        });
        var vm = new MainViewModel(_client, _preferences);

        var primero = vm.RefreshStatus();  // arranque: no notifica…
        Assert.Equal(TrayState.Incident, vm.TrayState); // …pero la bandeja SÍ queda roja
        Assert.Null(primero);

        // y un refresco posterior del MISMO error no repite el balón.
        var segundo = vm.RefreshStatus();
        Assert.Null(segundo);
    }

    // ------------------------------------------------- auto-arranque (GUI-07, HKCU Run)

    [Fact]
    public void El_autoarranque_por_defecto_solo_aplica_en_el_primer_arranque()
    {
        var store = new InMemoryPreferenceStore();
        try
        {
            // Primer arranque de la GUI tras instalar: auto-arranque ACTIVADO por defecto
            // (desviación documentada: el MSI per-máquina no puede escribir el HKCU del usuario).
            WindowsAutoStart.ApplyFirstRunDefault(store, exePath: @"C:\Program Files\Orvita Print\gui\Orvita.Print.Gui.exe");
            Assert.True(WindowsAutoStart.IsEnabled());
            Assert.True(store.GetBool(WindowsAutoStart.FirstRunMarker, fallback: false));

            // El usuario lo desactiva con la casilla…
            WindowsAutoStart.SetEnabled(false);
            Assert.False(WindowsAutoStart.IsEnabled());

            // …y un arranque posterior de la GUI NO lo vuelve a activar por su cuenta.
            WindowsAutoStart.ApplyFirstRunDefault(store, exePath: @"C:\Program Files\Orvita Print\gui\Orvita.Print.Gui.exe");
            Assert.False(WindowsAutoStart.IsEnabled());
        }
        finally
        {
            WindowsAutoStart.SetEnabled(false); // deja el registro como estaba
        }
    }

    [Fact]
    public void Autoarranque_real_en_HKCU_se_escribe_y_se_borra()
    {
        // Prueba REAL contra HKCU (inocua: es la clave Run del usuario).
        WindowsAutoStart.SetEnabled(true, @"C:\Program Files\Orvita Print\gui\Orvita.Print.Gui.exe");
        try
        {
            Assert.True(WindowsAutoStart.IsEnabled());
        }
        finally
        {
            WindowsAutoStart.SetEnabled(false);
        }

        Assert.False(WindowsAutoStart.IsEnabled());
    }
}
