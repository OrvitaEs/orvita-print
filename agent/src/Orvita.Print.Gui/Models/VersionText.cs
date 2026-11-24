namespace Orvita.Print.Gui.Models;

/// <summary>
/// Versiones para MOSTRAR (GUI-11, defecto «los textos se salen de las cajas»): las versiones
/// dev de la GUI y del servicio viajan con el hash completo del commit
/// («1.0.0-dev+b644dc9034dddf546118bac291502099ee4c1677») y desbordan las tarjetas. Para
/// pantalla se trunca el hash: «1.0.0-dev+b644dc9» — el ejemplo EXACTO que pidió el socio
/// (7 caracteres de hash; el texto dice 8, pero el ejemplo manda). El valor completo se
/// conserva en la pipe y en los diagnósticos — aquí solo se acorta lo que se pinta.
/// </summary>
public static class VersionText
{
    /// <summary>Número de caracteres del hash que se muestran en pantalla.</summary>
    public const int HashLength = 7;

    /// <summary>
    /// Acorta la parte tras «+» (el hash) a <see cref="HashLength"/> caracteres. Versiones
    /// sin «+» se devuelven tal cual; null/ vacío devuelve «—».
    /// </summary>
    public static string Shorten(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return "—";
        }

        var plus = version.IndexOf('+');
        if (plus < 0)
        {
            return version;
        }

        var hash = version[(plus + 1)..];
        return hash.Length <= HashLength
            ? version
            : string.Concat(version.AsSpan(0, plus + 1), hash.AsSpan(0, HashLength));
    }
}
