using Freihand.Core.Commands;
using Freihand.Core.Live;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>
/// Regel 4 „Lernen = Ersetzen“ (P11 S4, Codex zu PR #66): Jeder Vorschlag, der gespeichert werden kann, wird beim nächsten
/// gleich erkannten Diktat auch ersetzt. Geprüft über die ganze Kette: dieselbe Äusserung (Wörter der Erkennung) einmal
/// fürs Lernen, einmal durchs Diktat mit dem Formatter.
/// </summary>
public sealed class LearningMatchesReplacementTests
{
    private static Utterance Spoken(params string[] gap) => Utterance(0, ["Heute", "habe", "ich", .. gap, "erwähnt."]);

    private static LearningResult Learn(string spelling, Utterance utterance) =>
        WordCandidates.FromUtterances(spelling, [utterance], BuiltInCommands.Pilot, _ => false);

    private static string Dictate(LearnedWord word, Utterance utterance, CommandScope scope = CommandScope.Everywhere) =>
        Assert.IsType<InsertText>(Assert.Single(new DictationInterpreter(BuiltInCommands.Pilot,
            Interpreting(formatter: Formatter()) with { Replacements = WordReplacements.From([word]) })
            .Interpret(utterance, scope))).Segment.Text;

    [Theory]
    [InlineData("Nivella", "Niwela")]
    [InlineData("Bluetenpark", "Blüt", "Park")]
    [InlineData("Maßmann", "Maßman")]
    [InlineData("Dreissigacker", "dreissig", "Acker")]
    [InlineData("Nivella", "„Niwela“")]
    [InlineData("Nivella", "Niwela,")]
    [InlineData("Zielname", "Visual", ".NET")]
    [InlineData("Zielname", "Visual.NET")]
    public void EveryProposalIsReplacedWhenTheSameUtteranceIsDictated(string spelling, params string[] gap)
    {
        var utterance = Spoken(gap);

        var proposal = Assert.Single(Learn(spelling, utterance).Candidates);

        Assert.Equal(CandidateKind.New, proposal.Kind);
        foreach (var scope in new[] { CommandScope.Everywhere, CommandScope.DictationWindow })
        {
            var text = Dictate(new LearnedWord(spelling, [proposal.Text]), utterance, scope);
            Assert.StartsWith("Heute habe ich ", text, StringComparison.Ordinal);
            Assert.Contains(spelling, text, StringComparison.Ordinal);
            Assert.EndsWith(" erwähnt.", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Blüt", "Komma", "Park")]
    [InlineData("Blüt", "Punkt", "Park")]
    [InlineData("Blüt", "neue", "Zeile", "Park")]
    [InlineData("Blüt", "neuer", "Absatz", "Park")]
    [InlineData("Blüt", "Doppelpunkt", "Park")]
    [InlineData("Blüt,", "Park")]
    [InlineData("Blüt.", "Park")]
    [InlineData("Blüt", "„Park“")]
    [InlineData("Komma")]
    [InlineData("neue", "Zeile")]
    public void AGapThatTheDictationWouldNotReplaceGivesNoProposal(params string[] gap)
    {
        var result = Learn("Bluetenpark", Spoken(gap));

        Assert.Empty(result.Candidates);
        Assert.Equal(1, result.Unmatched + result.Unusable);
    }

    [Fact]
    public void ATooLongHeardFormIsUnusable()
    {
        var result = Learn("Kurz", Spoken([.. Enumerable.Range(0, 30).Select(k => $"Wort{k}")]));

        Assert.Empty(result.Candidates);
        Assert.Equal(1, result.Unusable);
    }

    // Eigene Vorprüfung zu PR #66: „richtig erkannt“ heisst, das Diktat schreibt genau die Schreibweise
    [Fact]
    public void ACaseOnlyDifferenceIsAReplacementNotRecognizedCorrectly()
    {
        var utterance = Spoken("Iphone");

        var proposal = Assert.Single(Learn("iPhone", utterance).Candidates);

        Assert.NotEqual(CandidateKind.Same, proposal.Kind);
        Assert.Equal("Heute habe ich iPhone erwähnt.", Dictate(new LearnedWord("iPhone", [proposal.Text]), utterance));
    }

    [Fact]
    public void WhatTheDictationWouldGlueIsNeverCalledRecognizedCorrectly()
    {
        // Die Fügeregel schreibt „Visual .NET“ als „Visual.NET“: weder richtig erkannt noch ersetzbar
        var result = Learn("Visual .NET", Spoken("Visual", ".NET"));

        Assert.Empty(result.Candidates);
        Assert.Equal(1, result.Unusable);
    }

    [Fact]
    public void EveryCandidateKeepsItsPromise()
    {
        Utterance[] utterances = [Spoken("Niwela"), Spoken("Nivella"), Spoken("nivella"), Spoken("Nivela"), Spoken("Nivella,")];

        foreach (var candidate in WordCandidates.FromUtterances("Nivella", utterances, BuiltInCommands.Pilot, _ => false).Candidates)
        {
            var utterance = utterances.First(item => item.Words[3].Text.TrimEnd(',') == candidate.Text);
            var replacements = candidate.Kind == CandidateKind.Same ? [] : new[] { new LearnedWord("Nivella", [candidate.Text]) };
            var text = Assert.IsType<InsertText>(Assert.Single(new DictationInterpreter(BuiltInCommands.Pilot,
                Interpreting(formatter: Formatter()) with { Replacements = WordReplacements.From(replacements) })
                .Interpret(utterance, CommandScope.Everywhere))).Segment.Text;
            Assert.Contains("Nivella", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AWordAlreadyRecognizedCorrectlyIsShownButNeedsNoReplacement()
    {
        var result = Learn("Nivella", Spoken("Nivella"));

        Assert.Equal(CandidateKind.Same, Assert.Single(result.Candidates).Kind);
        Assert.Equal(0, result.Unusable);
    }

    [Fact]
    public void ASentenceThatDoesNotFitIsCountedSeparately()
    {
        var result = WordCandidates.FromUtterances("Nivella",
            [Utterance(0, "Heute", "hab", "ich", "Niwela", "erwähnt."), Spoken("Niwela")], BuiltInCommands.Pilot, _ => false);

        Assert.Equal(1, result.Unmatched);
        Assert.Equal("Niwela", Assert.Single(result.Candidates).Text);
    }

    [Fact]
    public void EveryLearningSentenceWorksWithTheChain()
    {
        foreach (var sentence in WordCandidates.Sentences)
        {
            var utterance = Utterance(0, sentence.Replace(WordCandidates.Gap, "Niwela", StringComparison.Ordinal).Split(' '));
            Assert.Equal("Niwela", Assert.Single(Learn("Nivella", utterance).Candidates).Text);
        }
    }
}
