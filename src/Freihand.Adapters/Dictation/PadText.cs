using Freihand.Core.Segments;

namespace Freihand.Adapters.Dictation;

/// <summary>
/// Einfügen festen Texts an der Schreibmarke des Diktatfensters. Dieselbe
/// Fügeregel wie in fremden Feldern (<see cref="TextJoin"/>), aber mit dem eigenen Text vor der Schreibmarke: Steht dort
/// schon ein Leerzeichen oder ein Umbruch, kommt kein weiteres dazu. Ohne WPF, damit sie prüfbar ist.
/// </summary>
public static class PadText
{
    /// <param name="text">Bisheriger Text der Schreibfläche, Umbrüche als LF oder CRLF.</param>
    /// <param name="caret">Schreibmarke als Index in <paramref name="text"/>; ausserhalb wird sie an den Rand gesetzt.</param>
    /// <param name="segment">Fester Text eines Abschnitts.</param>
    /// <returns>Was an der Schreibmarke einzufügen ist, samt Trennzeichen; leer bei leerem Abschnitt.</returns>
    public static string Insertion(string text, int caret, string segment)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Length == 0)
        {
            return string.Empty;
        }

        caret = Math.Clamp(caret, 0, text.Length);
        if (caret == 0 || char.IsWhiteSpace(text[caret - 1]))
        {
            return segment;
        }

        // Nur das letzte Zeichen zählt; TextJoin entscheidet, ob ein Leerzeichen dazwischen gehört
        var previous = text[caret - 1].ToString();
        var joined = TextJoin.Join([previous, segment]);
        return joined[previous.Length..];
    }

    /// <summary>Umbrüche der Schreibfläche (CRLF, CR) im Kern als LF (P3).</summary>
    public static string ToCore(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
