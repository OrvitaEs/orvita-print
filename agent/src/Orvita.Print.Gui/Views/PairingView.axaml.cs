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

    /// <summary>
    /// DOBLE confirmación del desemparejado (GUI-09): primero el aviso de qué borra la
    /// operación y luego una segunda pregunta «definitiva». Ambas cancelables; la lógica
    /// del VM (pair.remove) no cambia.
    /// </summary>
    private async Task<bool> ConfirmUnpairAsync(string message)
    {
        var first = await ShowConfirmDialogAsync(
            "Desemparejar",
            message,
            yesLabel: "Desemparejar");
        if (!first)
        {
            return false;
        }

        return await ShowConfirmDialogAsync(
            "Desemparejar · confirmación final",
            "Esta acción es definitiva desde este equipo: el agente deja de imprimir los pedidos de la " +
            "tienda y el administrador deberá revocar el token en el panel de Orvita Print. ¿Seguro que " +
            "quieres continuar?",
            yesLabel: "Sí, desemparejar");
    }

    private async Task<bool> ShowConfirmDialogAsync(string title, string message, string yesLabel)
    {
        var dialog = new Window
        {
            Title = title,
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
        var yes = new Button { Content = yesLabel, Classes = { "danger" } };
        var no = new Button { Content = "Cancelar", Classes = { "secondary" } };
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
