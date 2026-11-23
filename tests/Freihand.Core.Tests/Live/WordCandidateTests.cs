using Freihand.Core.Live;

namespace Freihand.Core.Tests.Live;

/// <summary>Vorschläge beim Lernen (P11 S4): gewöhnliche Wörter nie vorgewählt, ohne Prüfung auch nicht.</summary>
public sealed class WordCandidateTests
{
    private static readonly HashSet<string> Dictionary = new(["Mark", "gut", "Gut", "Brot", "Park"], StringComparer.Ordinal);

    private static bool? Known(string word) => Dictionary.Contains(word);

    [Fact]
    public void HeardSpellingsAreCountedAndOnlyUnknownWordsArePreselected()
    {
        var candidates = WordCandidates.From("Claude", ["Gut.", "Claude", "Brot,", "Klod", "gut"], Known);

        // Gezählt wird je genauem Text: „Gut“ und „gut“ sind verschiedene Ersetzungen
        Assert.Equal(["Gut", "Claude", "Brot", "Klod", "gut"], candidates.Select(item => item.Text));
        Assert.Equal([1, 1, 1, 1, 1], candidates.Select(item => item.Count));
        Assert.Equal([CandidateKind.Ordinary, CandidateKind.Same, CandidateKind.Ordinary, CandidateKind.New, CandidateKind.Ordinary],
            candidates.Select(item => item.Kind));
        Assert.Equal(["Klod"], candidates.Where(item => item.Preselected).Select(item => item.Text));
    }

    [Fact]
    public void SeveralWordsArePreselectedOnlyIfOneOfThemIsUnknown()
    {
        var candidates = WordCandidates.From("Bluetenpark", ["Blüt Park", "gut Park"], Known);

        Assert.Equal([CandidateKind.New, CandidateKind.Ordinary], candidates.Select(item => item.Kind));
    }

    [Fact]
    public void WithoutASpellCheckerNothingIsPreselected()
    {
        var candidates = WordCandidates.From("Nivella", ["Niwela"], _ => null);

        Assert.Equal(CandidateKind.Unchecked, Assert.Single(candidates).Kind);
        Assert.False(candidates[0].Preselected);
    }

    [Fact]
    public void CommasSplitRepeatsSpokenWithoutAPauseAndPunctuationOnlyIsDropped()
    {
        var candidates = WordCandidates.From("Nivella", ["Niwela, Niwela, Nivela.", "...", "  "], Known);

        Assert.Equal(["Niwela", "Nivela"], candidates.Select(item => item.Text));
        Assert.Equal([2, 1], candidates.Select(item => item.Count));
    }

    // Handtest vom 18.11.2026: das Wort allein ergab „Niwella“, „Shani“; im Satz entsteht die Fehlschreibung des Diktats
    [Theory]
    [InlineData("Heute habe ich … erwähnt.", "Heute habe ich Niwela erwähnt.", "Niwela")]
    [InlineData("Heute habe ich … erwähnt.", "heute habe ich Blüt Park erwähnt", "Blüt Park")]
    [InlineData("Ich schreibe … in die Notiz.", "Ich schreibe Niwela, in die Notiz.", "Niwela")]
    [InlineData("Gestern ging es um …", "Gestern ging es um Niwela.", "Niwela")]
    [InlineData("Gestern ging es um …", "Gestern ging es um Niwela .", "Niwela")]
    public void TheWordAtTheGapIsTaken(string sentence, string heard, string expected)
    {
        Assert.Equal(expected, WordCandidates.InGap(sentence, heard));
    }

    [Theory]
    [InlineData("Heute habe ich … erwähnt.", "Heute habe ich erwähnt.")]
    [InlineData("Heute habe ich … erwähnt.", "Heute hab ich Niwela erwähnt.")]
    [InlineData("Heute habe ich … erwähnt.", "Heute habe ich Niwela erwähnt. Ich schreibe")]
    [InlineData("Gestern ging es um …", "Niwella")]
    [InlineData("Heute habe ich … erwähnt.", "Heute habe ich Niwela. erwähnt.")]
    [InlineData("Heute habe ich … erwähnt.", "Heute. Habe ich Niwela erwähnt.")]
    [InlineData("Gestern ging es um … beim Mittagessen.", "Gestern ging es um Niwela beim Mittagessen. Ich schreibe Niwela")]
    public void ASentenceThatDoesNotFitGivesNothing(string sentence, string heard)
    {
        Assert.Null(WordCandidates.InGap(sentence, heard));
    }

    [Fact]
    public void EveryLearningSentenceHasOneGapWithFixedWordsAfterIt()
    {
        Assert.All(WordCandidates.Sentences, sentence =>
        {
            var parts = sentence.Split(WordCandidates.Gap);
            Assert.Equal(2, parts.Length);
            Assert.Contains(parts[1], char.IsLetter);
        });
    }

    [Fact]
    public void CleanTrimsMarksAndInnerWhitespace()
    {
        Assert.Equal("Blüt Park", WordCandidates.Clean("  „Blüt   Park.“ "));
    }
}
