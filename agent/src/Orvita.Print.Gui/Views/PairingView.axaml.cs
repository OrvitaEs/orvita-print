using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Orvita.Print.Gui.ViewModels;

namespace Orvita.Print.Gui.Views;

/// <summary>Pestaña «Emparejamiento» (GUI-05). La lógica vive en <see cref="PairingViewModel"/>.</summary>
public partial class PairingView : UserControl
{
    public PairingView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is PairingViewModel vm)
            {
                vm.ConfirmUnpair = ConfirmUnpairAsync;
            }
        };
    }

    private async Task<bool> ConfirmUnpairAsync(string message)
    {
        var dialog = new Window
        {
            Title = "Desemparejar",
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
        var yes = new Button { Content = "Desemparejar" };
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

    private async void OnPairClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PairingViewModel vm)
        {
            await vm.PairAsync();
        }
    }

    private async void OnUnpairClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PairingViewModel vm)
        {
            await vm.UnpairAsync();
        }
    }
}
