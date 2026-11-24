using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Orvita.Print.Gui.Models;
using Orvita.Print.Gui.ViewModels;

namespace Orvita.Print.Gui.Views;

/// <summary>Pestaña «Cola» (GUI-04). La lógica vive en <see cref="QueueViewModel"/>.</summary>
public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
        // Confirmación de «Reimprimir» sobre un trabajo YA IMPRESO (GUI-15): diálogo modal,
        // misma pauta que el borrado de destinos — el VM solo pide el sí/no (testeable).
        DataContextChanged += (_, _) =>
        {
            if (DataContext is QueueViewModel vm)
            {
                vm.ConfirmReprintDone = ConfirmReprintDoneAsync;
            }
        };
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e) =>
        (DataContext as QueueViewModel)?.Refresh();

    private void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is QueueJobRow row && DataContext is QueueViewModel vm)
        {
            _ = vm.RequestRetryAsync(row);
        }
    }

    private void OnReviewClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is QueueJobRow row && DataContext is QueueViewModel vm)
        {
            _ = vm.RequestReviewAsync(row);
        }
    }

    /// <summary>Diálogo nativo «¿seguro? imprimirá otra copia» para reimprimir un trabajo done.</summary>
    private async Task<bool> ConfirmReprintDoneAsync(string message)
    {
        var dialog = new Window
        {
            Title = "Reimprimir trabajo",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 400 },
                },
            },
        };

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        var result = false;
        var yes = new Button { Content = "Sí, reimprimir", Classes = { "secondary" } };
        var no = new Button { Content = "Cancelar" };
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        ((StackPanel)dialog.Content!).Children.Add(buttons);

        var owner = this.GetVisualRoot() as Window;
        if (owner is null)
        {
            return false;
        }

        await dialog.ShowDialog(owner);
        return result;
    }
}
