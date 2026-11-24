using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Orvita.Print.Gui.Views;

/// <summary>
/// Cabecera común de las pestañas (GUI-09): tile azul con icono + título + subtítulo, y
/// zona de acciones a la derecha. Propiedades con estilo para usarla desde XAML:
/// <c>&lt;v:PageHeader Icon="{StaticResource Icon.Printer}" Title="…" Subtitle="…"&gt;…&lt;/v:PageHeader&gt;</c>.
/// </summary>
public partial class PageHeader : UserControl
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<PageHeader, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<PageHeader, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<PageHeader, string>(nameof(Subtitle), string.Empty);

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<PageHeader, object?>(nameof(Actions));

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public PageHeader()
    {
        InitializeComponent();
    }
}
