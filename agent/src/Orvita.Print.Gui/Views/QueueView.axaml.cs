using Avalonia.Controls;
using Orvita.Print.Gui.ViewModels;

namespace Orvita.Print.Gui.Views;

/// <summary>Pestaña «Cola» (GUI-04). La lógica vive en <see cref="QueueViewModel"/>.</summary>
public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
    }

    private void OnRefreshClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        (DataContext as QueueViewModel)?.Refresh();
}
