using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orvita.Print.Core;
using Orvita.Print.Gui.Models;
using Orvita.Print.Gui.ViewModels;
using Orvita.Print.Gui.Views;
using Xunit;

// GUI-16 (defecto visual reportado por el socio): en la Cola, el botón «Marcar como revisada»
// se cortaba y desbordaba el borde derecho de la tarjeta. Causa: las columnas fijas de la
// tabla (910 px, dos timestamps completos de ~110 px y dos botones con texto de 97+151 px)
// no cabían en la tarjeta al ancho mínimo de ventana (720 px → ~476 px de fila útil), así
// que la columna Acciones se pintaba FUERA de la tarjeta.
//
// Corrección (medida con sondeo headless, Skia):
// (1) Acciones de fila SOLO-ICONO (⟳ reimprimir / ✓ revisar) con ToolTip: con texto no caben
//     (97 + 75 + spacing = 178 px); con icono son 58 px. Los botones con texto quedan en el
//     panel de detalle (ahí sí hay sitio).
// (2) Etiqueta corta en el detalle: «Revisar» (tooltip «Marcar la incidencia como revisada»).
// (3) Tabla que cabe a 720: «Llegada» en dd/MM HH:mm (tooltip con la marca completa), columna
//     «Impresión» retirada de la TABLA (la marca completa vive en el detalle) y badge de
//     duplicados «Reimpreso» (era «Reimpreso tras corte», 118 px de badge).
// El criterio del socio se verifica aquí: a 720 px (MinWidth), el borde derecho del último
// botón de cada fila queda DENTRO de la tarjeta con margen ≥ 8 px, en layout REAL.

namespace Orvita.Print.Gui.Tests;

public sealed class Gui16QueueActionsTests
{
    private const double MinWindowWidth = 720.0;
    private const double MarginInsideCard = 8.0;

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

    /// <summary>Cola con un error (ambos botones: reimprimir + revisar) y un done (solo reimprimir).</summary>
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
                    job_id = 1850L,
                    order_id = 778899L,
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
                new
                {
                    job_id = 1844L,
                    order_id = 778800L,
                    printer_role = "default",
                    copies = 2,
                    state = "done",
                    duplicate_suspected = false,
                    attempts = 1,
                    created_at = "2026-11-24T10:00:00.000Z",
                    updated_at = "2026-11-24T10:00:05.000Z",
                    printed_at = "2026-11-24T10:00:05.000Z",
                },
            },
        });

        var vm = new MainViewModel(client, new Orvita.Print.Gui.Preferences.InMemoryPreferenceStore());
        var window = new MainWindow(vm, enableTray: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static (Control Page, Border TableCard, ListBox List) QueueParts(MainWindow window)
    {
        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.SelectedIndex = 0; // Cola
        Dispatcher.UIThread.RunJobs();
        var page = Assert.IsAssignableFrom<Control>(tabs.SelectedContent);
        var card = Descendants<Border>(page).First(b => b.Classes.Contains("card"));
        var list = Assert.IsAssignableFrom<ListBox>(page.GetVisualDescendants().First(c => c is ListBox));
        return (page, card, list);
    }

    // -------------------------- criterio del socio: botones DENTRO de la tarjeta a 720 px

    [AvaloniaFact]
    public void Al_ancho_minimo_los_botones_de_accion_quedan_dentro_de_la_tarjeta()
    {
        var window = OpenWindowWithQueue(new FakeManagementClient());

        foreach (var width in new[] { MinWindowWidth, 760d, 900d })
        {
            window.Width = width;
            Dispatcher.UIThread.RunJobs();

            var (_, card, list) = QueueParts(window);
            var cardBounds = BoundsInWindow(window, card);

            // La fila de ERROR ofrece los DOS botones (reimprimir + revisar) y la de DONE
            // reimprimir: hay botones visibles que medir en ambas filas.
            var rows = Descendants<ListBoxItem>(list).ToList();
            Assert.Equal(2, rows.Count);

            foreach (var row in rows)
            {
                var buttons = Descendants<Button>(row).Where(b => b.IsVisible).ToList();
                Assert.True(buttons.Count >= 1,
                    $"Ancho={width}: hay una fila sin botones visibles (se esperaban acciones).");

                foreach (var button in buttons)
                {
                    var right = BoundsInWindow(window, button).Right;
                    Assert.True(right <= cardBounds.Right - MarginInsideCard,
                        $"Ancho={width}: un botón de la fila desborda la tarjeta (borde derecho " +
                        $"{right:F1} vs tarjeta {cardBounds.Right:F1}; margen < {MarginInsideCard} px).");
                }
            }

            // La fila de error tiene los DOS botones (ambas acciones visibles).
            Assert.Equal(2, Descendants<Button>(rows[0]).Count(b => b.IsVisible));
        }

        window.Close();
    }

    // --------------------- forma de las acciones: icono+tooltip en la tabla, texto en detalle

    [AvaloniaFact]
    public void Las_acciones_de_fila_son_solo_icono_con_tooltip_y_las_del_detalle_tienen_texto()
    {
        var window = OpenWindowWithQueue(new FakeManagementClient());
        var (page, _, list) = QueueParts(window);

        // TABLA: los botones de fila NO tienen texto (solo el icono) y SIEMPRE llevan
        // tooltip que explique la acción.
        foreach (var button in Descendants<Button>(list))
        {
            var text = Descendants<TextBlock>(button).FirstOrDefault()?.Text;
            Assert.True(text is null,
                $"El botón de la fila tiene texto («{text}»): en la tabla deben ser solo-icono " +
                "(GUI-16), el texto vive en el detalle.");

            var tip = ToolTip.GetTip(button) as string;
            Assert.False(string.IsNullOrWhiteSpace(tip),
                "El botón solo-icono de la fila necesita ToolTip.Tip con el significado de la acción.");
        }

        // Panel de detalle (se abre al seleccionar la fila con error): botones CON TEXTO,
        // y el de revisión usa la etiqueta corta «Revisar» con su tooltip.
        var errorRow = Descendants<ListBoxItem>(list).First();
        list.SelectedItem = errorRow.DataContext as QueueJobRow;
        Dispatcher.UIThread.RunJobs();

        var detailButtons = Descendants<Button>(page)
            .Where(b => b.Parent is StackPanel { HorizontalAlignment: Avalonia.Layout.HorizontalAlignment.Right })
            .ToList();
        Assert.Equal(2, detailButtons.Count);

        var texts = detailButtons
            .Select(b => string.Concat(Descendants<TextBlock>(b).Select(t => t.Text)))
            .ToList();
        Assert.Contains("Reimprimir", texts);
        Assert.Contains("Revisar", texts);

        var reviewButton = detailButtons.First(b =>
            string.Concat(Descendants<TextBlock>(b).Select(t => t.Text)) == "Revisar");
        Assert.Equal("Marcar la incidencia como revisada: el TPV ya la gestionó",
            ToolTip.GetTip(reviewButton) as string);

        window.Close();
    }

    // ------------------------------------------------------ etiqueta de tiempo corta (tabla)

    [Fact]
    public void El_conversor_corto_formatea_ddmm_hhmm_y_mantiene_el_guion_para_vacios()
    {
        // Formato independiente de la zona horaria del equipo: solo la forma.
        var shaped = ShortTimestampConverter.Instance.Convert(
            new DateTimeOffset(2026, 11, 24, 11, 0, 0, TimeSpan.Zero),
            typeof(string), null, System.Globalization.CultureInfo.InvariantCulture) as string;
        Assert.NotNull(shaped);
        Assert.Matches(@"^\d{2}/\d{2} \d{2}:\d{2}$", shaped!);

        Assert.Equal("—", ShortTimestampConverter.Instance.Convert(null, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("—", ShortTimestampConverter.Instance.Convert(DateTimeOffset.MinValue, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));

        // Y el conversor completo sigue existiendo para el tooltip y el detalle.
        var full = TimestampConverter.Instance.Convert(
            new DateTimeOffset(2026, 11, 24, 11, 0, 0, TimeSpan.Zero),
            typeof(string), null, System.Globalization.CultureInfo.InvariantCulture) as string;
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", full!);
    }
}
