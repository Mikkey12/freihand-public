using System.Text;

namespace Freihand.Benchmark.Tests;

// Ausführbare Vertragsgegenproben aus der zweiten Codex-Nachprüfung (D04–D08), KEINE P3-Implementierungstests.
// Übernommen mit unveränderten Prüfaussagen; die Doc...-Funktionen bilden die Regeln des P3-Konzepts ab
//, nach der dritten Nachprüfung samt Grundregeln,
// Vergleichsnormalisierung und kurzem Rest vor C (H01, H03). Nicht modelliert: AudioStart/AudioEnd des
// Hypothesenkopfs; die Wortgrenzen im Audiointervall prüft DocValidHypothesis.
// Pr20ThirdReviewTests (E01–E03) ruft DocCut, DocValid und DocAccept per Reflection auf: Namen und Parameter bleiben.
// In P3 werden sie durch echte Aufrufe ersetzt (T59–T66).
// D06 ist ein grüner Gegenbeleg zu einer früher falschen Textbehauptung.
public sealed class Pr20ConceptCounterTests
{
    [Fact]
    public void D04_LengthCutMustNotSplitAnUnadoptedWord()
    {
        Word[] words = [new(9.5, 9.9)];
        var cut = DocCut(words, 10, 0.3);
        Assert.False(words.Any(w => w.Start < cut && cut < w.End), $"Schnitt {cut} liegt im Wort [9.5,9.9).");
    }

    [Fact]
    public void D05_FullWindowMustNotSplitAnUnadoptedWord()
    {
        Word[] words = [new(1.5, 1.9)];
        var cut = DocCut(words, 2, 0.3);
        Assert.False(words.Any(w => w.Start < cut && cut < w.End), $"Schnitt {cut} liegt im Wort [1.5,1.9).");
    }

    [Fact]
    public void D06_T30DisprovesTheClaimedMinimumProgress()
    {
        var words = Enumerable.Range(0, 10).Select(k => new Word(k, k + 1)).ToArray();
        var cut = DocCut(words, 10, 0.3);
        Assert.Equal(9.0, cut);
        Assert.True(cut < 9.7); // Gegenbeleg zur behaupteten Untergrenze, nicht neues Soll für P3.
    }

    [Fact]
    public void T63_FirstWordIsAdoptedWhenNothingElseAdvances()
    {
        // Schnittwahl Fall b: Beginnt das erste Wort am bisherigen Kürzungspunkt, wird es trotz CutGuard übernommen
        Word[] words = [new(0, 9.8)];
        Assert.Equal(9.8, DocCut(words, 10, 0.3));
    }

    [Fact]
    public void EmptyHypothesisCutsAtGuardAsReportedLoss()
    {
        // Schnittwahl Fall c (T53): leer, Schnitt bei G minus CutGuard
        Assert.Equal(9.7, DocCut([], 10, 0.3), 9);
    }

    [Theory]
    [InlineData(9.8)]
    [InlineData(9.7)]
    [InlineData(9.95)]
    public void T64_ShortEmptyRemainderCutsAtTheLimit(double cutPoint)
    {
        // Fall c beim letzten Live-Auftrag vor C: höchstens CutGuard übrig, also Schnitt bei G statt zurück
        Assert.Equal(10, DocCut([], 10, 0.3, cutPoint));
    }

    [Fact]
    public void T64_LongerEmptyRemainderKeepsTheGuard()
    {
        Assert.Equal(9.7, DocCut([], 10, 0.3, 9.6), 9);
    }

    [Fact]
    public void D07_PauseAndEmptyPreviewDoNotProveEngineCompletion()
    {
        const bool pauseConfirmed = true;
        const bool provisionalEmpty = true;
        const bool pendingFinalResult = true; // Quelle liefert den Rest des Mehrwortbefehls erst später.
        // Szenario T61: Sprache bis 2 s, bestätigt ist die Quelle erst bis 1 s
        var closeByDocument = DocCanCloseEngine(pauseConfirmed, provisionalEmpty, completeUntil: 1.0, boundary: 2.0);
        Assert.False(closeByDocument && pendingFinalResult);
    }

    [Fact]
    public void D07_ConfirmedCompletionClosesTheUtterance()
    {
        Assert.True(DocCanCloseEngine(pause: true, provisionalEmpty: true, completeUntil: 2.0, boundary: 2.0));
        Assert.False(DocCanCloseEngine(pause: true, provisionalEmpty: true, completeUntil: null, boundary: 2.0));
    }

    [Fact]
    public void FinishedSourceClosesWithoutCompletion()
    {
        Assert.True(DocCanCloseEngine(pause: true, provisionalEmpty: true, completeUntil: null, boundary: 2.0, sourceFinished: true));
    }

    [Fact]
    public void T66_CompletionNeverDecreases()
    {
        // 1 s, ohne Wert, 0,5 s: weiter 1 s, nicht abschliessen; erst 2 s schliesst; 1,5 s danach nimmt nichts zurück
        Assert.Equal(1.0, DocEffectiveCompleteUntil([1.0, null, 0.5]));
        Assert.False(DocCanCloseEngine(pause: true, provisionalEmpty: true, DocEffectiveCompleteUntil([1.0, null, 0.5]), boundary: 2.0));
        Assert.True(DocCanCloseEngine(pause: true, provisionalEmpty: true, DocEffectiveCompleteUntil([1.0, null, 0.5, 2.0]), boundary: 2.0));
        Assert.Equal(2.0, DocEffectiveCompleteUntil([1.0, null, 0.5, 2.0, 1.5]));
        Assert.Null(DocEffectiveCompleteUntil([null, null]));
    }

    [Fact]
    public void T65_SegmentIsSplitEvenlyIntoWords()
    {
        Assert.Equal(new[] { new Word(0, 2, "Alpha"), new Word(2, 4, "abbrechen") }, DocSplit("Alpha abbrechen", 0, 4));
        var three = DocSplit(" a  b c ", 1, 4);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, three.Select(w => w.Start));
        Assert.Equal(4.0, three[^1].End);
        Assert.Empty(DocSplit("   ", 0, 1));
    }

    [Fact]
    public void T65_WordsOfASegmentOverABoundaryGoToBothUtterances()
    {
        var words = DocSplit("Alpha abbrechen", 0, 4);
        Assert.Equal(new[] { "Alpha" }, DocBefore(words, boundary: 2).Select(w => w.Text));
        Assert.Equal(new[] { "abbrechen" }, words.Except(DocBefore(words, boundary: 2)).Select(w => w.Text));
    }

    [Fact]
    public void D08_T08RepeatMustReachTheFilterWithoutAContractError()
    {
        // Zwei gleiche Fünfwortfolgen; Gruppenintervalle [0,5) und [4.5,9.5) überlappen.
        var first = Enumerable.Range(0, 5).Select(k => new Word(k, k + 1, $"w{k}"));
        var repeated = Enumerable.Range(0, 5).Select(k => new Word(4.5 + k, 5.5 + k, $"w{k}"));
        var words = first.Concat(repeated).ToArray();
        Assert.True(DocValid(words), "T08 verlangt Filterung; Vertrag 2 weist diese Hypothese vorher ab.");
        Assert.Equal(first, DocAccept(words));
    }

    [Fact]
    public void RealRepeatInNewAudioStays()
    {
        // T62 Gegenfall und T25: dieselben Wörter in neuem, nicht überlappendem Audio sind echt gesprochen
        var first = Enumerable.Range(0, 5).Select(k => new Word(k, k + 1, $"w{k}"));
        var repeated = Enumerable.Range(0, 5).Select(k => new Word(5 + k, 6 + k, $"w{k}"));
        var words = first.Concat(repeated).ToArray();
        Assert.Equal(words, DocAccept(words));
    }

    [Fact]
    public void OtherOverlapStaysInvalid()
    {
        // T58: keine Wiederholung, also nach Schritt 3 ungültig
        Assert.False(DocValid([new(0, 2, "A"), new(1, 1.5, "B")]));
    }

    [Fact]
    public void DifferentWordsAreNoRepeatEvenAfterNormalization()
    {
        // Gegenfall zu E02: ein anderes Wort in der zweiten Folge, also keine Wiederholung und nach Schritt 3 ungültig
        var first = Enumerable.Range(0, 5).Select(k => new Word(k, k + 1, $"wort{k}"));
        var other = Enumerable.Range(0, 5).Select(k => new Word(4.5 + k, 5.5 + k, k == 4 ? "anders" : $"„Wort{k}“"));
        var words = first.Concat(other).ToArray();
        Assert.Equal(words, DocAccept(words));
        Assert.False(DocValid(words));
    }

    [Theory]
    [InlineData("’s", "'s")]
    [InlineData("Ende.", "ende")]
    [InlineData("«Gut»", "gut")]
    [InlineData("-5", "-5")]
    [InlineData("5%", "5%")]
    [InlineData("(ja)", "(ja)")]
    public void ComparisonFollowsChapter52(string text, string expected)
    {
        Assert.Equal(expected, DocNormalize(text));
    }

    [Fact]
    public void BasicRulesRejectEdgeWhitespaceAndFallingStart()
    {
        Assert.False(DocValid([new(0, 1, " Wort")]));
        Assert.False(DocValid([new(1, 2, "a"), new(0.5, 0.8, "b")]));
        Assert.True(DocValid([new(0, 1, "a"), new(1, 2, "b")]));
    }

    [Fact]
    public void WordsMustLieInTheRequestedAudio()
    {
        Word[] words = [new(0, 1, "a")];
        Assert.True(DocValidHypothesis(words, audioStart: 0, audioEnd: 1));
        Assert.False(DocValidHypothesis(words, audioStart: 0.5, audioEnd: 2));
        Assert.False(DocValidHypothesis(words, audioStart: 0, audioEnd: 0.9));
    }

    [Fact]
    public void RepeatSearchStartsAgainAfterEachRemoval()
    {
        // Dreimal dieselbe Folge ab 0, 4,5 und 9 s: die zweite überlappt die erste und entfällt. Danach folgt die dritte
        // unmittelbar auf die erste, überlappt sie aber nicht und bleibt als echt gesprochen (Kapitel 5.5).
        var words = Enumerable.Range(0, 3)
            .SelectMany(r => Enumerable.Range(0, 5).Select(k => new Word(4.5 * r + k, 4.5 * r + k + 1, $"w{k}")))
            .ToArray();
        Assert.Equal(words.Take(5).Concat(words.Skip(10)), DocAccept(words));
        Assert.True(DocValid(words));
    }

    [Fact]
    public void SeparatePunctuationStaysWithoutRepeat()
    {
        // Gegenfall zu J01: ohne Wiederholung bleibt jedes Satzzeichen, auch eines ohne Wort davor
        Word[] words = [new(0, 0.1, "."), new(0.1, 1, "a"), new(1, 1.1, "."), new(1.1, 2, "b")];
        Assert.Equal(words, DocAccept(words));
        Assert.True(DocValid(words));
    }

    [Fact]
    public void SixWordRepeatIsRemovedWhole()
    {
        // Sechs Wörter mit überlappender Wiederholung: entfällt als Ganzes
        var first = Enumerable.Range(0, 6).Select(k => new Word(k, k + 1, $"w{k}"));
        var repeated = Enumerable.Range(0, 6).Select(k => new Word(5.5 + k, 6.5 + k, $"w{k}"));
        var words = first.Concat(repeated).ToArray();
        Assert.Equal(first, DocAccept(words));
    }

    private sealed record Word(double Start, double End, string Text = "");

    // Kapitel 5.1, Schnittwahl an der Grenze G für einen Auftrag ab K (cutPoint), auf den geprüften Wörtern:
    // a) letztes Wort, das mindestens guard vor G endet; b) sonst Beginn des ersten Worts, wenn er hinter K liegt,
    // andernfalls dessen Ende; c) leer: G minus guard, liegt das nicht hinter K, G.
    private static double DocCut(IReadOnlyList<Word> words, double end, double guard, double cutPoint = 0)
    {
        if (words.Where(w => w.End <= end - guard).Select(w => (double?)w.End).LastOrDefault() is { } adopted)
        {
            return adopted;
        }

        if (words.Count > 0)
        {
            return words[0].Start > cutPoint ? words[0].Start : words[0].End;
        }

        return end - guard > cutPoint ? end - guard : end;
    }

    // Kapitel 3: abgeschlossen erst, wenn die Grenze feststeht (Pause, Taste oder Streamende) und die Quelle bis
    // dorthin bestätigt oder ganz beendet ist; Pause und leere Vorschau genügen nicht.
    private static bool DocCanCloseEngine(
        bool pause, bool provisionalEmpty, double? completeUntil, double boundary, bool sourceFinished = false) =>
        pause && (sourceFinished || completeUntil >= boundary);

    // Kapitel 3: grösster bisher gemeldeter Wert; ein Update ohne oder mit kleinerem Wert nimmt nichts zurück.
    private static double? DocEffectiveCompleteUntil(IEnumerable<double?> values) => values.Max();

    // Kapitel 3: fester Abschnitt am Leerraum geteilt, Wörter gleichmässig über [start, end), in ganzen Ticks
    // abgerundet; das letzte Wort endet genau bei end.
    private static Word[] DocSplit(string text, double start, double end)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var from = Ticks(start);
        var span = Ticks(end) - from;
        double At(long i) => (from + i * span / parts.Length) / (double)TimeSpan.TicksPerSecond;
        return parts.Select((part, i) => new Word(At(i), At(i + 1), part)).ToArray();
    }

    private static long Ticks(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);

    // Kapitel 5.1: ein Wort gehört zur Äusserung, in deren Grenzen es beginnt.
    private static IEnumerable<Word> DocBefore(IEnumerable<Word> words, double boundary) =>
        words.Where(w => w.Start < boundary);

    // Vertrag 2 in der Reihenfolge des Konzepts: Grundregeln, zeitliche Wiederholungen entfernen, keine Überlappung.
    private static bool DocValid(IReadOnlyList<Word> words) =>
        DocValidHypothesis(words, double.NegativeInfinity, double.PositiveInfinity);

    private static bool DocValidHypothesis(IReadOnlyList<Word> words, double audioStart, double audioEnd) =>
        DocBasic(words, audioStart, audioEnd) && NoOverlap(DocAccept(words));

    // Vertrag 2, Schritt 1: Text nicht leer und ohne Leerraum am Rand, Start < End, im Audio, Start nicht fallend.
    private static bool DocBasic(IReadOnlyList<Word> words, double audioStart, double audioEnd) =>
        words.All(w => w.Text.Length > 0 && w.Text == w.Text.Trim() && w.Start < w.End && audioStart <= w.Start && w.End <= audioEnd)
        && words.Zip(words.Skip(1), (left, right) => right.Start >= left.Start).All(valid => valid);

    // Kapitel 5.5: längste Folge zuerst, bei gleicher Länge von vorn; nach jedem Entfernen beginnt die Suche neu.
    // Verglichen und entfernt wird je Wort samt seinen getrennt gelieferten Randzeichen (Kapitel 5.2).
    private static IReadOnlyList<Word> DocAccept(IReadOnlyList<Word> words)
    {
        var units = DocUnits(words);
        while (RemoveFirstRepeat(units))
        {
            // jede Entfernung verkürzt die Liste, die Suche endet also
        }

        return units.SelectMany(unit => unit).ToList();
    }

    // Kapitel 5.2: ein Token nur aus Randzeichen gehört zum Wort davor.
    private static List<List<Word>> DocUnits(IReadOnlyList<Word> words)
    {
        var units = new List<List<Word>>();
        foreach (var word in words)
        {
            if (units.Count > 0 && word.Text.Trim(EdgeMarks).Length == 0)
            {
                units[^1].Add(word);
            }
            else
            {
                units.Add([word]);
            }
        }

        return units;
    }

    private static bool RemoveFirstRepeat(List<List<Word>> units)
    {
        for (var n = units.Count / 2; n >= 5; n--)
        {
            for (var i = 0; i + 2 * n <= units.Count; i++)
            {
                var a = units.GetRange(i, n);
                var b = units.GetRange(i + n, n);
                // Gleicher Wortlaut nach Kapitel 5.2 und überlappende Zeitspannen: dasselbe Audio ein zweites Mal erkannt
                if (a.Select(UnitText).SequenceEqual(b.Select(UnitText))
                    && b[0][0].Start < a.SelectMany(unit => unit).Max(w => w.End))
                {
                    units.RemoveRange(i + n, n);
                    return true;
                }
            }
        }

        return false;
    }

    private static string UnitText(List<Word> unit) => DocNormalize(string.Concat(unit.Select(w => w.Text)));

    // Kapitel 5.2: NFC, Kleinschreibung ohne Kultur, ’ als ', am Rand nur die Satz- und Anführungszeichen der Liste.
    private static string DocNormalize(string text) =>
        text.Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('’', '\'').Trim(EdgeMarks);

    private static readonly char[] EdgeMarks = ['.', ',', ';', ':', '!', '?', '…', '"', '„', '“', '”', '«', '»', '‹', '›'];

    private static bool NoOverlap(IReadOnlyList<Word> words) =>
        words.Zip(words.Skip(1), (left, right) => right.Start >= left.End).All(valid => valid);
}
