using Freihand.Core.Quality;

namespace Freihand.Core.Tests.Quality;

public class TextNormalizerTests
{
    [Fact]
    public void LowercasesFoldsSharpSAndDropsPunctuation()
    {
        Assert.Equal("grüsse herr müller", TextNormalizer.Normalize("Grüße, Herr Müller!"));
    }

    [Fact]
    public void KeepsNumberSeparatorsAndDropsThousandsApostrophe()
    {
        Assert.Equal(["1000", "3,5", "12.30", "12:30"], TextNormalizer.Tokenize("1’000 3,5 12.30 12:30"));
    }

    [Fact]
    public void KeepsSymbolsAndSplitsOtherPunctuation()
    {
        Assert.Equal(
            ["e", "mail", "an", "max@example", "org", "50", "%", "rabatt", "€", "20"],
            TextNormalizer.Tokenize("E-Mail an max@example.org: 50 % Rabatt; € 20."));
    }

    [Fact]
    public void LineBreaksAreWhitespace()
    {
        Assert.Equal(["erste", "zeile", "zweite"], TextNormalizer.Tokenize("Erste Zeile\n\nZweite"));
    }

    [Fact]
    public void ComposedAndDecomposedUmlautsAreEqual()
    {
        Assert.Equal(TextNormalizer.Normalize("Zürich"), TextNormalizer.Normalize("Zürich"));
    }
}

public class WordErrorRateTests
{
    [Fact]
    public void IdenticalTextHasNoErrors()
    {
        var result = WordErrorRate.Compute("Guten Tag, Herr Meier.", "guten tag herr meier");

        Assert.Equal(new WordErrorCounts(4, 0, 0, 0), result);
        Assert.Equal(0, result.Rate);
    }

    [Fact]
    public void CountsSubstitutionAndDeletion()
    {
        // „bellt“ → „belt“ ersetzt, „laut“ fehlt: 2 Fehler auf 4 Wörter
        var result = WordErrorRate.Compute("Der Hund bellt laut.", "Der Hund belt.");

        Assert.Equal(new WordErrorCounts(4, 1, 1, 0), result);
        Assert.Equal(0.5, result.Rate);
    }

    [Fact]
    public void CountsInsertion()
    {
        var result = WordErrorRate.Compute("Guten Tag", "Guten guten Tag");

        Assert.Equal(new WordErrorCounts(2, 0, 0, 1), result);
        Assert.Equal(0.5, result.Rate);
    }

    [Fact]
    public void MissingHypothesisIsAllDeletions()
    {
        Assert.Equal(new WordErrorCounts(3, 0, 3, 0), WordErrorRate.Compute("eins zwei drei", ""));
    }

    [Fact]
    public void EmptyReferenceStaysFinite()
    {
        Assert.Equal(0, WordErrorRate.Compute("", "").Rate);
        Assert.Equal(1, WordErrorRate.Compute("", "hallo welt").Rate);
        Assert.Equal(2, WordErrorRate.Compute("", "hallo welt").Insertions);
    }

    [Fact]
    public void PrefersSubstitutionsOnTies()
    {
        // Zwei Ersetzungen oder Auslassung plus Einfügung kosten gleich viel; gezählt werden Ersetzungen
        Assert.Equal(new WordErrorCounts(2, 2, 0, 0), WordErrorRate.Compute("a b", "b a"));
    }

    [Fact]
    public void ClassicExample()
    {
        // Referenz 6 Wörter, Distanz 3. Statt „sat → sit, the fehlt, down eingefügt“ zählt die
        // Rückverfolgung drei Ersetzungen (sat → sit, the → mat, mat → down); die Rate bleibt 3 / 6
        var result = WordErrorRate.Compute("the cat sat on the mat", "the cat sit on mat down");

        Assert.Equal(new WordErrorCounts(6, 3, 0, 0), result);
        Assert.Equal(0.5, result.Rate);
    }

    [Fact]
    public void AggregatesBySummingNotAveraging()
    {
        var first = WordErrorRate.Compute("eins zwei drei vier", "eins zwei drei fünf");
        var second = WordErrorRate.Compute("hallo", "hallo");

        var total = first + second;

        // 1 Fehler auf 5 Wörter = 0,2, nicht der Mittelwert (0,25 + 0) / 2
        Assert.Equal(new WordErrorCounts(5, 1, 0, 0), total);
        Assert.Equal(0.2, total.Rate);
    }
}

public class TermErrorsTests
{
    [Fact]
    public void CountsMissingNames()
    {
        var result = TermErrors.Evaluate(
            "Grüezi Frau Müller und Herr Meier",
            [new(TermCategory.Name, "Müller"), new(TermCategory.Name, "Meier"), new(TermCategory.Name, "Huber")]);

        Assert.Equal(new CategoryErrors(TermCategory.Name, 3, 1), result.Single(r => r.Category == TermCategory.Name));
        Assert.Equal(new CategoryErrors(TermCategory.Number, 0, 0), result.Single(r => r.Category == TermCategory.Number));
        Assert.Null(result.Single(r => r.Category == TermCategory.Number).Rate);
    }

    [Fact]
    public void NamesMatchOnWordBoundariesWithCase()
    {
        Assert.Equal(0, TermErrors.CountOccurrences("Maximilian schreibt", "Max"));
        Assert.Equal(0, TermErrors.CountOccurrences("frau müller", "Müller"));
        Assert.Equal(1, TermErrors.CountOccurrences("Danke, Max.", "Max"));
        Assert.Equal(1, TermErrors.CountOccurrences("Anna-Lena kommt", "Anna-Lena"));
    }

    [Fact]
    public void NumbersMatchWholeNumbersOnly()
    {
        const string hypothesis = "Es kostet 3,5 Franken, also 1’250 Rappen.";

        Assert.Equal(0, TermErrors.CountOccurrences(hypothesis, "3"));
        Assert.Equal(1, TermErrors.CountOccurrences(hypothesis, "3,5"));
        Assert.Equal(1, TermErrors.CountOccurrences(hypothesis, "1'250"));
        Assert.Equal(0, TermErrors.CountOccurrences(hypothesis, "250"));
        Assert.Equal(1, TermErrors.CountOccurrences("Termin um 12.30.", "12.30"));
    }

    [Fact]
    public void RepeatedTermNeedsEveryOccurrence()
    {
        var result = TermErrors.Evaluate(
            "Meier und Meier",
            [new(TermCategory.Name, "Meier"), new(TermCategory.Name, "Meier"), new(TermCategory.Name, "Meier")]);

        Assert.Equal(1, result.Single(r => r.Category == TermCategory.Name).Errors);
    }

    [Fact]
    public void ExtraOccurrencesAreNoTermErrors()
    {
        var result = TermErrors.Evaluate("42 und 42", [new(TermCategory.Number, "42")]);

        Assert.Equal(0, result.Single(r => r.Category == TermCategory.Number).Errors);
    }
}

public class DictationMarksTests
{
    [Fact]
    public void DuplicateAndMissingMarksAreErrors()
    {
        var result = DictationMarks.Evaluate("Hallo, Welt.", "Hallo,, Welt");

        Assert.Equal(1, Kind(result, DictationMarkKind.Comma).Errors);
        Assert.Equal(1, Kind(result, DictationMarkKind.Period).Errors);
        Assert.Equal(2, result.Expected);
        Assert.Equal(2, result.Errors);
        Assert.Equal(1.0, result.Rate);
    }

    [Fact]
    public void SeparatorsInsideNumbersAreNoMarks()
    {
        var counts = DictationMarks.Count("Um 12.30 Uhr, also 3,5 Stunden: gut?!");

        Assert.Equal(1, counts[DictationMarkKind.Comma]);
        Assert.Equal(1, counts[DictationMarkKind.Colon]);
        Assert.Equal(1, counts[DictationMarkKind.QuestionMark]);
        Assert.Equal(1, counts[DictationMarkKind.ExclamationMark]);
        Assert.False(counts.ContainsKey(DictationMarkKind.Period));
    }

    [Fact]
    public void DistinguishesLineAndParagraphBreaks()
    {
        var counts = DictationMarks.Count("A\nB\r\n\r\nC\n\n\nD");

        Assert.Equal(1, counts[DictationMarkKind.LineBreak]);
        Assert.Equal(2, counts[DictationMarkKind.ParagraphBreak]);
    }

    [Fact]
    public void SumsOverSentences()
    {
        var total = DictationMarkErrors.Sum(
        [
            DictationMarks.Evaluate("Ja.", "Ja."),
            DictationMarks.Evaluate("Nein, danke.", "Nein danke."),
        ]);

        Assert.Equal(3, total.Expected);
        Assert.Equal(1, total.Errors);
        Assert.Equal(2, Kind(total, DictationMarkKind.Period).Found);
    }

    [Fact]
    public void ErrorsOfDifferentSentencesDoNotCancelOut()
    {
        // Codex-Review R6: Satz A ohne Komma, Satz B mit überzähligem Komma; Soll und Ist sind summiert
        // gleich (1 und 1), es bleiben aber zwei Fehler
        var total = DictationMarkErrors.Sum(
        [
            DictationMarks.Evaluate("Ja, gut.", "Ja gut."),
            DictationMarks.Evaluate("Nein danke.", "Nein, danke."),
        ]);

        var comma = Kind(total, DictationMarkKind.Comma);
        Assert.Equal(new DictationMarkCount(DictationMarkKind.Comma, 1, 1, 2), comma);
        Assert.Equal(2, total.Errors);
        Assert.Equal(2.0 / 3, total.Rate);
    }

    [Fact]
    public void RateIsNullWithoutExpectedMarks()
    {
        Assert.Null(DictationMarks.Evaluate("ja", "ja").Rate);
    }

    private static DictationMarkCount Kind(DictationMarkErrors errors, DictationMarkKind kind) =>
        errors.Kinds.Single(k => k.Kind == kind);
}

public class CommandErrorsTests
{
    [Fact]
    public void ClassifiesFalseMissedAndConfusedCommands()
    {
        CommandObservation[] observations =
        [
            new("u1", null, null),
            new("u2", null, "rueckgaengig"),
            new("u3", "rueckgaengig", "rueckgaengig"),
            new("u4", "rueckgaengig", null),
            new("u5", "auswahl-loeschen", "rueckgaengig"),
            new("u6", null, "abbrechen"),
        ];

        var result = CommandErrors.Evaluate(observations, TimeSpan.FromMinutes(30));

        Assert.Equal(new CommandErrors(6, 3, 1, 1, 2, 1, TimeSpan.FromMinutes(30)), result);
        Assert.Equal(3, result.FalseCommands);
        Assert.Equal(2, result.MissedCommands);
        Assert.Equal(4.0, result.FalseTriggeredPerHour);
        Assert.Equal(2.0 / 3, result.MissedRate);
    }

    [Fact]
    public void RatesAreNullWithoutBasis()
    {
        var result = CommandErrors.Evaluate([new("u1", null, null)], null);

        Assert.Null(result.FalseTriggeredPerHour);
        Assert.Null(result.MissedRate);
    }
}
