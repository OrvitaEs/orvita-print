namespace Orvita.Print.Core;

/// <summary>
/// Impresora instalada en el sistema (Doc/03 §6.6). <see cref="Name"/> es el nombre EXACTO
/// con el que Windows la lista: es el valor que se guarda en la salida y el que luego se
/// busca al imprimir. <see cref="StatusHint"/> es solo una pista para mostrar al usuario
/// (nunca bloquea la impresión: las térmicas no devuelven estado fiable).
/// </summary>
public sealed class PrinterInfo
{
    /// <summary>Nombre exacto en Windows.</summary>
    public required string Name { get; init; }

    /// <summary>Es la impresora predeterminada del equipo.</summary>
    public bool IsDefault { get; init; }

    /// <summary>Puerto de red (instalada como impresora de red) frente a local/USB.</summary>
    public bool IsNetwork { get; init; }

    /// <summary>Pista de estado legible («Lista», «Sin papel», «Desconectada»…); null si no hay dato.</summary>
    public string? StatusHint { get; init; }
}

/// <summary>
/// Enumeración de impresoras instaladas (Doc/03 §6.6, tarea F2-06). El agente la usa para
/// `status`/`doctor`/`test` y para validar el nombre de una salida. Sin impresoras → lista
/// vacía: el llamador muestra el texto explícito («no hay impresoras instaladas»).
/// </summary>
public interface IPrinterEnumerator
{
    /// <summary>Impresoras instaladas; la predeterminada viene marcada. Nunca null.</summary>
    IReadOnlyList<PrinterInfo> List();

    /// <summary>Nombre exacto de la impresora predeterminada; null si no hay ninguna.</summary>
    string? DefaultPrinterName();

    /// <summary>Busca una impresora por nombre EXACTO (ordinal); null si no existe.</summary>
    PrinterInfo? Find(string name);
}
