namespace Freihand.Core.Formatting;

/// <summary>
/// Liest deutsche Zahlwörter von zwei bis 999 999 als ein Wort, z. B. „siebenundzwanzig“ oder
/// „zweitausendfünfhundert“ (BA-17). „ein“, „eine“ usw. sind Artikel und werden nie gelesen, „null“ ebenfalls nicht.
/// </summary>
internal static class GermanNumberWords
{
    private static readonly Dictionary<string, int> Below20 = new(StringComparer.Ordinal)
    {
        ["eins"] = 1,
        ["zwei"] = 2,
        ["drei"] = 3,
        ["vier"] = 4,
        ["fünf"] = 5,
        ["sechs"] = 6,
        ["sieben"] = 7,
        ["acht"] = 8,
        ["neun"] = 9,
        ["zehn"] = 10,
        ["elf"] = 11,
        ["zwölf"] = 12,
        ["dreizehn"] = 13,
        ["vierzehn"] = 14,
        ["fünfzehn"] = 15,
        ["sechzehn"] = 16,
        ["siebzehn"] = 17,
        ["achtzehn"] = 18,
        ["neunzehn"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.Ordinal)
    {
        ["zwanzig"] = 20,
        ["dreissig"] = 30,
        ["dreißig"] = 30,
        ["vierzig"] = 40,
        ["fünfzig"] = 50,
        ["sechzig"] = 60,
        ["siebzig"] = 70,
        ["achtzig"] = 80,
        ["neunzig"] = 90,
    };

    // Einer als Vorsilbe: „ein“undzwanzig, „ein“hundert
    private static readonly Dictionary<string, int> UnitPrefixes = new(StringComparer.Ordinal)
    {
        ["ein"] = 1,
        ["zwei"] = 2,
        ["drei"] = 3,
        ["vier"] = 4,
        ["fünf"] = 5,
        ["sechs"] = 6,
        ["sieben"] = 7,
        ["acht"] = 8,
        ["neun"] = 9,
    };

    /// <summary>Wert des Zahlworts oder null, wenn das Wort keine ganze Zahl von 1 bis 999 999 ist.</summary>
    public static int? Parse(string word)
    {
        var lower = word.ToLowerInvariant();
        if (lower.Length == 0)
        {
            return null;
        }

        var thousand = lower.IndexOf("tausend", StringComparison.Ordinal);
        if (thousand < 0)
        {
            return BelowThousand(lower);
        }

        var head = lower[..thousand];
        var tail = lower[(thousand + "tausend".Length)..];

        int? multiplier = head.Length == 0 || head == "ein" ? 1 : BelowThousand(head);
        int? rest = tail.Length == 0 ? 0 : BelowThousand(StripAnd(tail));
        return multiplier is null || rest is null ? null : multiplier * 1000 + rest;
    }

    private static int? BelowThousand(string text)
    {
        var hundred = text.IndexOf("hundert", StringComparison.Ordinal);
        if (hundred < 0)
        {
            return BelowHundred(text);
        }

        var head = text[..hundred];
        var tail = text[(hundred + "hundert".Length)..];

        int? multiplier = head.Length == 0 ? 1 : UnitPrefixes.TryGetValue(head, out var unit) ? unit : null;
        int? rest = tail.Length == 0 ? 0 : BelowHundred(StripAnd(tail));
        return multiplier is null || rest is null ? null : multiplier * 100 + rest;
    }

    private static int? BelowHundred(string text)
    {
        if (Below20.TryGetValue(text, out var small))
        {
            return small;
        }

        if (Tens.TryGetValue(text, out var tens))
        {
            return tens;
        }

        // „einundzwanzig“: Einer, „und“, Zehner
        var and = text.IndexOf("und", StringComparison.Ordinal);
        if (and > 0
            && UnitPrefixes.TryGetValue(text[..and], out var unit)
            && Tens.TryGetValue(text[(and + 3)..], out var tensPart))
        {
            return tensPart + unit;
        }

        return null;
    }

    // „tausendundeins“, „hundertundzwei“
    private static string StripAnd(string text) =>
        text.StartsWith("und", StringComparison.Ordinal) && text.Length > 3 ? text[3..] : text;
}
