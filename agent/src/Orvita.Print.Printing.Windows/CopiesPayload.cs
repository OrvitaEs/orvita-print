namespace Orvita.Print.Printing.Windows;

/// <summary>Utilidades compartidas por los transportes de impresión (públicas para poder probar el multiplicador de copias).</summary>
public static class CopiesPayload
{
    /// <summary>
    /// Repite <paramref name="payload"/> <paramref name="copies"/> veces (mínimo 1): el
    /// multiplicador de copias del trabajo lo aplica el transporte (Doc/03 §6.7), nunca el
    /// renderizador. Un solo búfer por rendimiento: el spooler y el socket lo tratan como un flujo.
    /// </summary>
    public static byte[] Repeat(byte[] payload, int copies)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0)
        {
            throw new ArgumentException("El flujo de impresión está vacío.", nameof(payload));
        }

        var count = Math.Max(1, copies);
        if (count == 1)
        {
            return payload;
        }

        var result = new byte[payload.Length * count];
        for (var i = 0; i < count; i++)
        {
            payload.CopyTo(result, i * payload.Length);
        }

        return result;
    }
}
