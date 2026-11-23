using System.Text;
using Freihand.Core.Recognition;

namespace Freihand.Core.Live;

/// <summary>
/// Vergleich von Wörtern nach Kapitel 5.2: Unicode-Normalform C, Kleinschreibung ohne Kultur, ’ als ', am Rand nur die
/// Satz- und Anführungszeichen der Liste. Vorzeichen, Prozent, Einheiten, Bindestriche und Klammern bleiben.
/// </summary>
internal static class WordComparison
{
    private static readonly char[] EdgeMarks = ['.', ',', ';', ':', '!', '?', '…', '"', '„', '“', '”', '«', '»', '‹', '›'];

    /// <summary>Vergleichsform eines Worts oder einer Einheit.</summary>
    public static string Normalize(string text) =>
        text.Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('’', '\'').Trim(EdgeMarks);

    /// <summary>Besteht der Text nur aus Zeichen der Randliste?</summary>
    public static bool IsMarksOnly(string text) => text.Trim(EdgeMarks).Length == 0;

    /// <summary>Text ohne Randzeichen der Liste, sonst unverändert.</summary>
    public static string TrimMarks(string text) => text.Trim(EdgeMarks);

    /// <summary>
    /// Leerraum auf ein Leerzeichen, Randzeichen der Liste nur am Anfang und Ende des Ganzen weg; sonst unverändert,
    /// auch Gross- und Kleinschreibung („Iphone“ bleibt „Iphone“, „.NET“ innen bleibt).
    /// </summary>
    public static string Plain(string text)
    {
        var joined = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        joined = joined[LeadingMarks(joined).Length..];
        joined = joined[..(joined.Length - TrailingMarks(joined).Length)];
        return string.Join(' ', joined.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Randzeichen der Liste am Anfang.</summary>
    public static string LeadingMarks(string text) => text[..(text.Length - text.TrimStart(EdgeMarks).Length)];

    /// <summary>Randzeichen der Liste am Ende.</summary>
    public static string TrailingMarks(string text) => text[text.TrimEnd(EdgeMarks).Length..];

    /// <summary>
    /// Fasst Wörter zu Einheiten zusammen: Ein Token nur aus Zeichen der Randliste gehört zum Wort davor (Kapitel 5.2).
    /// Ein solches Token ganz am Anfang bildet eine eigene Einheit.
    /// </summary>
    public static List<WordUnit> Units(IReadOnlyList<RecognizedWord> words)
    {
        var units = new List<WordUnit>();
        var current = new List<RecognizedWord>();
        foreach (var word in words)
        {
            if (current.Count > 0 && !IsMarksOnly(word.Text))
            {
                units.Add(new WordUnit(current));
                current = [];
            }

            current.Add(word);
        }

        if (current.Count > 0)
        {
            units.Add(new WordUnit(current));
        }

        return units;
    }

    /// <summary>Vergleichsformen einer gesprochenen Wendung, Token am Leerraum geteilt (Kapitel 5.6).</summary>
    public static string[] PhraseKeys(string phrase)
    {
        var tokens = phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var words = tokens.Select(token => new RecognizedWord(token, TimeSpan.Zero, TimeSpan.Zero, 1)).ToList();
        return [.. Units(words).Select(unit => unit.Key)];
    }
}

/// <summary>Ein Wort samt den getrennt gelieferten Randzeichen danach.</summary>
internal sealed class WordUnit
{
    public WordUnit(IReadOnlyList<RecognizedWord> words)
    {
        Words = words;
        Key = WordComparison.Normalize(string.Concat(words.Select(word => word.Text)));
    }

    public IReadOnlyList<RecognizedWord> Words { get; }

    /// <summary>Vergleichsform der ganzen Einheit.</summary>
    public string Key { get; }

    public RecognizedWord First => Words[0];

    /// <summary>Spätestes Ende der Einheit.</summary>
    public TimeSpan End => Words.Max(word => word.End);
}
