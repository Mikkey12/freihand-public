using Freihand.Core.Live;

namespace Freihand.Core.Tests.Live;

/// <summary>Die lesbare Datei <c>woerter.txt</c> (P11 S4): lesen, fehlerhafte Zeilen übergehen, anfügen ohne Verlust.</summary>
public sealed class WordListFileTests
{
    [Fact]
    public void LinesAreReadCommentsAndBlankLinesSkipped()
    {
        var document = WordListFile.Parse(WordListFile.Header + "\r\nNivella: Niwela, Nivela\r\n\r\n# Notiz\nBluetenpark:Blüt Park\n");

        Assert.Empty(document.Problems);
        Assert.Equal(2, document.Words.Count);
        Assert.Equal("Nivella", document.Words[0].Spelling);
        Assert.Equal(["Niwela", "Nivela"], document.Words[0].Heard);
        Assert.Equal(["Blüt Park"], document.Words[1].Heard);
    }

    [Fact]
    public void ABadLineIsSkippedAndNamedWithoutItsText()
    {
        var document = WordListFile.Parse("Niwela ohne Doppelpunkt\nNilo:\n: Mark\nNivella: Niwela\nNivella: Nivela\nX: ...\n");

        // „Nilo:“ ohne gehörte Schreibweise ist gültig und ersetzt nichts (schaltet ein gelerntes Wort ab)
        Assert.Equal(["Nilo", "Nivella"], document.Words.Select(word => word.Spelling));
        Assert.Empty(document.Words[0].Heard);
        Assert.Equal(4, document.Problems.Count);
        Assert.All(document.Problems, problem => Assert.DoesNotContain("Niwela", problem, StringComparison.Ordinal));
        Assert.StartsWith("Zeile 1:", document.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void FormatAndParseRoundTrip()
    {
        LearnedWord[] words = [new("Nivella", ["Niwela", "Nivela"]), new("Nilo", ["Mark"])];

        var document = WordListFile.Parse(WordListFile.Format(words));

        Assert.Empty(document.Problems);
        Assert.Equal(words.Select(word => (word.Spelling, string.Join("|", word.Heard))),
            document.Words.Select(word => (word.Spelling, string.Join("|", word.Heard))));
    }

    [Fact]
    public void AddingKeepsOtherLinesAndCommentsAndMergesTheSameSpelling()
    {
        var existing = "# eigene Notiz\nNilo: Mark\nNivella: Niwela\n";

        var added = WordListFile.Add(existing, new LearnedWord("Nivella", ["Nivela", "Niwela"]));
        var fresh = WordListFile.Add(added, new LearnedWord("Bluetenpark", ["Blütpark"]));

        Assert.Equal("# eigene Notiz\nNilo: Mark\nNivella: Niwela, Nivela\nBluetenpark: Blütpark\n", fresh);
    }

    [Fact]
    public void AddingToAnEmptyFileWritesTheHeader()
    {
        var text = WordListFile.Add(string.Empty, new LearnedWord("Nilo", ["Mark"]));

        Assert.StartsWith(WordListFile.LearnedHeader, text, StringComparison.Ordinal);
        Assert.EndsWith("\nNilo: Mark\n", text, StringComparison.Ordinal);
        Assert.Single(WordListFile.Parse(text).Words);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a:b")]
    [InlineData("a,b")]
    [InlineData("#a")]
    [InlineData("...")]
    [InlineData("a\tb")]
    public void InvalidSpellingsAreRefused(string spelling)
    {
        Assert.NotNull(WordListFile.Invalid(spelling));
        Assert.Throws<ArgumentException>(() => WordListFile.Add(string.Empty, new LearnedWord(spelling is "" ? " " : spelling, ["x"])));
    }

    [Fact]
    public void MoreThanTheLimitIsRefusedLineByLine()
    {
        var text = string.Concat(Enumerable.Range(0, WordListFile.MaxWords + 2).Select(k => $"W{k}: V{k}\n"));

        var document = WordListFile.Parse(text);

        Assert.Equal(WordListFile.MaxWords, document.Words.Count);
        Assert.Equal(2, document.Problems.Count);
    }
}
