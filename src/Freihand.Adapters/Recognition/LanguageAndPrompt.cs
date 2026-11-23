namespace Freihand.Adapters.Recognition;

/// <summary>Profilsprache → Whisper-Code (Kapitel 5.5); die Sprache ist fest, nie automatisch erkannt.</summary>
public static class LanguageMapping
{
    /// <summary>Nach dem Hauptteil der BCP-47-Kennung, ohne Gross- und Kleinschreibung: de, gsw → de; en; fr; it.</summary>
    /// <exception cref="RecognizerException"><see cref="RecognizerErrorCode.UnsupportedLanguage"/> für jede andere Kennung.</exception>
    public static string ToWhisper(string profileLanguage)
    {
        ArgumentNullException.ThrowIfNull(profileLanguage);

        var dash = profileLanguage.IndexOf('-', StringComparison.Ordinal);
        var primary = (dash < 0 ? profileLanguage : profileLanguage[..dash]).ToLowerInvariant();

        // Schweizerdeutsch wird ins Hochdeutsche übertragen (Grundentscheid 1)
        return primary switch
        {
            "de" or "gsw" => "de",
            "en" => "en",
            "fr" => "fr",
            "it" => "it",
            _ => throw new RecognizerException(RecognizerErrorCode.UnsupportedLanguage),
        };
    }
}

/// <summary>Vokabularhinweise als Anfangstext für Whisper (Kapitel 5.5).</summary>
public static class PromptBuilder
{
    /// <summary>Startwert für <c>maxChars</c>.</summary>
    public const int DefaultMaxChars = 400;

    private const string Separator = ", ";

    /// <summary>
    /// Kürzt jeden Hinweis am Rand, lässt leere und doppelte (ohne Gross- und Kleinschreibung) weg und verbindet mit „, “,
    /// solange das Ergebnis höchstens <paramref name="maxChars"/> Zeichen hat; ab dem ersten nicht passenden Hinweis fallen
    /// alle weiteren weg und werden gezählt. Ohne Hinweise kein Prompt.
    /// </summary>
    public static (string? Prompt, int Dropped) Build(IReadOnlyList<string> vocabularyHints, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(vocabularyHints);
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);

        // Leere und doppelte Hinweise zählen nie als weggelassen (Kapitel 10.4)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinct = new List<string>();
        foreach (var hint in vocabularyHints)
        {
            var trimmed = hint?.Trim() ?? string.Empty;
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                distinct.Add(trimmed);
            }
        }

        var length = 0;
        var taken = 0;
        foreach (var hint in distinct)
        {
            var next = length + (taken == 0 ? 0 : Separator.Length) + hint.Length;
            if (next > maxChars)
            {
                break;
            }

            length = next;
            taken++;
        }

        var prompt = taken == 0 ? null : string.Join(Separator, distinct.Take(taken));
        return (prompt, distinct.Count - taken);
    }
}
