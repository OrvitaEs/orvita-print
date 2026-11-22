using System.Text;

namespace Orvita.Print.Core;

/// <summary>
/// Tabla de código para el texto ESC/POS (Doc/03 §6.5): <b>CP850 por defecto</b>, CP858 y CP437
/// configurables (Doc/05 §4.3). Los valores son los codepages de Windows, registrados vía
/// <see cref="System.Text.CodePagesEncodingProvider"/>.
/// </summary>
public enum EscPosEncoding
{
    /// <summary>Latín-1 «Multilingual»: la tabla de fábrica de la mayoría de impresoras térmicas. Por defecto.</summary>
    Cp850 = 850,

    /// <summary>Como CP850 pero con el euro (0xD5). Preferible si la impresora la soporta y hay precios en €.</summary>
    Cp858 = 858,

    /// <summary>EE.UU. «PC-8»: imprime ñ/· pero no € ni el resto del latín-1 completo.</summary>
    Cp437 = 437,
}

/// <summary>
/// Políticas de sustitución del renderizador (Doc/05 §4.3): un carácter no representable en la
/// tabla se sustituye por uno seguro y se registra el aviso; <b>nunca</b> se envían bytes que
/// rompan la impresora (ni excepciones de codificación, ni bytes de la tabla extendida equivocada).
/// </summary>
public static class EscPosSubstitutions
{
    /// <summary>
    /// Transliteraciones documentadas: el euro no existe en CP850/CP437 (sí en CP858) y la raya
    /// U+2014 no existe en ninguna de las tres. Cualquier otro carácter cae a '?'.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>
    {
        ["€"] = "E",            // U+20AC — solo existe en CP858 (0xD5)
        ["—"] = "-",            // U+2014 raya
        ["–"] = "-",            // U+2013 semirraya
        ["…"] = "...",          // U+2026
        ["\u00A0"] = " ",       // espacio duro
        ["“"] = "\"",           // comillas tipográficas
        ["”"] = "\"",
        ["‘"] = "'",
        ["’"] = "'",
        ["×"] = "x",            // U+00D7 no está en CP850 (el signo «x» es el uso de cocina)
        ["÷"] = "/",
        ["′"] = "'",
        ["″"] = "\"",
    };

    /// <summary>Sustitución por defecto cuando no hay transliteración.</summary>
    public const string Default = "?";

    public static string Resolve(string original) =>
        Map.TryGetValue(original, out var replacement) ? replacement : Default;
}

/// <summary>
/// <see cref="EncoderFallback"/> que aplica <see cref="EscPosSubstitutions"/> y anota cada
/// sustitución para el aviso de log. Garantiza por construcción que la codificación nunca
/// produce una excepción ni bytes inválidos: lo no representable sale por la política de
/// sustitución, siempre en la tabla destino.
/// </summary>
internal sealed class LoggingSubstitutionFallback : EncoderFallback
{
    /// <summary>Sustituciones aplicadas desde la última creación (original → reemplazo).</summary>
    public List<(string Original, string Replacement)> Applied { get; } = [];

    public override int MaxCharCount => 3; // "…"

    public override EncoderFallbackBuffer CreateFallbackBuffer() => new Buffer(this);

    internal void Record(string original, string replacement) => Applied.Add((original, replacement));

    private sealed class Buffer : EncoderFallbackBuffer
    {
        private readonly LoggingSubstitutionFallback _owner;
        private string _replacement = string.Empty;
        private int _position;

        public Buffer(LoggingSubstitutionFallback owner) => _owner = owner;

        public override int Remaining => _replacement.Length - _position;

        public override bool Fallback(char charHigh, int charIndex) => Fallback(charHigh.ToString());

        /// <summary>Par sustituto (caracteres fuera del BMP): se registra el par completo.</summary>
        public override bool Fallback(char charHigh, char charLow, int charIndex) =>
            Fallback(char.IsSurrogatePair(charHigh, charLow)
                ? new string([charHigh, charLow])
                : charHigh.ToString());

        public override char GetNextChar() =>
            _position < _replacement.Length ? _replacement[_position++] : '\0';

        public override bool MovePrevious()
        {
            if (_position > 0)
            {
                _position--;
                return true;
            }

            return false;
        }

        public override void Reset()
        {
            _replacement = string.Empty;
            _position = 0;
        }

        private bool Fallback(string original)
        {
            _replacement = EscPosSubstitutions.Resolve(original);
            _owner.Record(original, _replacement);
            _position = 0;
            return true;
        }
    }
}
