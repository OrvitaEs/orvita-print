using Avalonia;
using Avalonia.Media;
using Orvita.Print.Gui.Models;
using Xunit;

namespace Orvita.Print.Gui.Tests;

/// <summary>
/// GUI-11 (defectos visuales reportados por el socio): alineación de los iconos de la barra
/// lateral y versiones cortas en pantalla.
///
/// Alineación (defecto 5): Avalonia dibuja la geometría escalada por Stretch=Uniform
/// ALINEADA ARRIBA-IZQUIERDA dentro del Path (a diferencia de WPF, que la centra). Por eso,
/// la única forma de que los 6 items del menú queden en la misma línea es que TODAS las
/// geometrías del menú compartan caja de tinta: centro (12,12) y proporción cuadrada — con
/// caja cuadrada, Uniform llena exactamente el Path de 22 px y la tinta queda idéntica en
/// cualquier tamaño (cabecera 24 px, tarjetas 20 px).
///
/// Nota: en el entorno headless de pruebas (sin Skia) <c>Geometry.Bounds</c> de los arcos se
/// calcula conservador; lo que SI es estable en ambos entornos es el CENTRO de la caja, que
/// es lo que esta prueba exige. La caja cuadrada de las 4 geometrías redibujadas se verifica
/// además con el render real (socio, pantalla) — ver comentario en Views/Icons.axaml.
/// </summary>
public sealed class VisualAlignmentTests
{
    /// <summary>Los 6 iconos del menú lateral (Tags de MainWindow.axaml).</summary>
    public static readonly string[] SidebarIcons =
    [
        "Icon.Printer", "Icon.List", "Icon.Link", "Icon.Pulse", "Icon.Gear", "Icon.Info",
    ];

    [Fact]
    public void Los_iconos_de_la_barra_lateral_estan_centrados_en_la_misma_caja()
    {
        Assert.NotNull(Avalonia.Application.Current);
        foreach (var key in SidebarIcons)
        {
            var found = Avalonia.Application.Current!.TryGetResource(key, null, out var value);
            Assert.True(found, $"Falta el icono «{key}»");
            var geometry = Assert.IsType<StreamGeometry>(value);
            var center = geometry.Bounds.Center;
            Assert.True(Math.Abs(center.X - 12) <= 0.25, $"{key}: centro X = {center.X}, esperado 12");
            Assert.True(Math.Abs(center.Y - 12) <= 0.25, $"{key}: centro Y = {center.Y}, esperado 12");
        }
    }

    [Fact]
    public void Las_geometrias_redibujadas_tienen_caja_cuadrada()
    {
        // Printer/List/Pulse no usan arcos: sus Bounds son exactos también en headless.
        foreach (var key in new[] { "Icon.Printer", "Icon.List", "Icon.Pulse" })
        {
            var found = Avalonia.Application.Current!.TryGetResource(key, null, out var value);
            Assert.True(found, $"Falta el icono «{key}»");
            var geometry = Assert.IsType<StreamGeometry>(value);
            var b = geometry.Bounds;
            Assert.True(Math.Abs(b.Width - b.Height) <= 0.25,
                $"{key}: caja {b.Width}x{b.Height}, esperada cuadrada");
        }
    }

    // ---------------------------------------------------- versiones cortas (defecto 4)

    [Theory]
    [InlineData(null, "—")]
    [InlineData("", "—")]
    [InlineData("1.0.0-dev", "1.0.0-dev")]
    [InlineData("2.3.1", "2.3.1")]
    [InlineData("1.0.0-dev+b644dc9", "1.0.0-dev+b644dc9")]
    [InlineData("1.0.0-dev+b644dc9034dddf546118bac291502099ee4c1677", "1.0.0-dev+b644dc9")]
    public void La_version_en_pantalla_se_acorta_a_8_caracteres_de_hash(string? version, string expected)
    {
        Assert.Equal(expected, VersionText.Shorten(version));
    }
}
