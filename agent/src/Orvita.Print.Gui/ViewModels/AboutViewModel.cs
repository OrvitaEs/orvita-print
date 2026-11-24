using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Orvita.Print.Gui.Models;

namespace Orvita.Print.Gui.ViewModels;

/// <summary>
/// Pestaña «Acerca de» (GUI-09, NUEVA): solo lectura — versiones de la GUI y del servicio
/// (la del servicio llega en el payload de <c>status</c>), texto breve del producto y línea
/// de soporte. No hace ninguna llamada nueva por la pipe: reutiliza la instantánea de status
/// que el MainViewModel ya recoge cada 5 s.
/// </summary>
public partial class AboutViewModel : ObservableObject
{
    /// <summary>Versión del ensamblado de la GUI (mayor.menor.parche).</summary>
    public string GuiVersion { get; } =
        typeof(AboutViewModel).Assembly.GetName().Version is { } version
            ? version.ToString(3)
            : "—";

    /// <summary>Texto breve del producto (una frase, sin marketing).</summary>
    public const string ProductText =
        "Orvita Print imprime automáticamente los pedidos de tu tienda WooCommerce en las impresoras " +
        "de tu local: un servicio de Windows con cola propia y esta GUI de bandeja para seguirlo todo.";

    /// <summary>
    /// Línea de soporte. PLACEHOLDER documentado (README de la GUI): el dominio definitivo
    /// se fija con el socio antes de la distribución.
    /// </summary>
    public const string SupportLine = "Soporte: soporte@orvitaprint.es";

    [ObservableProperty]
    private string _serviceVersion = "—";

    /// <summary>Enriquece la versión con la última instantánea de status. Si la pipe no
    /// responde (status null) conserva la última conocida: «Acerca de» no parpadea. En
    /// pantalla la versión va CORTA (hash a 8 caracteres, GUI-11) para no desbordar.</summary>
    public void ApplyStatus(StatusSnapshot? status)
    {
        if (status?.Version is { Length: > 0 } version)
        {
            ServiceVersion = VersionText.Shorten(version);
        }
    }
}
