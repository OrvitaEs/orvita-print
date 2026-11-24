using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Orvita.Print.Gui.Views;

/// <summary>
/// Punto de la barra de estado (GUI-09): verde #2E7D32 cuando hay conexión (status reciente,
/// emparejado y poller corriendo), rojo #C62828 en cualquier otro caso (sin servicio, sin
/// emparejar, sin conexión con la tienda o con incidencia abierta).
/// </summary>
public sealed class ConnectionBrushConverter : IValueConverter
{
    public static readonly ConnectionBrushConverter Instance = new();

    public IBrush ConnectedBrush { get; } = new SolidColorBrush(Color.Parse("#2E7D32"));

    public IBrush DisconnectedBrush { get; } = new SolidColorBrush(Color.Parse("#C62828"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ConnectedBrush : DisconnectedBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
