namespace Orvita.Print.Contracts;

/// <summary>
/// Cursor de trabajos (Doc/04 §5.4): un id autoincremental, nunca una fecha.
/// El agente envía <c>after</c> y el servidor responde el nuevo cursor.
/// </summary>
public readonly record struct Cursor(long Value)
{
    public static implicit operator Cursor(long value) => new(value);
    public static implicit operator long(Cursor cursor) => cursor.Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
