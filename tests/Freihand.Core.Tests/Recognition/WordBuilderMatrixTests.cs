using Freihand.Adapters.Recognition;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using static Freihand.Core.Tests.Recognition.WordBuilderCases;

namespace Freihand.Core.Tests.Recognition;

/// <summary>
/// Zeitklasse eines Worts in der Matrix; Token-Zeiten in 10 ms. Wortplatz k hat nominal (S, S + 40) mit S = 20 + 60k;
/// (ps, pe) sind die rohen Zeiten des Vorgängers, wenn sie gültig sind, sonst seine Nominallage.
/// </summary>
public enum TimeClass
{
    /// <summary>Nominale Lage (S, S + 40).</summary>
    Normal,

    /// <summary>(S, S): Länge 0 mitten im Audio (W8).</summary>
    ZeroLength,

    /// <summary>(−1, −1): ungültig, Start &lt; 0 (W4, W5 Lücke).</summary>
    NegativeStart,

    /// <summary>(S + 40, S): ungültig, End &lt; Start (W4, W5 Lücke).</summary>
    EndBeforeStart,

    /// <summary>(−10, S + 40): beginnt vor <c>AudioStart</c>, darum ungültig und nicht begrenzt (W4).</summary>
    BeforeAudioStart,

    /// <summary>(S, 260): endet nach <c>AudioEnd</c>, wird begrenzt (W4).</summary>
    OverAudioEnd,

    /// <summary>(200, 210): liegt ganz am oder hinter dem Audioende (W4, W8 zweiter Schritt).</summary>
    AtAudioEnd,

    /// <summary>(199, 200): die letzten 10 ms bzw. 5 ms vor dem Audioende.</summary>
    NearAudioEnd,

    /// <summary>(ps − 10, pe + 20): beginnt vor dem Vorgänger (W7); nicht für das erste Wort.</summary>
    Backward,

    /// <summary>(pe − 5, pe + 40): 50 ms Überlappung mit dem Vorgänger (W9); nicht für das erste Wort.</summary>
    SmallOverlap,

    /// <summary>(pe − 10, pe + 40): genau 100 ms = Toleranz (W9 greift noch); nicht für das erste Wort.</summary>
    ToleranceOverlap,

    /// <summary>(pe − 20, pe + 40): 200 ms Überlappung, bleibt stehen; nicht für das erste Wort.</summary>
    LargeOverlap,
}

/// <summary>Aufbau eines Worts aus Tokens; die Zeitklasse gibt (s, e).</summary>
public enum WordShape
{
    /// <summary>Ein Token (s, e).</summary>
    OneToken,

    /// <summary>(s + 2, e) und (s, e − 2): überlappen sich im Wort; Beginn aus dem zweiten, Ende aus dem ersten Token (W5).</summary>
    TwoTokensOverlapping,

    /// <summary>Token (s, e) und ein Token mit End &lt; Start, das weder Zeit noch Wort verlängern darf (W4, W6 zählt es).</summary>
    WithInvalidToken,

    /// <summary>Token (s, e), Sondertoken (0, 300) mit 0,01, Token (s, e) (W1).</summary>
    SpecialInside,

    /// <summary>Zwei Tokens mit U+FFFD für ein geteiltes UTF-8-Zeichen (W3).</summary>
    SplitUtf8,
}

/// <summary>Verteilung der Wörter auf Segmente.</summary>
public enum SegmentLayout
{
    /// <summary>Ein Segment, jedes Wort beginnt mit Leerraum.</summary>
    OneSegment,

    /// <summary>Ein Segment je Wort, Segmenttext und erstes Token ohne Leerraum (W2).</summary>
    SegmentPerWord,
}

/// <summary>
/// Matrix zur Wortbildung W1–W11 (Kapitel 5.5) mit ausdrücklichem Abdeckungsinventar, nach der Lehre aus P3: jede
/// Fehlerart über alle Kombinationen statt einzelner Beispiele.
/// <para><b>Aufbau.</b> Eine Zeile je <c>sampleCount</c> (32 000: <c>AudioEnd</c> 32 s auf dem 10-ms-Raster; 31 920:
/// 31,995 s daneben) und Zeitklasse des zweiten Worts (12 Klassen), also 24 Zeilen. Jede Zeile erzeugt deterministisch,
/// ohne Zufall: erstes Wort in allen 8 Klassen ohne Vorgängerbezug × drittes Wort fehlt oder in allen 12 Klassen × 5
/// Wortformen (Wort i erhält Form (b + i) mod 5) × 2 Segmentaufteilungen × Sondertokens zwischen den Wörtern ja/nein,
/// also 8 × 13 × 5 × 2 × 2 = 2 080 Fälle, zusammen 49 920. Die Wahrscheinlichkeiten der Tokens wechseln
/// deterministisch zwischen 0,9, 0,6, NaN, 1,5 und −0,2; Sondertokens haben 0,01 und Zeiten über das ganze Audio.</para>
/// <para><b>Prüfung je Fall.</b> Entweder <see cref="TokenAlignmentException"/> oder eine Hypothese mit (W10, P3 Vertrag 2
/// Schritt 1; seit W11 für die Wörter ohne eigene positive Tokenzeit) <c>AudioStart</c> = 30 s und <c>AudioEnd</c> = 30 s + <c>sampleCount</c> × 625 Ticks genau, jedes
/// <c>Text</c> nicht leer und ohne Leerraum am Rand, <c>Start &lt; End</c>, im Audio, Starts nicht fallend (W7); ein nach W8
/// oder W11 gesetztes Wort ohne eigene positive Tokenzeit überlappt keinen Nachbarn (W10); Wortzahl und Texte gleich dem Segmenttext (W3); Wahrscheinlichkeit =
/// kleinster Wert der Nicht-Sondertokens, NaN als 0, in [0, 1] (W6); keine kleine Überlappung (≤ Toleranz, nach dem Beginn
/// des Vorgängers) bleibt stehen (W9). Gegenprobe mit P3 (<see cref="HypothesisCheck.Accept"/>): Die Hypothese wird genau
/// dann angenommen, wenn kein Wort seinen Vorgänger überlappt (bei höchstens drei Wörtern gibt es keine Wiederholung nach
/// Schritt 2), und dann unverändert. Zuletzt muss das Ergebnis exakt der <see cref="WordBuilderReference">Nachrechnung</see>
/// gleichen, auch in der Frage, ob eine Ausnahme fällt. Die Nachrechnung muss vorher jedes exakt ausgerechnete Beispiel
/// aus <see cref="WordBuilderCases.All"/> treffen (R01–R12, W01–W10).</para>
/// <para><b>Inventar.</b> Jede Zeile prüft vor dem Produkt, dass in ihr jede Regel aus <see cref="WordMatrix.Rules"/> in
/// mindestens einem Fall mit Hypothese greift (in der Nachrechnung), W11 eingeschlossen; die einzigen Ausnahmen stehen mit Begründung in
/// <see cref="WordMatrix.NotReachable"/> und dürfen dort auch wirklich nicht vorkommen (sonst ist das Inventar veraltet).
/// Nicht in der Matrix, nur in gezielten Fällen: Abweichung zwischen Gruppen und Segmenttext (W3: R05, W03a, W03b), leere
/// Gruppen (W2: R12a), leeres Intervall nach W8 bei <c>U</c> = <c>AudioEnd</c> (W08c), Rundung bei k = 3 (W05b).</para>
/// <para><b>Rotnachweis: was rot werden muss, wenn eine Regel im Produkt fehlt.</b> (W11 und die dadurch gefallenen Würfe von W10: siehe <c>UncertainEdgeTests</c> und <c>RandomEdgeAgreementTests</c>; die Angaben zu W7, W8 und W10 unten stammen von vor W11.) Gemessen am 30.10.2026 mit einer
/// Probeumsetzung (alle 61 Tests grün) und je einer Mutante, die genau eine Regel verletzt. „Zeile“ heisst
/// (<c>sampleCount</c>, Klasse des zweiten Worts); „alle 24“ heisst jede Zeile beider <c>sampleCount</c>.</para>
/// <list type="bullet">
/// <item>W1, Sondertokens nicht entfernt: R02, R11, W01; Matrix alle 24.</item>
/// <item>W2, Segmentanfang ohne Leerraum beginnt kein neues Wort: R12, W02; Matrix alle 24. Leere Gruppen behalten: nur
/// R12 (die Matrix erzeugt keine leeren Gruppen).</item>
/// <item>W3, Text aus den Tokens statt aus dem Segment: R04; Matrix alle 24. Keine Ausnahme bei Abweichung: R05, W03 (die
/// Matrix erzeugt keine Abweichung).</item>
/// <item>W4, End &lt; Start als gültig: R06, W05; Matrix alle 24. Nicht begrenzt: R07, R10, W04, W08; Matrix alle 24.</item>
/// <item>W5, Beginn aus dem ersten Token: W05; Matrix alle 24. Ende aus dem letzten Token: Matrix alle 24. Lücke nicht
/// geteilt: R06, W05; Matrix nur die 6 Zeilen <c>NegativeStart</c>, <c>EndBeforeStart</c>, <c>BeforeAudioStart</c>, weil
/// nur dort zwei Wörter ohne Zeit aufeinander folgen (k ≥ 2).</item>
/// <item>W6, NaN nicht als 0: R11; Matrix alle 24. Erstes statt kleinstes Token: R03, W01, W06; Matrix alle 24. Nicht auf
/// [0, 1] begrenzt: R11, W06; Matrix alle 24.</item>
/// <item>W7 fehlt: R10, W07; Matrix 18 Zeilen, alle ausser (32 000; <c>AtAudioEnd</c>, <c>BeforeAudioStart</c>,
/// <c>EndBeforeStart</c>, <c>NegativeStart</c>, <c>ZeroLength</c>) und (31 920; <c>ZeroLength</c>).</item>
/// <item>W8 fehlt: R07, W08, W09; Matrix alle 24. Erster Schritt nicht auf <c>AudioEnd</c> begrenzt: R07, W08; Matrix alle
/// ausser <c>NearAudioEnd</c> (22). Zweiter Schritt ohne <c>U</c> (immer <c>AudioEnd</c> − 10 ms): nur W08 (W08b); in der
/// Matrix wirft ein solcher Fall ohnehin nach W10.</item>
/// <item>W9 fehlt: R07, R08, W08, W09; Matrix alle ausser <c>ZeroLength</c> (22). Toleranz mit &lt; statt ≤: W09; Matrix
/// 10 Zeilen (<c>Normal</c>, <c>Backward</c>, <c>SmallOverlap</c>, <c>ToleranceOverlap</c>, <c>LargeOverlap</c>). Ohne
/// „nach dessen Beginn“: R10, W07, W09; Matrix 12 Zeilen (<c>Backward</c>, <c>SmallOverlap</c>, <c>ToleranceOverlap</c>,
/// <c>LargeOverlap</c>, <c>OverAudioEnd</c>, <c>NearAudioEnd</c>).</item>
/// <item>W10, Prüfung der nach W8 gesetzten Wörter fehlt: R07, W10; Matrix alle 24. Auch echte Überlappungen abgewiesen:
/// R09, R10, W07, W09; Matrix 14 Zeilen (<c>Normal</c>, <c>Backward</c>, <c>SmallOverlap</c>, <c>ToleranceOverlap</c>,
/// <c>LargeOverlap</c>, <c>OverAudioEnd</c>, <c>NearAudioEnd</c>).</item>
/// </list>
/// </summary>
public sealed class WordBuilderMatrixTests
{
    public static TheoryData<int, TimeClass> Rows()
    {
        var rows = new TheoryData<int, TimeClass>();
        foreach (var sampleCount in WordMatrix.SampleCounts)
        {
            foreach (var second in Enum.GetValues<TimeClass>())
            {
                rows.Add(sampleCount, second);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void W01_W10_EveryCombinationIsAValidHypothesisOrTokenAlignment(int sampleCount, TimeClass second)
    {
        Assert.True(WordBuilderReference.SelfCheck.Value is null, WordBuilderReference.SelfCheck.Value);

        var cases = WordMatrix.Row(sampleCount, second)
            .Select(c => (Case: c, Expected: WordBuilderReference.Build(c.Segments, Offset, c.SampleCount, Options.NeighborOverlapTolerance)))
            .ToList();

        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, expected) in cases)
        {
            if (expected.Failure is null)
            {
                covered.UnionWith(expected.Rules);
                covered.Add(WordMatrix.HypothesisOutcome);
            }
            else
            {
                covered.Add(expected.Failure);
            }
        }

        var missing = WordMatrix.Required(sampleCount, second).Where(rule => !covered.Contains(rule)).ToList();
        Assert.True(missing.Count == 0, $"Inventar der Zeile ({sampleCount}, {second}) unvollständig: {string.Join(", ", missing)}");
        var reached = WordMatrix.NotReachable(sampleCount, second).Where(covered.Contains).ToList();
        Assert.True(reached.Count == 0, $"Inventar der Zeile ({sampleCount}, {second}) veraltet, erreicht: {string.Join(", ", reached)}");

        var violations = new List<string>();
        foreach (var (matrixCase, expected) in cases)
        {
            violations.AddRange(Check(matrixCase, expected).Select(violation => $"{matrixCase.Name}: {violation}"));
        }

        Assert.True(
            violations.Count == 0,
            $"{violations.Count} Verstösse in {cases.Count} Fällen, die ersten:\n{string.Join("\n", violations.Take(12))}");
    }

    /// <summary>Alle Verstösse eines Falls.</summary>
    private static List<string> Check(MatrixCase matrixCase, ReferenceResult expected)
    {
        var violations = new List<string>();
        RecognitionHypothesis actual;
        try
        {
            actual = WhisperWordBuilder.Build(matrixCase.Segments, Offset, matrixCase.SampleCount, Options);
        }
        catch (TokenAlignmentException)
        {
            if (expected.Failure is null)
            {
                violations.Add($"TokenAlignmentException, erwartet {WordBuilderReference.Format(expected.Hypothesis!.Words)}");
            }

            return violations;
        }
#pragma warning disable CA1031 // jede andere Ausnahme ist hier ein Befund, kein Abbruch der Matrix
        catch (Exception exception)
#pragma warning restore CA1031
        {
            violations.Add($"unerwartete Ausnahme {exception.GetType().Name}");
            return violations;
        }

        if (expected.Failure is not null)
        {
            violations.Add($"Hypothese {WordBuilderReference.Format(actual?.Words)}, erwartet TokenAlignmentException ({expected.Failure})");
        }

        if (actual?.Words is null)
        {
            violations.Add("keine Hypothese oder keine Wörter");
            return violations;
        }

        var audioStart = Offset;
        var audioEnd = Offset + TimeSpan.FromTicks(matrixCase.SampleCount * 625L);
        var words = actual.Words;

        // W10 bzw. P3 Vertrag 2, Schritt 1
        if (actual.AudioStart != audioStart || actual.AudioEnd != audioEnd)
        {
            violations.Add($"W10: Audio [{actual.AudioStart.Ticks}, {actual.AudioEnd.Ticks}) statt [{audioStart.Ticks}, {audioEnd.Ticks})");
        }

        // W3: Wortzahl und Texte aus dem Segmenttext
        if (!words.Select(word => word?.Text).SequenceEqual(matrixCase.Texts))
        {
            violations.Add($"W3: Texte {string.Join(" ", words.Select(word => word?.Text))} statt {string.Join(" ", matrixCase.Texts)}");
            return violations;
        }

        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (string.IsNullOrEmpty(word.Text) || char.IsWhiteSpace(word.Text[0]) || char.IsWhiteSpace(word.Text[^1]))
            {
                violations.Add($"W10: Wort {i} leer oder mit Leerraum am Rand");
            }

            if (word.Start >= word.End)
            {
                violations.Add($"W10: Wort {i} hat Start {word.Start.Ticks} ≥ End {word.End.Ticks}");
            }

            if (word.Start < audioStart || word.End > audioEnd)
            {
                violations.Add($"W10: Wort {i} [{word.Start.Ticks}, {word.End.Ticks}) liegt nicht im Audio");
            }

            if (i > 0 && word.Start < words[i - 1].Start)
            {
                violations.Add($"W7: Wort {i} beginnt vor dem vorherigen");
            }

            // W6: unabhängig von der Nachrechnung aus den erzeugten Tokens
            if (!word.Probability.Equals(matrixCase.Probabilities[i]))
            {
                violations.Add($"W6: Wort {i} hat Wahrscheinlichkeit {word.Probability} statt {matrixCase.Probabilities[i]}");
            }

            if (i > 0)
            {
                var previous = words[i - 1];
                if (word.Start > previous.Start && word.Start < previous.End
                    && previous.End - word.Start <= Options.NeighborOverlapTolerance)
                {
                    violations.Add($"W9: kleine Überlappung zwischen Wort {i - 1} und {i} stehen geblieben");
                }
            }

            // W10: ein nach W8 gesetztes Wort überlappt keinen Nachbarn
            if (expected.Failure is null && expected.SetByW8[i])
            {
                if ((i > 0 && words[i - 1].End > word.Start) || (i + 1 < words.Count && word.End > words[i + 1].Start))
                {
                    violations.Add($"W10: nach W8 gesetztes Wort {i} überlappt einen Nachbarn");
                }
            }
        }

        // Gegenprobe mit P3: angenommen genau dann, wenn kein Wort seinen Vorgänger überlappt
        var overlapping = Enumerable.Range(1, Math.Max(0, words.Count - 1)).Any(i => words[i].Start < words[i - 1].End);
        try
        {
            var accepted = HypothesisCheck.Accept(actual, audioStart, audioEnd);
            if (overlapping)
            {
                violations.Add("P3: Hypothese mit Überlappung angenommen");
            }
            else if (!accepted.SequenceEqual(words))
            {
                violations.Add("P3: angenommene Wörter verändert");
            }
        }
        catch (InvalidHypothesisException exception)
        {
            if (!overlapping)
            {
                violations.Add($"P3 weist ab: {exception.Message}");
            }
        }

        if (expected.Failure is null && !words.SequenceEqual(expected.Hypothesis!.Words))
        {
            violations.Add($"Ergebnis {WordBuilderReference.Format(words)}, erwartet {WordBuilderReference.Format(expected.Hypothesis.Words)}");
        }

        return violations;
    }
}

/// <summary>Ein erzeugter Fall der Matrix.</summary>
/// <param name="Texts">Wörter des Segmenttexts in ihrer Reihenfolge (W3).</param>
/// <param name="Probabilities">Kleinster Wert der Nicht-Sondertokens je Wort, NaN als 0, in [0, 1] (W6).</param>
internal sealed record MatrixCase(
    string Name, int SampleCount, IReadOnlyList<WhisperSegmentData> Segments, IReadOnlyList<string> Texts,
    IReadOnlyList<float> Probabilities);

/// <summary>Ergebnis der Nachrechnung.</summary>
/// <param name="SetByW8">Je Wort, ob W8 oder W11 seine Zeit gesetzt hat und es keine eigene positive Tokenzeit hat (nur solche Wörter dürfen keinen Nachbarn überlappen).</param>
/// <param name="Failure">Grund der erwarteten <see cref="TokenAlignmentException"/>, sonst <c>null</c>.</param>
/// <param name="Rules">Regeln, die gegriffen haben (Inventar).</param>
internal sealed record ReferenceResult(
    RecognitionHypothesis? Hypothesis, IReadOnlyList<bool> SetByW8, string? Failure, IReadOnlySet<string> Rules);

/// <summary>Erzeugung der Matrixfälle und das Inventar.</summary>
internal static class WordMatrix
{
    public const string HypothesisOutcome = "Hypothese";

    public static readonly int[] SampleCounts = [SampleCount, OffGridSampleCount];

    /// <summary>Regeln und Ausgänge, die jede Zeile enthalten muss.</summary>
    public static readonly string[] Rules =
    [
        WordBuilderReference.W1, WordBuilderReference.W3Replacement, WordBuilderReference.W4Invalid,
        WordBuilderReference.W4Clamped, WordBuilderReference.W5Inside, WordBuilderReference.W5Gap,
        WordBuilderReference.W6NaN, WordBuilderReference.W6Clamped, WordBuilderReference.W7, WordBuilderReference.W8,
        WordBuilderReference.W8AtEnd, WordBuilderReference.W9, WordBuilderReference.W11, HypothesisOutcome,
    ];

    private static readonly string[] Words = ["Käse", "Öl", "Tür"];

    private static readonly (string Head, string Tail)[] Parts = [("Kä", "se"), ("Ö", "l"), ("Tü", "r")];

    private static readonly (string Head, string Tail)[] Utf8Parts =
        [("K�", "�se"), ("�", "�l"), ("T�", "�r")];

    private static readonly float[] TokenProbabilities = [0.9f, 0.6f, float.NaN, 1.5f, -0.2f];

    /// <summary>
    /// Regeln, die eine Zeile aus dem Aufbau heraus nicht erreichen kann (mit Begründung); alles andere aus
    /// <see cref="Rules"/> muss jede Zeile enthalten.
    /// </summary>
    /// <remarks>
    /// Gilt für beide <c>sampleCount</c>: Seit W11 (Entscheid 05.11.2026) kippt ein Zeitfehler am Rand die Hypothese nicht
    /// mehr; darum sind W7 und der zweite Schritt von W8 auch in den Zeilen erreichbar, in denen früher W10 warf. Es bleibt:
    /// <list type="bullet">
    /// <item><c>ZeroLength</c>: W9. Wort 2 bei 30,8 s ohne Dauer legt W8 hinter Wort 1, mit Platz bis zum nächsten Wort oder
    /// zum Audioende (Kapitel 10.6); seine Lage auf dem 10-ms-Raster lässt keine kleine Überlappung (W9) zu.</item>
    /// </list>
    /// </remarks>
    public static IReadOnlySet<string> NotReachable(int sampleCount, TimeClass second) => second switch
    {
        TimeClass.ZeroLength => new HashSet<string>(StringComparer.Ordinal) { WordBuilderReference.W9 },
        _ => new HashSet<string>(StringComparer.Ordinal),
    };

    public static IEnumerable<string> Required(int sampleCount, TimeClass second) =>
        Rules.Where(rule => !NotReachable(sampleCount, second).Contains(rule));

    public static bool AllowedFirst(TimeClass timeClass) =>
        timeClass is not (TimeClass.Backward or TimeClass.SmallOverlap or TimeClass.ToleranceOverlap or TimeClass.LargeOverlap);

    public static IEnumerable<MatrixCase> Row(int sampleCount, TimeClass second)
    {
        var thirds = new TimeClass?[] { null }.Concat(Enum.GetValues<TimeClass>().Select(c => (TimeClass?)c)).ToArray();
        foreach (var first in Enum.GetValues<TimeClass>().Where(AllowedFirst))
        {
            foreach (var third in thirds)
            {
                for (var shapeBase = 0; shapeBase < 5; shapeBase++)
                {
                    foreach (var layout in Enum.GetValues<SegmentLayout>())
                    {
                        foreach (var specials in new[] { false, true })
                        {
                            TimeClass[] classes = third is { } t ? [first, second, t] : [first, second];
                            yield return Create(sampleCount, classes, shapeBase, layout, specials);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Token-Zeiten (10 ms) einer Zeitklasse auf Wortplatz <paramref name="slot"/>. Die Klassen mit Vorgängerbezug
    /// beziehen sich auf die rohen Zeiten (ps, pe) des Vorgängers, wenn diese gültig sind, sonst auf seine Nominallage.
    /// </summary>
    public static (long Start, long End) Times(TimeClass timeClass, int slot, (long Start, long End)? previous)
    {
        var start = 20 + (60 * slot);
        var end = start + 40;
        var (ps, pe) = previous ?? (start - 60, start - 20);
        return timeClass switch
        {
            TimeClass.Normal => (start, end),
            TimeClass.ZeroLength => (start, start),
            TimeClass.NegativeStart => (-1, -1),
            TimeClass.EndBeforeStart => (end, start),
            TimeClass.BeforeAudioStart => (-10, end),
            TimeClass.OverAudioEnd => (start, 260),
            TimeClass.AtAudioEnd => (200, 210),
            TimeClass.NearAudioEnd => (199, 200),
            TimeClass.Backward => (ps - 10, pe + 20),
            TimeClass.SmallOverlap => (pe - 5, pe + 40),
            TimeClass.ToleranceOverlap => (pe - 10, pe + 40),
            TimeClass.LargeOverlap => (pe - 20, pe + 40),
            _ => throw new ArgumentOutOfRangeException(nameof(timeClass)),
        };
    }

    private static MatrixCase Create(int sampleCount, TimeClass[] classes, int shapeBase, SegmentLayout layout, bool specials)
    {
        var segments = new List<WhisperSegmentData>();
        var oneSegment = new List<WhisperTokenData>();
        var texts = new List<string>();
        var probabilities = new List<float>();
        var shapes = new List<WordShape>();
        (long Start, long End)? previous = null;

        for (var i = 0; i < classes.Length; i++)
        {
            var shape = (WordShape)((shapeBase + i) % 5);
            shapes.Add(shape);
            var (start, end) = Times(classes[i], i, previous);
            previous = start >= 0 && end >= start ? (start, end) : null;
            var lead = layout == SegmentLayout.OneSegment ? " " : string.Empty;
            var head = TokenProbabilities[(shapeBase + (2 * i) + (int)layout) % 5];
            var tail = TokenProbabilities[(shapeBase + (2 * i) + 3 + (int)layout + (specials ? 1 : 0)) % 5];

            var tokens = new List<WhisperTokenData>();
            if (specials)
            {
                tokens.Add(Special($"[_TT_{i}]", 0, 300, 0.01f));
            }

            var used = new List<float> { head };
            switch (shape)
            {
                case WordShape.OneToken:
                    tokens.Add(Tok(lead + Words[i], start, end, head));
                    break;
                case WordShape.TwoTokensOverlapping:
                    // das zweite Token beginnt früher, das erste endet später: Start und End sind nie die des ersten
                    // bzw. letzten Tokens
                    var valid = start >= 0 && end >= start;
                    tokens.Add(Tok(lead + Parts[i].Head, valid ? Math.Min(start + 2, end) : start, end, head));
                    tokens.Add(Tok(Parts[i].Tail, start, valid ? Math.Max(end - 2, start) : end, tail));
                    used.Add(tail);
                    break;
                case WordShape.WithInvalidToken:
                    var after = Math.Max(end, 0);
                    tokens.Add(Tok(lead + Parts[i].Head, start, end, head));
                    tokens.Add(Tok(Parts[i].Tail, after + 30, after + 10, tail));
                    used.Add(tail);
                    break;
                case WordShape.SpecialInside:
                    tokens.Add(Tok(lead + Parts[i].Head, start, end, head));
                    tokens.Add(Special($"[_TT_{150 + i}]", 0, 300, 0.01f));
                    tokens.Add(Tok(Parts[i].Tail, start, end, tail));
                    used.Add(tail);
                    break;
                case WordShape.SplitUtf8:
                    tokens.Add(Tok(lead + Utf8Parts[i].Head, start, end, head));
                    tokens.Add(Tok(Utf8Parts[i].Tail, start, end, tail));
                    used.Add(tail);
                    break;
            }

            texts.Add(Words[i]);
            probabilities.Add(Math.Clamp(used.Select(p => float.IsNaN(p) ? 0f : p).Min(), 0f, 1f));

            if (layout == SegmentLayout.OneSegment)
            {
                oneSegment.AddRange(tokens);
            }
            else
            {
                if (specials)
                {
                    tokens.Add(Special("<|endoftext|>", 300, 300, 0.01f));
                }

                segments.Add(new WhisperSegmentData(Words[i], tokens));
            }
        }

        if (layout == SegmentLayout.OneSegment)
        {
            if (specials)
            {
                oneSegment.Add(Special("<|endoftext|>", 300, 300, 0.01f));
            }

            segments.Add(new WhisperSegmentData(" " + string.Join(" ", Words.Take(classes.Length)), oneSegment));
        }

        var name = $"[{sampleCount}; {string.Join("/", classes)}; {string.Join("/", shapes)}; {layout}; Sonder {(specials ? "ja" : "nein")}]";
        return new MatrixCase(name, sampleCount, segments, texts, probabilities);
    }
}

/// <summary>
/// Nachrechnung der Wortbildung, Schritt für Schritt wörtlich nach Kapitel 5.5 (W1–W10), in Ticks. Sie dient der Matrix
/// als Orakel; dass sie das Konzept trifft, belegt <see cref="SelfCheck"/> an allen von Hand ausgerechneten Beispielen.
/// <para>Lesarten: Die Lücke nach W5 läuft über die Wortfolge der ganzen Hypothese (W5 spricht wie W7 vom vorherigen
/// Wort). <c>U</c> in W7 und W8 ist der <c>Start</c> des vorherigen Worts nach dessen eigener Behandlung durch W8; W7 und W8
/// laufen darum Wort für Wort (Kapitel 10.4; Codex-Gegenprobe C01). Eine verkehrte Lücke (b &lt; a) gilt als leer
/// (b = a, Kapitel 10.4).</para>
/// </summary>
internal static class WordBuilderReference
{
    public const string W1 = "W1 Sondertoken";
    public const string W2Empty = "W2 leere Gruppe";
    public const string W3Replacement = "W3 U+FFFD";
    public const string W4Invalid = "W4 ungültig";
    public const string W4Clamped = "W4 begrenzt";
    public const string W5Inside = "W5 Überlappung im Wort";
    public const string W5Gap = "W5 Lücke";
    public const string W6NaN = "W6 NaN";
    public const string W6Clamped = "W6 begrenzt";
    public const string W7 = "W7";
    public const string W8 = "W8";
    public const string W8AtEnd = "W8 Audioende";
    public const string W9 = "W9";
    public const string W3Count = "Ausnahme W3 Wortzahl";
    public const string W3Text = "Ausnahme W3 Text";
    public const string W11 = "W11";
    public const string W11NoRoom = "Ausnahme W11 kein Platz";
    public const string W10Basic = "Ausnahme W10 Grundregel";
    public const string W10Overlap = "Ausnahme W10 geschätztes Wort überlappt";

    /// <summary>10 ms in Ticks.</summary>
    private const long Unit = 100_000;

    /// <summary><c>null</c>, wenn die Nachrechnung jedes Beispiel aus <see cref="WordBuilderCases.All"/> exakt trifft.</summary>
    public static readonly Lazy<string?> SelfCheck = new(() =>
    {
        var errors = new List<string>();
        foreach (var example in WordBuilderCases.All)
        {
            var result = Build(example.Segments, Offset, example.SampleCount, Options.NeighborOverlapTolerance);
            if (example.Words is null)
            {
                if (result.Failure is null)
                {
                    errors.Add($"{example.Name}: Nachrechnung ohne Ausnahme, {Format(result.Hypothesis!.Words)}");
                }

                continue;
            }

            if (result.Failure is not null)
            {
                errors.Add($"{example.Name}: Nachrechnung wirft ({result.Failure})");
            }
            else if (result.Hypothesis!.AudioEnd != example.AudioEnd || !result.Hypothesis.Words.SequenceEqual(example.Words))
            {
                errors.Add($"{example.Name}: Nachrechnung {Format(result.Hypothesis.Words)}, Beispiel {Format(example.Words)}");
            }
        }

        return errors.Count == 0 ? null : "Nachrechnung weicht von Beispielen ab:\n" + string.Join("\n", errors);
    });

    public static string Format(IReadOnlyList<RecognizedWord>? words) => words is null
        ? "(keine)"
        : string.Join(" ", words.Select(word => word is null
            ? "(null)"
            : $"{word.Text}[{word.Start.Ticks},{word.End.Ticks}){word.Probability}"));

    public static ReferenceResult Build(
        IReadOnlyList<WhisperSegmentData> segments, TimeSpan offset, int sampleCount, TimeSpan tolerance)
    {
        var rules = new HashSet<string>(StringComparer.Ordinal);
        var audioStart = offset.Ticks;
        var audioEnd = offset.Ticks + (sampleCount * 625L);
        var texts = new List<string>();
        var wordTokens = new List<List<WhisperTokenData>>();

        foreach (var segment in segments)
        {
            // W1, W2
            var groups = new List<List<WhisperTokenData>>();
            foreach (var token in segment.Tokens)
            {
                if (token.IsSpecial)
                {
                    rules.Add(W1);
                    continue;
                }

                if (groups.Count == 0 || (token.Text.Length > 0 && char.IsWhiteSpace(token.Text[0])))
                {
                    groups.Add([]);
                }

                groups[^1].Add(token);
            }

            var kept = new List<(string Text, List<WhisperTokenData> Tokens)>();
            foreach (var group in groups)
            {
                var text = string.Concat(group.Select(token => token.Text)).Trim();
                if (text.Length == 0)
                {
                    rules.Add(W2Empty);
                    continue;
                }

                kept.Add((text, group));
            }

            // W3
            var spoken = segment.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (spoken.Length != kept.Count)
            {
                return Failed(W3Count, rules);
            }

            for (var i = 0; i < kept.Count; i++)
            {
                if (kept[i].Text.Contains('�', StringComparison.Ordinal))
                {
                    rules.Add(W3Replacement);
                }
                else if (!string.Equals(kept[i].Text, spoken[i], StringComparison.Ordinal))
                {
                    return Failed(W3Text, rules);
                }

                texts.Add(spoken[i]);
                wordTokens.Add(kept[i].Tokens);
            }
        }

        var n = texts.Count;
        var start = new long[n];
        var end = new long[n];
        var timed = new bool[n];
        var probability = new float[n];

        long Raw(long t) => audioStart + (t * Unit);
        long Clamp(long t) => Math.Clamp(Raw(t), audioStart, audioEnd);

        // W4, W5 innerhalb des Worts
        for (var i = 0; i < n; i++)
        {
            var valid = wordTokens[i].Where(token => token.Start >= 0 && token.End >= token.Start).ToList();
            if (valid.Count < wordTokens[i].Count)
            {
                rules.Add(W4Invalid);
            }

            if (valid.Count == 0)
            {
                continue;
            }

            timed[i] = true;
            start[i] = valid.Min(token => Clamp(token.Start));
            end[i] = valid.Max(token => Clamp(token.End));
            if (valid.Any(token => Raw(token.Start) != Clamp(token.Start) || Raw(token.End) != Clamp(token.End)))
            {
                rules.Add(W4Clamped);
            }

            for (var j = 1; j < valid.Count; j++)
            {
                if (valid[j].Start < valid[j - 1].End)
                {
                    rules.Add(W5Inside);
                }
            }
        }

        var tokenStartSnapshot = start.ToArray();
        var tokenEndSnapshot = end.ToArray();
        // W5 Lücke: k Wörter ohne gültige Zeit in [a, b)
        for (var i = 0; i < n;)
        {
            if (timed[i])
            {
                i++;
                continue;
            }

            var j = i;
            while (j < n && !timed[j])
            {
                j++;
            }

            var k = j - i;
            var a = i > 0 ? end[i - 1] : audioStart;
            // Kapitel 10.4: Beginnt das nächste Wort mit Zeit vor dem Ende des vorherigen (b < a), gilt b = a.
            var b = Math.Max(j < n ? start[j] : audioEnd, a);
            for (var m = 0; m < k; m++)
            {
                start[i + m] = a + FloorDiv(m * (b - a), k);
                end[i + m] = m == k - 1 ? b : a + FloorDiv((m + 1) * (b - a), k);
            }

            rules.Add(W5Gap);
            i = j;
        }

        var audioEndZero = Enumerable.Range(0, n).Select(index => start[index] == audioEnd && end[index] == audioEnd).ToArray();
        // W6
        for (var i = 0; i < n; i++)
        {
            if (wordTokens[i].Any(token => float.IsNaN(token.Probability)))
            {
                rules.Add(W6NaN);
            }

            var lowest = wordTokens[i].Select(token => float.IsNaN(token.Probability) ? 0f : token.Probability).Min();
            if (lowest is < 0f or > 1f)
            {
                rules.Add(W6Clamped);
            }

            probability[i] = Math.Clamp(lowest, 0f, 1f);
        }

        // W7 und W8 Wort für Wort (Kapitel 10.4, Codex C01); W8 legt Folgen ohne Dauer hintereinander (Kapitel 10.6)
        var setByW8 = new bool[n];
        for (var i = 0; i < n;)
        {
            // W7
            var lower = i == 0 ? audioStart : start[i - 1];
            if (start[i] < lower)
            {
                start[i] = lower;
                rules.Add(W7);
            }

            if (end[i] > start[i])
            {
                i++;
                continue;
            }

            rules.Add(W8);
            var j = i + 1;
            while (j < n && end[j] <= start[j])
            {
                j++;
            }

            var k = j - i;
            var from = Math.Max(start[i], i == 0 ? audioStart : end[i - 1]);
            var to = Math.Min(j < n ? start[j] : audioEnd, audioEnd);
            if (from < audioEnd && to - from >= k)
            {
                for (var m = 0; m < k; m++)
                {
                    setByW8[i + m] = true;
                    if (to - from >= k * Unit)
                    {
                        start[i + m] = from + (m * Unit);
                        end[i + m] = from + ((m + 1) * Unit);
                    }
                    else
                    {
                        start[i + m] = from + FloorDiv(m * (to - from), k);
                        end[i + m] = m == k - 1 ? to : from + FloorDiv((m + 1) * (to - from), k);
                    }
                }

                i = j;
                continue;
            }

            // Entscheid 30.10.2026: Nur die exakt gleich beginnende Nullfolge darf das positive Folgewort teilen.
            // Unabhängige Nachrechnung: zuerst gemeinsamer Bereich, dann ganzzahlige Dauer je Teil.
            var anchor = start[i];
            var shareable = j < n && start[j] == anchor && end[j] > anchor && end[j] <= audioEnd
                && (i == 0 || end[i - 1] <= anchor)
                && Enumerable.Range(i, k).All(index => start[index] == anchor && end[index] == anchor);
            if (shareable)
            {
                var step = Math.Min(Unit, FloorDiv(end[j] - anchor, k + 1L));
                if (step > 0)
                {
                    for (var index = i; index < j; index++)
                    {
                        setByW8[index] = true;
                        start[index] = anchor + (index - i) * step;
                        end[index] = anchor + (index - i + 1L) * step;
                    }

                    start[j] = anchor + k * step;
                    i = j;
                    continue;
                }
            }

            // Entscheid 31.10.2026: letzte Nullfolge zuerst in einen freien Suffix legen; nur ein unverändertes,
            // tokenbelegtes positives Vorgängerwort darf bei Bedarf selbst Dauer abgeben. W9/W10 bleiben aktiv.
            if (j == n && Enumerable.Range(i, k).All(index => audioEndZero[index])
                && (i == 0 || end[i - 1] > start[i - 1]))
            {
                rules.Add(W8AtEnd);
                var tailStart = Math.Max(i == 0 ? audioStart : end[i - 1], audioEnd - k * Unit);
                if (audioEnd - tailStart >= k)
                {
                    for (var m = 0; m < k; m++)
                    {
                        setByW8[i + m] = true;
                        start[i + m] = tailStart + m * (audioEnd - tailStart) / k;
                        end[i + m] = m == k - 1 ? audioEnd : tailStart + (m + 1L) * (audioEnd - tailStart) / k;
                    }

                    i = j;
                    continue;
                }

                if (i > 0 && timed[i - 1] && !setByW8[i - 1] && end[i - 1] == audioEnd
                    && start[i - 1] == tokenStartSnapshot[i - 1] && end[i - 1] == tokenEndSnapshot[i - 1])
                {
                    var duration = Math.Min(Unit, (audioEnd - start[i - 1]) / (k + 1L));
                    if (duration >= 1)
                    {
                        tailStart = audioEnd - k * duration;
                        end[i - 1] = tailStart;
                        setByW8[i - 1] = true;
                        for (var m = 0; m < k; m++)
                        {
                            setByW8[i + m] = true;
                            start[i + m] = tailStart + m * duration;
                            end[i + m] = tailStart + (m + 1L) * duration;
                        }

                        i = j;
                        continue;
                    }
                }
            }

            // kein Platz: wie bisher im zulässigen Intervall [U, AudioEnd]; liegt das Wort schon bei U = AudioEnd,
            // bleibt es ohne Dauer stehen und W11 ordnet es ein
            setByW8[i] = true;
            end[i] = Math.Min(start[i] + Unit, audioEnd);
            if (end[i] <= start[i])
            {
                var shifted = Math.Max(lower, audioEnd - Unit);
                if (shifted < audioEnd)
                {
                    start[i] = shifted;
                    end[i] = audioEnd;
                    rules.Add(W8AtEnd);
                }
            }

            i++;
        }

        // W9
        for (var i = 1; i < n; i++)
        {
            if (start[i] < end[i - 1] && end[i - 1] - start[i] <= tolerance.Ticks && start[i] > start[i - 1])
            {
                end[i - 1] = start[i];
                rules.Add(W9);
            }
        }

        // W11 (Entscheid 05.11.2026): Wörter ohne eigene positive Tokenzeit, die gegen W10 verstossen, werden eingeordnet
        var placed = new bool[n];
        bool OwnPositive(int index) => timed[index] && tokenEndSnapshot[index] > tokenStartSnapshot[index];
        bool BreaksBasics(int index) => start[index] >= end[index] || start[index] < audioStart || end[index] > audioEnd
            || (index > 0 && start[index] < start[index - 1]);
        bool OverlapsAsEstimate(int index) => setByW8[index] && !OwnPositive(index)
            && ((index > 0 && end[index - 1] > start[index]) || (index + 1 < n && end[index] > start[index + 1]));

        for (var i = 0; i < n;)
        {
            if (OwnPositive(i) || !(BreaksBasics(i) || (!placed[i] && OverlapsAsEstimate(i))))
            {
                i++;
                continue;
            }

            if (placed[i])
            {
                return Failed(W11NoRoom, rules);
            }

            // Folge ohne Dauer ab diesem Wort und ihre Grenze
            var tailEnd = i;
            while (tailEnd + 1 < n && end[tailEnd + 1] <= start[tailEnd + 1])
            {
                tailEnd++;
            }

            var boundary = tailEnd + 1 < n ? start[tailEnd + 1] : audioEnd;
            var count = tailEnd - i + 1;
            rules.Add(W11);

            // Das Wort davor gibt Dauer ab
            if (i > 0)
            {
                var part = Math.Min(Unit, FloorDiv(boundary - start[i - 1], count + 1L));
                if (part >= 1)
                {
                    // Reicht das Wort davor über die Grenze hinaus, ist das eine echte Überlappung: sein Ende bleibt
                    if (end[i - 1] - boundary <= tolerance.Ticks)
                    {
                        end[i - 1] = Math.Min(end[i - 1], boundary - count * part);
                    }

                    for (var m = 0; m < count; m++)
                    {
                        start[i + m] = boundary - (count - m) * part;
                        end[i + m] = boundary - (count - m - 1L) * part;
                        placed[i + m] = true;
                    }

                    i = tailEnd + 1;
                    continue;
                }
            }

            // Sonst rückwärts zum ersten Wort, ab dem je Wort mindestens ein Tick Platz bleibt
            var origin = -1;
            for (var g = i > 0 ? i - 1 : 0; g >= 0 && origin < 0; g--)
            {
                if (boundary - start[g] >= tailEnd - g + 1)
                {
                    origin = g;
                }
            }

            if (origin < 0)
            {
                return Failed(W11NoRoom, rules);
            }

            var span = boundary - start[origin];
            var total = tailEnd - origin + 1;
            var from11 = start[origin];
            for (var m = 0; m < total; m++)
            {
                start[origin + m] = from11 + FloorDiv(m * span, total);
                var evenEnd = m == total - 1 ? boundary : from11 + FloorDiv((m + 1L) * span, total);
                end[origin + m] = end[origin + m] - boundary > tolerance.Ticks ? end[origin + m] : evenEnd;
                placed[origin + m] = true;
            }

            i = tailEnd + 1;
        }

        // W10
        var insideOverlap = new bool[n];
        for (var i = 0; i < n; i++)
        {
            if (BreaksBasics(i))
            {
                return Failed(W10Basic, rules);
            }

            // W11: Ein eingeordnetes Wort darf in einer echten Überlappung liegen, die der Vorgänger behält
            if (placed[i])
            {
                insideOverlap[i] = (i > 0 && end[i - 1] > start[i]) || (i + 1 < n && end[i] > start[i + 1]);
            }
            else if (OverlapsAsEstimate(i))
            {
                return Failed(W10Overlap, rules);
            }
        }

        var words = Enumerable.Range(0, n)
            .Select(i => new RecognizedWord(texts[i], TimeSpan.FromTicks(start[i]), TimeSpan.FromTicks(end[i]), probability[i]))
            .ToList();
        return new ReferenceResult(
            new RecognitionHypothesis(TimeSpan.FromTicks(audioStart), TimeSpan.FromTicks(audioEnd), words),
            Enumerable.Range(0, n).Select(index => (setByW8[index] || placed[index]) && !OwnPositive(index) && !insideOverlap[index]).ToArray(), null, rules);
    }

    private static ReferenceResult Failed(string reason, HashSet<string> rules) => new(null, [], reason, rules);

    /// <summary>Ganzzahlige Division, abgerundet (gegen −∞).</summary>
    private static long FloorDiv(long dividend, long divisor)
    {
        var quotient = dividend / divisor;
        return dividend % divisor != 0 && (dividend < 0) != (divisor < 0) ? quotient - 1 : quotient;
    }
}
