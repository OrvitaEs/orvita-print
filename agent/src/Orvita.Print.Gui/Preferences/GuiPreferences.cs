using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Orvita.Print.Gui.Preferences;

/// <summary>Preferencias de la PROPIA GUI (no del servicio): dónde se guardan y cómo.</summary>
public interface IPreferenceStore
{
    bool GetBool(string name, bool fallback);

    void SetBool(string name, bool value);
}

/// <summary>Preferencias en memoria (pruebas).</summary>
public sealed class InMemoryPreferenceStore : IPreferenceStore
{
    private readonly Dictionary<string, bool> _values = [];

    public bool GetBool(string name, bool fallback) => _values.TryGetValue(name, out var value) ? value : fallback;

    public void SetBool(string name, bool value) => _values[name] = value;
}

/// <summary>
/// Preferencias de la GUI en el REGISTRO del usuario (HKCU\Software\Orvita\OrvitaPrintGui).
/// ¿Por qué el registro y no un fichero? Regla de hierro de Doc/08 §1: la GUI no escribe
/// ficheros de estado — y esto no es estado del SERVICIO (que vive en su SQLite vía pipe):
/// es la configuración de la ventana de bandeja del usuario (arranque con Windows, notificar
/// aciertos). Si el usuario borra su perfil, las preferencias se van con él: correcto.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryPreferenceStore(string keyPath = @"Software\Orvita\OrvitaPrintGui") : IPreferenceStore
{
    public bool GetBool(string name, bool fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            var raw = key?.GetValue(name);
            return raw is int i ? i != 0 : fallback;
        }
        catch (Exception)
        {
            return fallback; // registro ilegible: preferencia por defecto, nunca una caída
        }
    }

    public void SetBool(string name, bool value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception)
        {
            // Sin permiso para escribir la preferencia: se ignora (best-effort).
        }
    }
}

/// <summary>
/// Auto-arranque de la GUI con la sesión del usuario (GUI-07, Doc/08 §5): clave
/// <c>HKCU\…\Run</c>. <b>Desviación documentada de Doc/08 §5</b>: el MSI es per-máquina y
/// Windows Installer NO puede escribir la clave Run de cada usuario que entre — así que el
/// «activado por defecto» lo aplica la GUI en su PRIMER arranque (marca first_run_done);
/// si el usuario lo desactiva, no se vuelve a activar nunca por su cuenta.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsAutoStart
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "OrvitaPrintGui";
    public const string FirstRunMarker = "FirstRunDone";

    /// <summary>Aplica el auto-arranque por defecto SOLO en el primer arranque de la GUI.</summary>
    public static void ApplyFirstRunDefault(IPreferenceStore store, string? exePath = null)
    {
        if (store.GetBool(FirstRunMarker, fallback: false))
        {
            return; // ya hubo primer arranque: la decisión del usuario es la que vale
        }

        store.SetBool(FirstRunMarker, true);
        SetEnabled(true, exePath);
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            // El comando se guarda entrecomillado («"C:\…\Orvita.Print.Gui.exe"»): sin comillas.
            return key?.GetValue(ValueName) is string command
                && command.Trim().Trim('"').EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Activa/desactiva el arranque con la sesión del usuario. La ruta es la del exe en marcha.</summary>
    public static void SetEnabled(bool enabled, string? exePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                var path = exePath ?? Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    key.SetValue(ValueName, $"\"{path}\"");
                }
            }
            else
            {
                try
                {
                    key.DeleteValue(ValueName);
                }
                catch (System.IO.FileNotFoundException)
                {
                    // ya no estaba: nada que hacer
                }
            }
        }
        catch (Exception)
        {
            // best-effort: sin permiso para el Run del usuario, la casilla simplemente no pega.
        }
    }
}
