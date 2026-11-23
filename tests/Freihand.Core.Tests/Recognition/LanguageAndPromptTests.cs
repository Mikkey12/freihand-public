using Freihand.Adapters.Recognition;

namespace Freihand.Core.Tests.Recognition;

/// <summary>Sprachabbildung und Prompt aus Vokabularhinweisen (Kapitel 5.5, R14, R15).</summary>
public sealed class LanguageAndPromptTests
{
    /// <summary>Nach dem Hauptteil der BCP-47-Kennung, ohne Gross- und Kleinschreibung; Schweizerdeutsch → Hochdeutsch.</summary>
    [Theory]
    [InlineData("de-CH", "de")]
    [InlineData("de", "de")]
    [InlineData("DE-de", "de")]
    [InlineData("de-AT", "de")]
    [InlineData("gsw", "de")]
    [InlineData("gsw-CH", "de")]
    [InlineData("en-GB", "en")]
    [InlineData("fr-CH", "fr")]
    [InlineData("it", "it")]
    public void R14_ProfileLanguagesMapToWhisperCodes(string profileLanguage, string whisperLanguage) =>
        Assert.Equal(whisperLanguage, LanguageMapping.ToWhisper(profileLanguage));

    /// <summary>Jede andere Kennung, auch eine leere, ergibt <c>UnsupportedLanguage</c>; nie eine automatische Erkennung.</summary>
    [Theory]
    [InlineData("rm")]
    [InlineData("xx")]
    [InlineData("")]
    public void R14_OtherLanguagesAreUnsupported(string profileLanguage)
    {
        var exception = Assert.Throws<RecognizerException>(() => LanguageMapping.ToWhisper(profileLanguage));
        Assert.Equal(RecognizerErrorCode.UnsupportedLanguage, exception.Code);
    }

    /// <summary>Am Rand gekürzt; Leeres und Doppeltes (ohne Gross- und Kleinschreibung) entfallen und zählen nicht.</summary>
    [Fact]
    public void R15_HintsAreTrimmedAndDuplicatesDropped()
    {
        Assert.Equal(400, PromptBuilder.DefaultMaxChars);

        var (prompt, dropped) = PromptBuilder.Build(["Musterbegriff", " Freihand ", "", "musterbegriff"], 400);

        Assert.Equal("Musterbegriff, Freihand", prompt);
        Assert.Equal(0, dropped);
    }

    /// <summary>
    /// 50 verschiedene Hinweise zu je 20 Zeichen: 18 × 20 + 17 × 2 = 394 Zeichen passen, mit dem 19. wären es
    /// 19 × 20 + 18 × 2 = 416; die übrigen 32 fallen weg und werden gezählt.
    /// </summary>
    [Fact]
    public void R15_HintsThatNoLongerFitAreDroppedAndCounted()
    {
        var hints = Enumerable.Range(1, 50).Select(i => $"Fachwort-Nummer-{i:D4}").ToList();
        Assert.All(hints, hint => Assert.Equal(20, hint.Length));

        var (prompt, dropped) = PromptBuilder.Build(hints, 400);

        Assert.Equal(string.Join(", ", hints.Take(18)), prompt);
        Assert.Equal(394, prompt!.Length);
        Assert.Equal(32, dropped);
    }

    /// <summary>Ohne Hinweise gibt es keinen Prompt.</summary>
    [Fact]
    public void R15_NoHintsGiveNoPrompt()
    {
        var (prompt, dropped) = PromptBuilder.Build([], 400);

        Assert.Null(prompt);
        Assert.Equal(0, dropped);
    }
}
