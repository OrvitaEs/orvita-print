using System.Globalization;
using Avalonia.Data.Converters;

namespace Orvita.Print.Gui.Views;

/// <summary>true cuando el valor no es null/empty: habilita «Abrir carpeta» del ZIP.</summary>
public sealed class NotNullConverter : IValueConverter
{
    public static readonly NotNullConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && text.Length > 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
