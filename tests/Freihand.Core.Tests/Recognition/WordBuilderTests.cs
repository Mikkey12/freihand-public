using static Freihand.Core.Tests.Recognition.WordBuilderCases;

namespace Freihand.Core.Tests.Recognition;

/// <summary>
/// Wortbildung aus Whisper-Tokens (Kapitel 5.5, W1–W10): Abnahmetests R01–R12 aus Kapitel 7.1 und je Regel gezielte
/// Fälle mit exakt ausgerechneter Erwartung (W11: UncertainEdgeTests und AudioEndBoundaryTests). Die Daten und die Rechnung je Fall stehen in <see cref="WordBuilderCases"/>;
/// dieselben Beispiele prüfen dort auch die Nachrechnung der Matrix (<see cref="WordBuilderMatrixTests"/>).
/// </summary>
public sealed class WordBuilderTests
{
    // ---- Kapitel 7.1 ------------------------------------------------------------------------------------------

    /// <summary>„ Hallo Welt.“: „Hallo“ [30, 30,4) mit 0,9, „Welt.“ [30,4, 30,82) mit 0,8; Audio [30 s, 32 s).</summary>
    [Fact]
    public void R01_TokensBecomeWordsWithSessionTimes() => AssertExample(R01);

    /// <summary>Sondertokens entfallen samt Wahrscheinlichkeit: genau „Ja“ [30,1, 30,3) mit 0,7.</summary>
    [Fact]
    public void R02_SpecialTokensAreDropped() => AssertExample(R02);

    /// <summary>Ein Wort aus drei Tokens mit Überlappung im Wort: „Rückmeldung“ [31, 31,7) mit 0,6.</summary>
    [Fact]
    public void R03_TokensWithoutLeadingSpaceContinueTheWord() => AssertExample(R03);

    /// <summary>U+FFFD in den Tokens: der Text kommt aus dem Segment, „Grüsse“ [30, 30,5).</summary>
    [Fact]
    public void R04_SplitUtf8TakesTheTextFromTheSegment() => AssertExample(R04);

    /// <summary>Andere Wortzahl oder anderer Text ohne U+FFFD: <c>TokenAlignmentException</c>.</summary>
    [Fact]
    public void R05_MismatchBetweenTokensAndSegmentTextThrows()
    {
        AssertExample(R05a);
        AssertExample(R05b);
    }

    /// <summary>Wörter ohne gültige Token-Zeit teilen sich die Lücke: „zwei“ [30,4, 30,7), „drei“ [30,7, 31).</summary>
    [Fact]
    public void R06_WordsWithoutValidTimesShareTheGap() => AssertExample(R06);

    /// <summary>
    /// Begrenzung (W4), Mindestdauer (W8) und Kürzung (W9) am Audioende, auf dem Raster (32 s) und daneben (31,995 s);
    /// am Ende neben dem Raster teilen die Wörter den vorhandenen Endbereich ohne Überlappung (Entscheid 31.10.2026).
    /// </summary>
    [Fact]
    public void R07_TimesAreClampedToTheAudio()
    {
        AssertExample(R07a);
        AssertExample(R07b);
        AssertExample(R07c);
    }

    /// <summary>Kleine Überlappung (0,05 s) wird gekürzt: „gut“ [30, 30,4), „so“ [30,4, 30,8).</summary>
    [Fact]
    public void R08_SmallNeighborOverlapIsTrimmed() => AssertExample(R08);

    /// <summary>Eine ganze Wiederholung mit 0,5 s Überlappung bleibt zeitgleich stehen (Fixture P3 T62).</summary>
    [Fact]
    public void R09_RepeatedSequenceIsLeftForP3() => AssertExample(R09);

    /// <summary>W7 hebt den Beginn; grössere Überlappungen echter Token-Zeiten bleiben, auch am Audioende neben dem Raster.</summary>
    [Fact]
    public void R10_WordsNeverStartBeforeThePreviousWord()
    {
        AssertExample(R10a);
        AssertExample(R10b);
    }

    /// <summary>NaN, 1,5 und −0,2 ergeben 0, 1 und 0; ein Sondertoken mit 0,1 zählt nicht.</summary>
    [Fact]
    public void R11_ProbabilityIsTheClampedMinimumOfTheWordTokens()
    {
        AssertExample(R11a);
        AssertExample(R11b);
    }

    /// <summary>Leere Gruppe entfällt, jedes Segment für sich, leeres Segment ohne Tokens; ohne Segmente keine Wörter.</summary>
    [Fact]
    public void R12_SegmentsAreProcessedOneByOne()
    {
        AssertExample(R12a);
        AssertExample(R12b);
    }

    // ---- Gezielte Fälle zu W1–W10 (Kapitel 5.5) ----------------------------------------------------------------

    /// <summary>Sondertokens vor, in und nach einem Wort, mit Zeiten über das ganze Audio: „Tor“ [30,1, 30,3) mit 0,7.</summary>
    [Fact]
    public void W01_SpecialTokensInsideAWordAreDroppedWithTheirTimes() => AssertExample(W01);

    /// <summary>Ein Segment, das ohne Leerraum beginnt, beginnt trotzdem ein neues Wort.</summary>
    [Fact]
    public void W02_AWordNeverSpansTwoSegments() => AssertExample(W02);

    /// <summary>Abweichende Schreibung ohne U+FFFD und andere Wortzahl mit U+FFFD werfen.</summary>
    [Fact]
    public void W03_AlignmentIsExact()
    {
        AssertExample(W03a);
        AssertExample(W03b);
    }

    /// <summary>Ein Token vor dem Audio ist ungültig (nicht begrenzt); Ende hinter dem Audio wird begrenzt, auch neben dem Raster.</summary>
    [Fact]
    public void W04_InvalidTimesAreIgnoredAndValidTimesClamped()
    {
        AssertExample(W04a);
        AssertExample(W04b);
    }

    /// <summary>Kleinster Beginn aller Tokens; Lückenteilung mit Abrunden auf Ticks; Lücken am Anfang und am Ende.</summary>
    [Fact]
    public void W05_WordTimesAndGapsAreExact()
    {
        AssertExample(W05a);
        AssertExample(W05b);
        AssertExample(W05c);
    }

    /// <summary>Tokens mit ungültiger Zeit zählen für die Wahrscheinlichkeit; Werte über 1 werden begrenzt.</summary>
    [Fact]
    public void W06_ProbabilityCountsEveryTokenOfTheWord() => AssertExample(W06);

    /// <summary>Beginn auf den des Vorgängers gehoben, auch in einer Kette.</summary>
    [Fact]
    public void W07_StartsAreRaisedToThePreviousStart()
    {
        AssertExample(W07a);
        AssertExample(W07b);
    }

    /// <summary>
    /// Mindestdauer mitten im Audio, am Audioende (nie vor <c>U</c>), kürzer als 10 ms neben dem Raster; ohne Audio hat auch
    /// W11 keinen Platz für einen Tick und es bleibt bei <c>TokenAlignmentException</c> (W08c).
    /// </summary>
    [Fact]
    public void W08_MinimumDurationStaysInsideTheAllowedInterval()
    {
        AssertExample(W08a);
        AssertExample(W08b);
        AssertExample(W08c);
        AssertExample(W08d);
        AssertExample(W08e);
    }

    /// <summary>Messbefund RM01 (Kapitel 10.6): Folgen von Wörtern ohne Dauer hintereinander, mit und ohne genug Platz.</summary>
    [Fact]
    public void W08_RunsOfZeroLengthWordsAreLaidOutOneAfterAnother()
    {
        AssertExample(W08f);
        AssertExample(W08g);
    }

    /// <summary>Toleranz einschliesslich, darüber nicht; nur wenn das Wort nach dem Vorgänger beginnt; je Paar; rettet ein W8-Wort.</summary>
    [Fact]
    public void W09_OnlySmallOverlapsAfterThePreviousStartAreTrimmed()
    {
        AssertExample(W09a);
        AssertExample(W09b);
        AssertExample(W09c);
        AssertExample(W09d);
        AssertExample(W09e);
    }

    /// <summary>
    /// Ein nach W8 gesetztes Wort darf den folgenden oder den vorherigen Nachbarn nicht überlappen: Hier teilen sich die
    /// Wörter dazu den vorhandenen Bereich, ohne dass der Builder wirft (W11 für die übrigen Fälle: <see cref="UncertainEdgeTests"/>).
    /// </summary>
    [Fact]
    public void W10_AnEstimatedWordMustNotOverlapItsNeighbors()
    {
        AssertExample(W10a);
        AssertExample(W10b);
    }
}
