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

/// <summary>
/// Marca de tiempo CORTA (dd/MM HH:mm) para la columna «Llegada» de la tabla (GUI-16): el
/// formato completo (19 caracteres, ~110 px) no cabe en la tarjeta al ancho mínimo de
/// ventana. La marca completa sigue disponible: en el tooltip de la celda (la vista enlaza
/// TimestampConverter) y en el panel de detalle.
/// </summary>
public sealed class ShortTimestampConverter : IValueConverter
{
    public static readonly ShortTimestampConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            null => "—",
            DateTimeOffset { } dt when dt == DateTimeOffset.MinValue => "—",
            DateTimeOffset { } dt => dt.ToLocalTime().ToString("dd/MM HH:mm"),
            _ => value.ToString(),
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
