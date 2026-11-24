using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orvita.Print.Core;
using Orvita.Print.Gui;
using Orvita.Print.Gui.Views;
using Xunit;

// GUI-13 (2 defectos de layout reportados por el socio):
// (1) filas de la Cola apretadas — la corrección de GUI-12 puso ListBoxItem Padding=0 para
//     alinear bordes y se fueron TODAS las márgenes verticales. Ahora el aire vertical
//     (ListBoxItem Padding 14,7 + MinHeight 40, clase «rows») va SEPARADO del inset
//     horizontal (14 px, el mismo que la cabecera de columnas, para no perder el alineado).
// (2) el contenido dejaba de crecer al maximizar (MaxWidth 1000/720 centrado): ahora las 6
//     páginas son FLUIDAS — sin MaxWidth, ancladas a izquierda/arriba/derecha; la tarjeta
//     principal ocupa TODO el ancho disponible (ventana − sidebar − 24 px por lado) y la
//     tabla reparte el extra entre columnas («Último error» es *).
//
// Prueba de regresión headless con Skia (como GUI-12): se redimensiona la ventana host
// (900 y 1500 de ancho) y se miden bounds REALES en coordenadas de ventana (±2 px).
//
// Hallazgo al separar el aire: el inset horizontal de las filas (Padding=0 de GUI-12) dejaba
// los valores 14 px a la IZQUIERDA de su cabecera de columna (que sí tenía Margin 14). El
// padding «14,7» del item lo corrige de paso y lo deja protegido por la prueba.

namespace Orvita.Print.Gui.Tests;

public sealed class Gui13FluidLayoutTests
{
    private const double Tolerance = 2.0;

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

    private static Rect BoundsInWindow(Window window, Visual control)
    {
        var topLeft = control.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException("El control no está en el árbol visual.");
        var bottomRight = control.TranslatePoint(
            new Point(control.Bounds.Width, control.Bounds.Height), window)
            ?? throw new InvalidOperationException("El control no está en el árbol visual.");
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>Ventana con la cola poblada (2 trabajos reales por la pipe doble).</summary>
    private static MainWindow OpenWindowWithQueue(FakeManagementClient client)
    {
        client.RespondOk(ManagementProtocol.Operations.QueueList, new
        {
            limit = 50,
            total_jobs = 2,
            jobs = new object[]
            {
                new
                {
                    job_id = 1844L,
                    order_id = 778899L,
                    printer_role = "default",
                    copies = 2,
                    state = "done",
                    duplicate_suspected = false,
                    attempts = 1,
                    created_at = "2026-11-24T10:00:00.000Z",
                    updated_at = "2026-11-24T10:00:05.000Z",
                    printed_at = "2026-11-24T10:00:05.000Z",
                },
                new
                {
                    job_id = 1850L,
                    printer_role = "cocina",
                    copies = 1,
                    state = "error",
                    duplicate_suspected = false,
                    attempts = 2,
                    error_code = "orp_print_failed",
                    error_message = "La impresora no responde.",
                    created_at = "2026-11-24T11:00:00.000Z",
                    updated_at = "2026-11-24T11:00:10.000Z",
                },
            },
        });

        var vm = new Orvita.Print.Gui.ViewModels.MainViewModel(
            client,
            new Orvita.Print.Gui.Preferences.InMemoryPreferenceStore());
        var window = new MainWindow(vm, enableTray: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>Página Cola, Grid raíz, tarjeta principal de la tabla y la ListBox.</summary>
    private static (Control Page, Grid RootGrid, Border TableCard, ListBox List, Window Window) QueueParts(MainWindow window)
    {
        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.SelectedIndex = 0; // Cola
        Dispatcher.UIThread.RunJobs();
        var page = Assert.IsAssignableFrom<Control>(tabs.SelectedContent);
        var rootGrid = Descendants<Grid>(page).First();
        var card = Descendants<Border>(page).First(b => b.Classes.Contains("card"));
        var list = Assert.IsAssignableFrom<ListBox>(page.GetVisualDescendants().First(c => c is ListBox));
        return (page, rootGrid, card, list, window);
    }

    // --------------------------------------- defecto 2: la tarjeta crece con la ventana

    [AvaloniaFact]
    public void Al_maximizar_la_tarjeta_de_cola_ocupa_todo_el_ancho_y_las_filas_respiran()
    {
        var window = OpenWindowWithQueue(new FakeManagementClient());
        var measured = new List<(double WindowWidth, double CardWidth, double PageWidth, double CardLeft, List<double> RowHeights)>();

        foreach (var width in new[] { 900d, 1500d })
        {
            // Redimensionado EN CALIENTE del host headless (Skia, como GUI-12).
            window.Width = width;
            Dispatcher.UIThread.RunJobs();

            var (page, _, card, list, _) = QueueParts(window);

            // Ancho disponible REAL: ventana − sidebar − 24 px por lado del shell. La página
            // (UserControl) ya mide exactamente ese ancho (el margen del shell es del tema
            // del TabControl, FUERA del control de página); sidebar = izquierda de página − 24.
            var pageBounds = BoundsInWindow(window, page);
            var sidebar = pageBounds.Left - 24;
            var expected = window.ClientSize.Width - sidebar - 48;

            var cardBounds = BoundsInWindow(window, card);
            var rows = Descendants<ListBoxItem>(list).ToList();
            var heights = rows.Select(i => BoundsInWindow(window, i).Height).ToList();

            Assert.NotEmpty(rows);
            Assert.All(heights, h =>
                Assert.True(h > 32, $"Fila sin aire vertical: altura {h:F1} px (Padding 14,7 + MinHeight 40 de GUI-13)."));

            // (a) la tarjeta sigue la ventana: ancho = ventana − sidebar − 2×24 (±2 px).
            Assert.True(Math.Abs(cardBounds.Width - expected) <= Tolerance,
                $"Ancho={width}: tarjeta {cardBounds.Width:F1} px, esperado ventana−sidebar−48 = {expected:F1} px " +
                $"(ClientSize={window.ClientSize.Width:F1}, sidebar={sidebar:F1}).");

            // y ancla a IZQUIERDA: la tarjeta empieza donde la página (no centrada).
            Assert.True(Math.Abs(cardBounds.Left - pageBounds.Left) <= Tolerance,
                $"Ancho={width}: tarjeta a la izquierda {cardBounds.Left:F1}, página {pageBounds.Left:F1} (¿centrada por MaxWidth?).");

            measured.Add((width, cardBounds.Width, pageBounds.Width, cardBounds.Left, heights));
        }

        // Fluido: al pasar de 900 a 1500, la tarjeta crece CON la ventana (≈ +600 px).
        Assert.True(measured[1].CardWidth - measured[0].CardWidth >= 560,
            $"La tarjeta no crece con la ventana: {measured[0].CardWidth:F1} → {measured[1].CardWidth:F1} px.");

        // El aire vertical NO depende del ancho (7+7 px + MinHeight 40 en ambas tallas).
        Assert.All(measured.SelectMany(m => m.RowHeights), h => Assert.True(h > 32));

        window.Close();
    }

    // ---------------------------------------- defecto 2 (todas las páginas): sin MaxWidth

    [AvaloniaFact]
    public void Las_seis_paginas_son_fluidas_sin_maxwidth()
    {
        var window = OpenWindowWithQueue(new FakeManagementClient());
        var tabs = window.FindControl<TabControl>("Tabs")!;

        foreach (var tab in tabs.Items.OfType<TabItem>())
        {
            tabs.SelectedItem = tab;
            Dispatcher.UIThread.RunJobs();
            var page = Assert.IsAssignableFrom<Control>(tabs.SelectedContent);

            // El Grid raíz de la página: sin MaxWidth (Infinity = fluido) y ocupando el
            // ancho COMPLETO de la página (página = ventana − sidebar − 2×24).
            var rootGrid = Descendants<Grid>(page).First();
            Assert.True(double.IsPositiveInfinity(rootGrid.MaxWidth),
                $"Pestaña «{tab.Header}»: el Grid raíz tiene MaxWidth {rootGrid.MaxWidth} (el contenido deja de crecer al maximizar).");

            var pageBounds = BoundsInWindow(window, page);
            var gridBounds = BoundsInWindow(window, rootGrid);
            Assert.True(Math.Abs(pageBounds.Width - gridBounds.Width) <= Tolerance,
                $"Pestaña «{tab.Header}»: Grid raíz {gridBounds.Width:F1} px vs página {pageBounds.Width:F1} px (¿cap de ancho?).");
        }

        window.Close();
    }

    // ------------------------------- defecto 1: inset horizontal de filas = cabecera

    [AvaloniaFact]
    public void Las_columnas_de_las_filas_quedan_alineadas_con_la_cabecera_de_la_tabla()
    {
        var window = OpenWindowWithQueue(new FakeManagementClient());
        var (_, _, _, list, _) = QueueParts(window);

        // Primera cabecera de columna («Pedido») y primer valor de la primera fila.
        var header = Descendants<TextBlock>(list.GetVisualAncestors().OfType<Border>().First())
            .First(t => t.Classes.Contains("table-header"));
        var firstRow = Descendants<ListBoxItem>(list).First();
        var firstCell = Descendants<TextBlock>(firstRow).First();

        var headerLeft = BoundsInWindow(window, header).Left;
        var cellLeft = BoundsInWindow(window, firstCell).Left;
        Assert.True(Math.Abs(headerLeft - cellLeft) <= 1.0,
            $"Columna «Pedido»: cabecera a la izquierda {headerLeft:F1}, fila {cellLeft:F1} — inset horizontal desigual.");

        window.Close();
    }
}
