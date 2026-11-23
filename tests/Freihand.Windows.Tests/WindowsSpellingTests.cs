using Freihand.Windows.Recognition;

namespace Freihand.Windows.Tests;

/// <summary>
/// Rechtschreibprüfung von Windows für „Wort lernen“ (P11 S4). Ohne deutsches Wörterbuch (z. B. auf einem Build-Rechner)
/// liefert sie keine Prüfung; dann ist nichts vorgewählt, das deckt WordCandidateTests ab.
/// </summary>
public sealed class WindowsSpellingTests
{
    [Fact]
    public void AnOrdinaryWordIsKnownAndAMadeUpOneIsNot()
    {
        using var spelling = WindowsSpelling.TryCreate("de-CH", "de-DE", "en-US");
        if (spelling is null)
        {
            return;
        }

        var ordinary = spelling.Language.StartsWith("de", StringComparison.Ordinal) ? "Haus" : "house";
        Assert.True(spelling.IsKnown(ordinary));
        Assert.False(spelling.IsKnown("Xqzvbrtlk"));
        Assert.Null(spelling.IsKnown("  "));
    }

    [Fact]
    public void AnUnsupportedLanguageGivesNoChecker()
    {
        Assert.Null(WindowsSpelling.TryCreate("xx-XX"));
    }
}
