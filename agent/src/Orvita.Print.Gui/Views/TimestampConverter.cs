using System.Globalization;
using Avalonia.Data.Converters;

namespace Orvita.Print.Gui.Views;

/// <summary>Marca de tiempo completa y local (Doc/08 §4.2) para las columnas de la cola.</summary>
public sealed class TimestampConverter : IValueConverter
{
    public static readonly TimestampConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            null => "—",
            DateTimeOffset { } dt when dt == DateTimeOffset.MinValue => "—",
            DateTimeOffset { } dt => dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            _ => value.ToString(),
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
