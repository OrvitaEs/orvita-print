using Avalonia.Controls;

namespace Orvita.Print.Gui.Views;

/// <summary>Pestaña «Configuración» (GUI-09). La lógica vive en <see cref="MainViewModel"/>
/// (casillas) y en <see cref="Orvita.Print.Gui.ViewModels.SettingsViewModel"/> (doctor).</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }
}
