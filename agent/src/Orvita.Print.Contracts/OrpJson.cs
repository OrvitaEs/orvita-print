using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orvita.Print.Contracts;

/// <summary>
/// Opciones de serialización del contrato (Doc/04). El servidor habla snake_case y los
/// literales de los enums son exactamente los del contrato; los bloques se resuelven
/// por el discriminador "type". Única vía sancionada para (de)serializar modelos Orp.
/// </summary>
public static class OrpJson
{
    /// <summary>Opciones completas: incluye el conversor polimórfico de <see cref="Block"/>.</summary>
    public static JsonSerializerOptions Default { get; } = CreateDefault();

    /// <summary>
    /// Opciones internas sin el conversor de <see cref="Block"/>: las usa
    /// <see cref="BlockConverter"/> para serializar cada bloque concreto sin reentrada.
    /// </summary>
    public static JsonSerializerOptions Inner { get; } = CreateInner();

    private static JsonSerializerOptions CreateInner() => new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Escritura fiel de acentos y símbolos («», ñ, €, ·): sin escapar a \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private static JsonSerializerOptions CreateDefault()
    {
        var options = CreateInner();
        options.Converters.Insert(0, new BlockConverter());
        return options;
    }
}
