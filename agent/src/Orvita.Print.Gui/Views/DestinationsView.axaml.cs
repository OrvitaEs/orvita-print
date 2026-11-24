using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Orvita.Print.Gui.Models;
using Orvita.Print.Gui.ViewModels;

namespace Orvita.Print.Gui.Views;

/// <summary>Pestaña «Salidas» (GUI-05). La lógica vive en <see cref="DestinationsViewModel"/>.</summary>
public partial class DestinationsView : UserControl
{
    public DestinationsView()
    {
        InitializeComponent();
        // Confirmación de borrado: diálogo nativo. El VM solo pide el sí/no (testeable).
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DestinationsViewModel vm)
            {
                vm.ConfirmRemove = ConfirmRemoveAsync;
                vm.Load();
            }
        };
    }

    private async Task<bool> ConfirmRemoveAsync(string message)
    {
        var dialog = new Window
        {
            Title = "Quitar salida",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 380 },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    },
                },
            },
        };

        var buttons = ((StackPanel)((StackPanel)dialog.Content!).Children[1]).Children;
        var result = false;
        var yes = new Button { Content = "Quitar" };
        var no = new Button { Content = "Cancelar" };
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        buttons.Add(yes);
        buttons.Add(no);

        var owner = this.GetVisualRoot() as Window;
        if (owner is null)
        {
            return false;
        }

        await dialog.ShowDialog(owner);
        return result;
    }

    private void OnLoadClick(object? sender, RoutedEventArgs e) => (DataContext as DestinationsViewModel)?.Load();

    private void OnSaveClick(object? sender, RoutedEventArgs e) => (DataContext as DestinationsViewModel)?.Save();

    private void OnResetClick(object? sender, RoutedEventArgs e) => (DataContext as DestinationsViewModel)?.ResetForm();

    private void OnEditClick(object? sender, RoutedEventArgs e) =>
        (DataContext as DestinationsViewModel)?.EditRow((sender as Button)?.DataContext as DestinationRow);

    private async void OnRemoveClick(object? sender, RoutedEventArgs e) =>
        await (DataContext as DestinationsViewModel)?.RemoveAsync((sender as Button)?.DataContext as DestinationRow)!;

    private void OnTestClick(object? sender, RoutedEventArgs e)
    {
        // Probar la salida: el MISMO test que el CLI (op test con --role).
        if ((sender as Button)?.DataContext is not DestinationRow row || DataContext is not DestinationsViewModel vm)
        {
            return;
        }

        vm.Test(row.Role);
    }
}
