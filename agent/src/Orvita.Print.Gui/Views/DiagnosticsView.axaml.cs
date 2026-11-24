using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Orvita.Print.Gui.ViewModels;

namespace Orvita.Print.Gui.Views;

/// <summary>Pestaña «Diagnóstico» (GUI-06). La lógica vive en <see cref="DiagnosticsViewModel"/>.</summary>
public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DiagnosticsViewModel vm)
            {
                vm.OpenFolder = OpenFolder;
            }
        };
    }

    /// <summary>Abre el explorador con el ZIP seleccionado (la ruta la dio la pipe, Doc/03 F2-12).</summary>
    [SupportedOSPlatform("windows")]
    private void OpenFolder(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // Si el explorador no arranca, la ruta queda visible en pantalla: suficiente.
        }
    }

    private void OnLoadClick(object? sender, RoutedEventArgs e) => (DataContext as DiagnosticsViewModel)?.LoadSummary();

    private void OnZipClick(object? sender, RoutedEventArgs e) => (DataContext as DiagnosticsViewModel)?.CreateZip();

    private void OnLogClick(object? sender, RoutedEventArgs e) => (DataContext as DiagnosticsViewModel)?.LoadLog();

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DiagnosticsViewModel { ZipPath: { Length: > 0 } path } vm && vm.OpenFolder is { } open)
        {
            open(path);
        }
    }
}
