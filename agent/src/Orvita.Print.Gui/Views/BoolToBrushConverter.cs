using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Orvita.Print.Gui.Views;

/// <summary>Verde para mensajes de éxito, rojo para errores (texto del emparejamiento).</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public static readonly BoolToBrushConverter Instance = new();

    public IBrush ErrorBrush { get; } = new SolidColorBrush(Color.Parse("#C62828"));

    public IBrush OkBrush { get; } = new SolidColorBrush(Color.Parse("#2E7D32"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ErrorBrush : OkBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
