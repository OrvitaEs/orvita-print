using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Orvita.Print.Gui.Views;

/// <summary>Convierte «#RRGGBB» (hex de los mapeos de Doc/08) a IBrush para los badges.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex && hex.StartsWith('#') && Color.TryParse(hex, out var color)
            ? new SolidColorBrush(color)
            : Brushes.Gray;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
