using System.Text.Json;

namespace Orvita.Print.Gui.Preferences;

/// <summary>
/// Preferencias locales de la GUI en un fichero JSON PROPIO de la GUI:
/// <c>%LocalAppData%\OrvitaPrint\gui.json</c> (GUI-09, tarjeta «Notificaciones»). Es un
/// fichero de la APLICACIÓN de bandeja, no del servicio: nunca toca la SQLite, ni las rutas,
/// ni el token. Best-effort: si no se puede leer o escribir, la preferencia vuelve al valor
/// por defecto — la GUI nunca cae por una preferencia.
/// </summary>
public sealed class GuiJsonPreferenceStore : IPreferenceStore
{
    private readonly string _path;

    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrvitaPrint",
            "gui.json");

    public GuiJsonPreferenceStore(string? path = null) => _path = path ?? DefaultPath();

    public bool GetBool(string name, bool fallback)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return fallback;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(name, out var value))
            {
                return fallback; // clave ausente: el valor por defecto manda
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            };
        }
        catch (Exception)
        {
            return fallback; // JSON corrupto o fichero ocupado: valor por defecto, nunca una caída
        }
    }

    public void SetBool(string name, bool value)
    {
        try
        {
            var values = ReadValues();
            values[name] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(values));
        }
        catch (Exception)
        {
            // best-effort: si el disco no deja escribir, la preferencia no se persiste.
        }
    }

    private Dictionary<string, bool> ReadValues()
    {
        try
        {
            if (File.Exists(_path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_path));
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var values = new Dictionary<string, bool>();
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.True
                            || property.Value.ValueKind == JsonValueKind.False)
                        {
                            values[property.Name] = property.Value.ValueKind == JsonValueKind.True;
                        }
                    }

                    return values;
                }
            }
        }
        catch (Exception)
        {
            // fichero corrupto: se reescribe desde cero
        }

        return [];
    }
}
