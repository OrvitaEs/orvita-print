using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orvita.Print.Core;
using Orvita.Print.Gui;
using Orvita.Print.Gui.Views;
using Xunit;

// GUI-12 (defectos de alineación/consistencia reportados por el socio en la pestaña «Salidas»):
// (1) el icono de la cabecera debe ser el MISMO que el del item del menú lateral;
// (2) el tile de la cabecera alineado con el borde IZQUIERDO de la primera tarjeta y el botón
//     «Actualizar» alineado con el borde DERECHO (la causa era el Padding 4,0,4,8 de
//     ListBox.table, que metía las tarjetas 4 px respecto al ancho de la cabecera);
// (3) el botón «Actualizar» centrado VERTICALMENTE con el tile y el hueco cabecera→primer
//     elemento IGUAL en las 6 páginas (margen inferior compartido de PageHeader = 16 px).
//
// Se mide con LAYOUT REAL del framework headless (ventanas reales sin shell): se transforman
// los bounds de cada control a coordenadas de la ventana y se comparan con tolerancia ±1 px
// (subpixel de redondeo de layout).

namespace Orvita.Print.Gui.Tests;

public sealed class Gui12AlignmentTests
{
    private const double Tolerance = 1.0;

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<T> Descendants<T>(Visual root) where T : Visual
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var deep in Descendants<T>(child))
            {
                yield return deep;
            }
        }
    }

    /// <summary>Bounds del control transformados a coordenadas de la ventana (layout real).</summary>
    private static Rect BoundsInWindow(Window window, Visual control)
    {
        var topLeft = control.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException("El control no está en el árbol visual.");
        var bottomRight = control.TranslatePoint(
            new Point(control.Bounds.Width, control.Bounds.Height), window)
            ?? throw new InvalidOperationException("El control no está en el árbol visual.");
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>El PageHeader de la página seleccionada, el tile azul y su acción (si hay).</summary>
    private static (PageHeader Header, Border Tile, Button? FirstAction) HeaderOf(Window window, Control page)
    {
        var header = Descendants<PageHeader>(page).First();
        var tile = Descendants<Border>(header).First(b => b.Width == 44 && b.Height == 44);
        var action = Descendants<Button>(header).Cast<Button?>().FirstOrDefault();
        return (header, tile, action);
    }

    private static MainWindow OpenWindow(FakeManagementClient client)
    {
        var vm = new Orvita.Print.Gui.ViewModels.MainViewModel(
            client,
            new Orvita.Print.Gui.Preferences.InMemoryPreferenceStore());
        var window = new MainWindow(vm, enableTray: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static string Dump(string label, params (string Name, Rect Bounds)[] items)
    {
        var sb = new StringBuilder(label).AppendLine(":");
        foreach (var (name, bounds) in items)
        {
            sb.AppendLine($"  {name}: L={bounds.Left:F1} T={bounds.Top:F1} R={bounds.Right:F1} B={bounds.Bottom:F1}");
        }

        return sb.ToString();
    }

    // ------------------------------------------------- defecto 1: icono = item del menú

    [AvaloniaFact]
    public void Cada_cabecera_usa_el_mismo_icono_que_su_item_del_menu_lateral()
    {
        var window = OpenWindow(new FakeManagementClient());
        var tabs = window.FindControl<TabControl>("Tabs")!;

        foreach (var tab in tabs.Items.OfType<TabItem>())
        {
            tabs.SelectedItem = tab;
            Dispatcher.UIThread.RunJobs();
            var page = Assert.IsAssignableFrom<Control>(tabs.SelectedContent);

            var expected = Assert.IsAssignableFrom<Geometry>(tab.Tag);
            var header = Descendants<PageHeader>(page).First();
            Assert.True(ReferenceEquals(expected, header.Icon),
                $"Pestaña «{tab.Header}»: la cabecera NO usa el icono del menú (esperado {expected.GetType().Name}).");
        }

        window.Close();
    }

    // ------------------------------------------- defecto 2: alineación horizontal (Salidas)

    [AvaloniaFact]
    public void En_salidas_el_tile_coincide_con_la_izquierda_de_las_tarjetas_y_el_boton_con_la_derecha()
    {
        // Una tarjeta real en la lista (destination.list por la pipe doble).
        var client = new FakeManagementClient();
        client.RespondOk(ManagementProtocol.Operations.DestinationList, new
        {
            destinations = new object[]
            {
                new { role = "default", label = "Mostrador", printer_name = "TP-100", copies = 2, enabled = true, kind = "raw" },
            },
            printers = new object[] { new { name = "TP-100", @default = true } },
        });

        var window = OpenWindow(client);
        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.SelectedIndex = 1; // Salidas
        Dispatcher.UIThread.RunJobs();

        var page = Assert.IsAssignableFrom<Control>(tabs.SelectedContent);
        var (header, tile, refresh) = HeaderOf(window, page);

        // ¿Es el botón «Actualizar»? (la cabecera de Salidas solo tiene uno).
        Assert.NotNull(refresh);
        Assert.Contains("Actualizar", Descendants<TextBlock>(refresh!).First().Text);

        var list = Assert.IsAssignableFrom<ListBox>(page.GetVisualDescendants().First(c => c is ListBox));
        var firstCard = Descendants<Border>(list).First(b => b.Classes.Contains("card"));
        var formCard = Descendants<Border>(page).Last(b => b.Classes.Contains("card"));

        var tileBounds = BoundsInWindow(window, tile);
        var refreshBounds = BoundsInWindow(window, refresh);
        var cardBounds = BoundsInWindow(window, firstCard);
        var formBounds = BoundsInWindow(window, formCard);
        var rootGrid = Descendants<Grid>(page).First();
        var rootBounds = BoundsInWindow(window, rootGrid);
        var headerCtl = Descendants<PageHeader>(page).First();
        var headerCtlBounds = BoundsInWindow(window, headerCtl);
        Assert.True(Math.Abs(tileBounds.Left - cardBounds.Left) <= Tolerance,
            Dump($"ClientSize={window.ClientSize} RootGrid L={rootBounds.Left} R={rootBounds.Right} W={rootBounds.Width} | Header L={headerCtlBounds.Left} R={headerCtlBounds.Right} W={headerCtlBounds.Width} | Tile vs 1ª tarjeta (izquierda)",
                ("Tile", tileBounds), ("Botón", refreshBounds), ("Tarjeta", cardBounds), ("Formulario", formBounds)));

        Assert.True(Math.Abs(refreshBounds.Right - cardBounds.Right) <= Tolerance,
            Dump($"ClientSize={window.ClientSize} RootGrid L={rootBounds.Left} R={rootBounds.Right} W={rootBounds.Width} | Header L={headerCtlBounds.Left} R={headerCtlBounds.Right} W={headerCtlBounds.Width} | Botón vs 1ª tarjeta (derecha)",
                ("Tile", tileBounds), ("Botón", refreshBounds), ("Tarjeta", cardBounds), ("Formulario", formBounds)));

        Assert.True(Math.Abs(tileBounds.Left - formBounds.Left) <= Tolerance,
            Dump("Tile vs formulario (izquierda)",
                ("Tile", tileBounds), ("Botón", refreshBounds), ("Formulario", formBounds)));

        Assert.True(Math.Abs(refreshBounds.Right - formBounds.Right) <= Tolerance,
            Dump("Botón vs formulario (derecha)",
                ("Tile", tileBounds), ("Botón", refreshBounds), ("Formulario", formBounds)));

        window.Close();
    }

    // -------------------------------------------------- defecto 3: respiración vertical

    [AvaloniaFact]
    public void El_boton_esta_centrado_verticalmente_con_el_tile_y_el_hueco_cabecera_contenido_es_uniforme()
    {
        var window = OpenWindow(new FakeManagementClient());
        var tabs = window.FindControl<TabControl>("Tabs")!;
        var gaps = new List<(string Page, double Gap)>();

        foreach (var tab in tabs.Items.OfType<TabItem>())
        {
            tabs.SelectedItem = tab;
            Dispatcher.UIThread.RunJobs();
            var page = Assert.IsAssignableFrom<Control>(tabs.SelectedContent);
            var (header, tile, action) = HeaderOf(window, page);

            // Botón «Actualizar» centrado con el tile (defecto 3) — solo en páginas con
            // acción en la cabecera (Cola, Salidas, Diagnóstico).
            if (action is { } button)
            {
                var tileBounds = BoundsInWindow(window, tile);
                var actionBounds = BoundsInWindow(window, button);
                var tileCenter = tileBounds.Center.Y;
                var buttonCenter = actionBounds.Center.Y;
                Assert.True(Math.Abs(tileCenter - buttonCenter) <= Tolerance,
                    Dump($"Pestaña «{tab.Header}»: botón vs tile (vertical)",
                        ("Tile", tileBounds), ("Acción", actionBounds)));
            }

            // Hueco cabecera → primer elemento visible: igual en TODAS las páginas (16 px,
            // el margen inferior compartido de PageHeader). ESTRUCTURAL: se buscan los hijos
            // directos del Grid raíz de la página (no partes de plantillas como scrolls) y se
            // mide desde el borde inferior del Grid INTERNO de la cabecera — los bounds del
            // UserControl ya INCLUYEN su margen (16 px), así que medir contra ellos daría 0.
            var headerGrid = Descendants<Grid>(header).First();
            var headerBottom = BoundsInWindow(window, headerGrid).Bottom;
            var root = header.GetVisualAncestors().OfType<Grid>().First();
            var next = root.GetVisualChildren()
                .OfType<Control>()
                .Where(c => !ReferenceEquals(c, header) && c.IsVisible)
                .Select(c => (Control: c, Bounds: BoundsInWindow(window, c)))
                .Where(x => x.Bounds.Top > headerBottom + 0.5)
                .OrderBy(x => x.Bounds.Top)
                .First();
            var gap = next.Bounds.Top - headerBottom;
            gaps.Add(((string)tab.Header!, gap));
        }

        // Todas las páginas comparten el MISMO hueco (16 px ± 1 de redondeo).
        Assert.All(gaps, entry =>
            Assert.True(Math.Abs(entry.Gap - 16) <= Tolerance,
                $"Pestaña «{entry.Page}»: hueco cabecera→contenido = {entry.Gap:F1} px, esperado 16 ± {Tolerance}. " +
                $"Todos: {string.Join(", ", gaps.Select(g => $"{g.Page}={g.Gap:F1}"))}"));

        window.Close();
    }
}
