using Freihand.Adapters.Recognition;
using Freihand.Core.Recognition;

namespace Freihand.Core.Tests.Recognition;

/// <summary>Ein Beispiel zur Wortbildung mit exakt ausgerechneter Erwartung (Kapitel 7.1 oder gezielter Fall zu W1–W11).</summary>
/// <param name="Name">Konzeptnummer und Teil, z. B. „R07b“.</param>
/// <param name="SampleCount">Samples bei 16 kHz; <c>offset</c> ist immer 30 s.</param>
/// <param name="AudioEnd">Von Hand ausgerechnetes <c>AudioEnd</c>.</param>
/// <param name="Words">Erwartete Wörter; <c>null</c> heisst <see cref="TokenAlignmentException"/> (W3, oder nicht einmal je Wort ein Tick Platz nach W11).</param>
internal sealed record WordExample(
    string Name, IReadOnlyList<WhisperSegmentData> Segments, int SampleCount, TimeSpan AudioEnd,
    IReadOnlyList<RecognizedWord>? Words);

/// <summary>
/// Gemeinsame Daten der Wortbildungstests. Startwerte nach Kapitel 7: <c>offset</c> 30 s, <c>sampleCount</c> 32 000
/// (<c>AudioEnd</c> 32 s), Token-Wahrscheinlichkeit 0,9, <c>NeighborOverlapTolerance</c> 0,1 s. Token-Zeiten stehen in
/// Einheiten von 10 ms (= 100 000 Ticks), erwartete Zeiten in Millisekunden der Sitzungszeit (1 ms = 10 000 Ticks), wo
/// nötig in Ticks. Alle Texte sind erfunden.
/// </summary>
internal static class WordBuilderCases
{
    public const int SampleCount = 32_000;

    /// <summary>31 920 Samples sind 1,995 s: <c>AudioEnd</c> liegt neben dem 10-ms-Raster.</summary>
    public const int OffGridSampleCount = 31_920;

    public const float Probability = 0.9f;

    public static readonly TimeSpan Offset = TimeSpan.FromSeconds(30);

    public static readonly WordBuilderOptions Options = new(TimeSpan.FromMilliseconds(100));

    /// <summary>Sitzungszeit in Millisekunden.</summary>
    public static TimeSpan Ms(long milliseconds) => TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);

    public static WhisperTokenData Tok(string text, long start, long end, float probability = Probability) =>
        new(text, start, end, probability, false);

    public static WhisperTokenData Special(string text, long start, long end, float probability) =>
        new(text, start, end, probability, true);

    public static WhisperSegmentData Seg(string text, params WhisperTokenData[] tokens) => new(text, tokens);

    public static RecognizedWord Word(string text, long startMs, long endMs, float probability = Probability) =>
        new(text, Ms(startMs), Ms(endMs), probability);

    public static RecognizedWord WordTicks(string text, long startTicks, long endTicks, float probability = Probability) =>
        new(text, TimeSpan.FromTicks(startTicks), TimeSpan.FromTicks(endTicks), probability);

    public static RecognitionHypothesis Build(WordExample example) =>
        WhisperWordBuilder.Build(example.Segments, Offset, example.SampleCount, Options);

    /// <summary>Prüft das Produkt gegen die exakte Erwartung eines Beispiels.</summary>
    public static void AssertExample(WordExample example)
    {
        if (example.Words is null)
        {
            Assert.Throws<TokenAlignmentException>(() => Build(example));
            return;
        }

        var hypothesis = Build(example);
        Assert.Equal(Offset, hypothesis.AudioStart);
        Assert.Equal(example.AudioEnd, hypothesis.AudioEnd);
        Assert.Equal(example.Words, hypothesis.Words);
    }

    private static WordExample Example(string name, IReadOnlyList<RecognizedWord>? words, params WhisperSegmentData[] segments) =>
        new(name, segments, SampleCount, Ms(32_000), words);

    private static WordExample OffGrid(string name, IReadOnlyList<RecognizedWord>? words, params WhisperSegmentData[] segments) =>
        new(name, segments, OffGridSampleCount, Ms(31_995), words);

    // ---- Kapitel 7.1 ------------------------------------------------------------------------------------------

    public static WordExample R01 { get; } = Example(
        "R01",
        [Word("Hallo", 30_000, 30_400, 0.9f), Word("Welt.", 30_400, 30_820, 0.8f)],
        Seg(" Hallo Welt.", Tok(" Hallo", 0, 40, 0.9f), Tok(" Welt", 40, 80, 0.8f), Tok(".", 80, 82, 0.95f)));

    public static WordExample R02 { get; } = Example(
        "R02",
        [Word("Ja", 30_100, 30_300, 0.7f)],
        Seg(" Ja", Special("[_BEG_]", 0, 0, 0.1f), Tok(" Ja", 10, 30, 0.7f), Special("[_TT_30]", 30, 30, 0.05f)));

    public static WordExample R03 { get; } = Example(
        "R03",
        [Word("Rückmeldung", 31_000, 31_700, 0.6f)],
        Seg(" Rückmeldung", Tok(" Rück", 100, 140, 0.9f), Tok("mel", 130, 150, 0.6f), Tok("dung", 150, 170, 0.8f)));

    public static WordExample R04 { get; } = Example(
        "R04",
        [Word("Grüsse", 30_000, 30_500)],
        Seg(" Grüsse", Tok(" Gr", 0, 20), Tok("�", 20, 22), Tok("�sse", 22, 50)));

    /// <summary>Zwei Gruppen, drei Wörter im Segmenttext.</summary>
    public static WordExample R05a { get; } = Example(
        "R05a", null, Seg(" Hallo schöne Welt", Tok(" Hallo", 0, 40), Tok(" Welt", 40, 80)));

    /// <summary>Gruppentext ohne U+FFFD weicht vom Wort ab.</summary>
    public static WordExample R05b { get; } = Example("R05b", null, Seg(" Hallo", Tok(" Hello", 0, 40)));

    /// <summary>„zwei“ (Start &lt; 0) und „drei“ (End &lt; Start) teilen sich [30,4, 31) gleichmässig: je 3 000 000 Ticks.</summary>
    public static WordExample R06 { get; } = Example(
        "R06",
        [Word("eins", 30_000, 30_400), Word("zwei", 30_400, 30_700), Word("drei", 30_700, 31_000), Word("vier", 31_000, 31_400)],
        Seg(" eins zwei drei vier", Tok(" eins", 0, 40), Tok(" zwei", -1, -1), Tok(" drei", 60, 50), Tok(" vier", 100, 140)));

    /// <summary>„Ende“ bis 32,3 s auf 32 s begrenzt; „Schluss“ [32, 32) → W8 [31,99, 32); W9 kürzt „Ende“ auf 31,99 s.</summary>
    public static WordExample R07a { get; } = Example(
        "R07a",
        [Word("Ende", 31_900, 31_990), Word("Schluss", 31_990, 32_000)],
        Seg(" Ende Schluss", Tok(" Ende", 190, 230), Tok(" Schluss", 200, 210)));

    /// <summary><c>AudioEnd</c> 300 000 000 + 31 920 × 625 = 319 950 000 Ticks (31,995 s); W8 gibt [31,985, 31,995).</summary>
    public static WordExample R07b { get; } = OffGrid(
        "R07b",
        [Word("Ende", 31_900, 31_985), Word("Schluss", 31_985, 31_995)],
        Seg(" Ende Schluss", Tok(" Ende", 190, 230), Tok(" Schluss", 200, 210)));

    /// <summary>Entscheid 31.10.2026: tokenbelegtes Ende wird geteilt, je 2,5 ms ohne Überlappung.</summary>
    public static WordExample R07c { get; } = OffGrid(
        "R07c", [WordTicks("Ende", 319_900_000, 319_925_000), WordTicks("Schluss", 319_925_000, 319_950_000)], Seg(" Ende Schluss", Tok(" Ende", 199, 200), Tok(" Schluss", 200, 210)));

    /// <summary>Überlappung 0,05 s ≤ Toleranz, „so“ beginnt nach „gut“: W9 kürzt „gut“ auf 30,4 s.</summary>
    public static WordExample R08 { get; } = Example(
        "R08",
        [Word("gut", 30_000, 30_400), Word("so", 30_400, 30_800)],
        Seg(" gut so", Tok(" gut", 0, 45), Tok(" so", 40, 80)));

    /// <summary>
    /// <c>sampleCount</c> 160 000: <c>AudioEnd</c> 300 000 000 + 100 000 000 Ticks = 40 s. Die Wiederholung überlappt die
    /// erste Folge um 0,5 s (&gt; Toleranz) und bleibt stehen (Fixture P3 T62).
    /// </summary>
    public static WordExample R09 { get; } = new(
        "R09",
        [Seg(
            " a b c d e a b c d e",
            Tok(" a", 0, 100), Tok(" b", 100, 200), Tok(" c", 200, 300), Tok(" d", 300, 400), Tok(" e", 400, 500),
            Tok(" a", 450, 550), Tok(" b", 550, 650), Tok(" c", 650, 750), Tok(" d", 750, 850), Tok(" e", 850, 950))],
        160_000,
        Ms(40_000),
        [
            Word("a", 30_000, 31_000), Word("b", 31_000, 32_000), Word("c", 32_000, 33_000), Word("d", 33_000, 34_000),
            Word("e", 34_000, 35_000), Word("a", 34_500, 35_500), Word("b", 35_500, 36_500), Word("c", 36_500, 37_500),
            Word("d", 37_500, 38_500), Word("e", 38_500, 39_500),
        ]);

    /// <summary>W7 hebt „zwei“ auf 30,5 s; W9 greift nicht (Überlappung 0,3 s, gleicher Beginn).</summary>
    public static WordExample R10a { get; } = Example(
        "R10a",
        [Word("eins", 30_500, 30_800), Word("zwei", 30_500, 30_900)],
        Seg(" eins zwei", Tok(" eins", 50, 80), Tok(" zwei", 45, 90)));

    /// <summary>„zwei“ bis 32,1 s auf 31,995 s begrenzt, W7 hebt den Beginn auf 31,9 s; echte Überlappung bleibt.</summary>
    public static WordExample R10b { get; } = OffGrid(
        "R10b",
        [Word("eins", 31_900, 31_990), Word("zwei", 31_900, 31_995)],
        Seg(" eins zwei", Tok(" eins", 190, 199), Tok(" zwei", 185, 210)));

    public static WordExample R11a { get; } = Example(
        "R11a",
        [Word("eins", 30_000, 30_400, 0f), Word("zwei", 30_400, 30_800, 1f), Word("drei", 30_800, 31_200, 0f)],
        Seg(" eins zwei drei", Tok(" eins", 0, 40, float.NaN), Tok(" zwei", 40, 80, 1.5f), Tok(" drei", 80, 120, -0.2f)));

    public static WordExample R11b { get; } = Example(
        "R11b",
        [Word("Tor", 30_000, 30_400, 0.8f)],
        Seg(" Tor", Tok(" Tor", 0, 40, 0.8f), Special("[_TT_40]", 40, 40, 0.1f)));

    /// <summary>„ “ bildet eine leere Gruppe und entfällt samt Token; „Morgen“ beginnt als erstes Token sein Segment.</summary>
    public static WordExample R12a { get; } = Example(
        "R12a",
        [Word("Guten", 30_000, 30_300), Word("Tag", 30_300, 30_600), Word("Morgen", 30_700, 31_100)],
        Seg(" Guten Tag", Tok(" Guten", 0, 30), Tok(" ", 30, 30), Tok(" Tag", 30, 60)),
        Seg("Morgen", Tok("Morgen", 70, 110)),
        Seg("  "));

    public static WordExample R12b { get; } = Example("R12b", []);

    // ---- Gezielte Fälle zu W1–W10 -----------------------------------------------------------------------------

    /// <summary>Sondertokens am Anfang, mitten im Wort (mit Zeiten über das ganze Audio) und am Ende entfallen samt 0,01/0,02.</summary>
    public static WordExample W01 { get; } = Example(
        "W01",
        [Word("Tor", 30_100, 30_300, 0.7f)],
        Seg(
            " Tor",
            Special("[_BEG_]", 0, 0, 0.01f), Tok(" T", 10, 20, 0.9f), Special("[_TT_15]", 0, 300, 0.02f),
            Tok("or", 20, 30, 0.7f), Special("<|endoftext|>", 300, 300, 0.01f)));

    /// <summary>Ein Wort reicht nie über ein Segment hinaus, auch wenn das nächste Segment ohne Leerraum beginnt.</summary>
    public static WordExample W02 { get; } = Example(
        "W02",
        [Word("Haus", 30_000, 30_400), Word("tür", 30_400, 30_800)],
        Seg(" Haus", Tok(" Haus", 0, 40)),
        Seg("tür", Tok("tür", 40, 80)));

    /// <summary>Der Abgleich unterscheidet Gross- und Kleinschreibung (Gruppentext ohne U+FFFD).</summary>
    public static WordExample W03a { get; } = Example("W03a", null, Seg(" Hallo", Tok(" hallo", 0, 40)));

    /// <summary>U+FFFD erlaubt nur einen anderen Text, keine andere Wortzahl.</summary>
    public static WordExample W03b { get; } = Example(
        "W03b", null, Seg(" Käse Brot", Tok(" K�", 0, 20), Tok("�se", 20, 40)));

    /// <summary>
    /// (−5, 20) beginnt vor dem Audio und ist ungültig, wird also nicht auf <c>AudioStart</c> begrenzt: „eins“ erhält die
    /// Lücke [30, 30,5). „drei“ endet bei 32,5 s und wird auf 32 s begrenzt.
    /// </summary>
    public static WordExample W04a { get; } = Example(
        "W04a",
        [Word("eins", 30_000, 30_500), Word("zwei", 30_500, 30_800), Word("drei", 31_500, 32_000)],
        Seg(" eins zwei drei", Tok(" eins", -5, 20), Tok(" zwei", 50, 80), Tok(" drei", 150, 250)));

    /// <summary>Begrenzung auf ein <c>AudioEnd</c> neben dem Raster (31,995 s).</summary>
    public static WordExample W04b { get; } = OffGrid(
        "W04b",
        [Word("eins", 30_000, 30_400), Word("zwei", 31_500, 31_995)],
        Seg(" eins zwei", Tok(" eins", 0, 40), Tok(" zwei", 150, 250)));

    /// <summary>Der Beginn ist der kleinste Wert aller gültigen Tokens, nicht der des ersten: [30,9, 31,7).</summary>
    public static WordExample W05a { get; } = Example(
        "W05a",
        [Word("Rückmeldung", 30_900, 31_700)],
        Seg(" Rückmeldung", Tok(" Rück", 100, 140), Tok("mel", 90, 150), Tok("dung", 150, 170)));

    /// <summary>
    /// Drei Wörter ohne gültige Zeit in [30,1 s, 30,2 s) = [301 000 000, 302 000 000) Ticks, k = 3: Grenzen
    /// 301 000 000 + ⌊j × 1 000 000 / 3⌋, also 301 333 333 und 301 666 666; das letzte endet genau bei 302 000 000.
    /// </summary>
    public static WordExample W05b { get; } = Example(
        "W05b",
        [
            WordTicks("a", 300_000_000, 301_000_000), WordTicks("b", 301_000_000, 301_333_333),
            WordTicks("c", 301_333_333, 301_666_666), WordTicks("d", 301_666_666, 302_000_000),
            WordTicks("e", 302_000_000, 303_000_000),
        ],
        Seg(" a b c d e", Tok(" a", 0, 10), Tok(" b", -1, -1), Tok(" c", 20, 10), Tok(" d", -3, 15), Tok(" e", 20, 30)));

    /// <summary>Lücken am Anfang (ab <c>AudioStart</c>) und am Ende (bis <c>AudioEnd</c>).</summary>
    public static WordExample W05c { get; } = Example(
        "W05c",
        [Word("a", 30_000, 30_500), Word("b", 30_500, 30_600), Word("c", 30_600, 32_000)],
        Seg(" a b c", Tok(" a", -1, -1), Tok(" b", 50, 60), Tok(" c", -1, -1)));

    /// <summary>Auch ein Token mit ungültiger Zeit zählt für die Wahrscheinlichkeit; min(1,2; 1,1) wird auf 1 begrenzt.</summary>
    public static WordExample W06 { get; } = Example(
        "W06",
        [Word("Maus", 30_000, 30_200, 0.4f), Word("Dach", 30_300, 30_600, 1f)],
        Seg(" Maus Dach", Tok(" Ma", 0, 20, 0.8f), Tok("us", -1, -1, 0.4f), Tok(" Da", 30, 50, 1.2f), Tok("ch", 50, 60, 1.1f)));

    /// <summary>„c“ beginnt vor „b“ und wird auf dessen Beginn 30,6 s gehoben.</summary>
    public static WordExample W07a { get; } = Example(
        "W07a",
        [Word("a", 30_000, 30_500), Word("b", 30_600, 30_900), Word("c", 30_600, 31_200)],
        Seg(" a b c", Tok(" a", 0, 50), Tok(" b", 60, 90), Tok(" c", 55, 120)));

    /// <summary>Kette: „b“ wird auf 30,5 s gehoben, „c“ (30,3 s) auf den neuen Beginn von „b“, nicht auf 30,2 s.</summary>
    public static WordExample W07b { get; } = Example(
        "W07b",
        [Word("a", 30_500, 31_000), Word("b", 30_500, 30_600), Word("c", 30_500, 30_700)],
        Seg(" a b c", Tok(" a", 50, 100), Tok(" b", 20, 60), Tok(" c", 30, 70)));

    /// <summary>Ein Wort ohne Dauer mitten im Audio erhält 10 ms.</summary>
    public static WordExample W08a { get; } = Example(
        "W08a",
        [Word("a", 30_000, 30_100), Word("b", 30_500, 30_510), Word("c", 30_800, 31_000)],
        Seg(" a b c", Tok(" a", 0, 10), Tok(" b", 50, 50), Tok(" c", 80, 100)));

    /// <summary>
    /// 80 Samples: <c>AudioEnd</c> 300 000 000 + 50 000 Ticks (30,005 s). Das Wort liegt am Audioende; W8 setzt
    /// [max(U = 30 s, 29,995 s), 30,005 s), also nie vor <c>AudioStart</c>.
    /// </summary>
    public static WordExample W08b { get; } = new(
        "W08b", [Seg(" a", Tok(" a", 1, 2))], 80, Ms(30_005), [Word("a", 30_000, 30_005)]);

    /// <summary>Kein Audio: das Intervall [max(U, AudioEnd − 10 ms), AudioEnd) ist leer und auch W11 findet keinen Tick Platz: Ausnahme.</summary>
    public static WordExample W08c { get; } = new("W08c", [Seg(" a", Tok(" a", 0, 10))], 0, Ms(30_000), null);

    /// <summary>Entscheid 31.10.2026: freier Suffix zuerst; „a“ unverändert, „b“ [31,990;31,995).</summary>
    public static WordExample W08d { get; } = OffGrid(
        "W08d",
        [Word("a", 31_980, 31_990), Word("b", 31_990, 31_995)],
        Seg(" a b", Tok(" a", 198, 199), Tok(" b", 200, 205)));

    /// <summary>Erster Schritt von W8 endet am Audioende: [31,99, 31,995), kürzer als 10 ms.</summary>
    public static WordExample W08e { get; } = OffGrid(
        "W08e", [Word("a", 31_990, 31_995)], Seg(" a", Tok(" a", 199, 199)));

    /// <summary>Überlappung genau 0,1 s (= Toleranz): gekürzt.</summary>
    public static WordExample W09a { get; } = Example(
        "W09a",
        [Word("a", 30_000, 30_400), Word("b", 30_400, 30_800)],
        Seg(" a b", Tok(" a", 0, 50), Tok(" b", 40, 80)));

    /// <summary>Überlappung 0,11 s (&gt; Toleranz): bleibt stehen.</summary>
    public static WordExample W09b { get; } = Example(
        "W09b",
        [Word("a", 30_000, 30_510), Word("b", 30_400, 30_800)],
        Seg(" a b", Tok(" a", 0, 51), Tok(" b", 40, 80)));

    /// <summary>Kleine Überlappung, aber „b“ beginnt nicht nach „a“: bleibt stehen.</summary>
    public static WordExample W09c { get; } = Example(
        "W09c",
        [Word("a", 30_400, 30_450), Word("b", 30_400, 30_800)],
        Seg(" a b", Tok(" a", 40, 45), Tok(" b", 40, 80)));

    /// <summary>W9 je Nachbarpaar: zweimal gekürzt.</summary>
    public static WordExample W09d { get; } = Example(
        "W09d",
        [Word("a", 30_000, 30_400), Word("b", 30_400, 30_800), Word("c", 30_800, 31_200)],
        Seg(" a b c", Tok(" a", 0, 45), Tok(" b", 40, 85), Tok(" c", 80, 120)));

    /// <summary>„b“ ohne Dauer bei 30,5 s liegt in „a“; W8 legt es hinter „a“: [30,6, 30,61) (Kapitel 10.6).</summary>
    public static WordExample W09e { get; } = Example(
        "W09e",
        [Word("a", 30_400, 30_600), Word("b", 30_600, 30_610)],
        Seg(" a b", Tok(" a", 40, 60), Tok(" b", 50, 50)));

    /// <summary>Entscheid 30.10.2026: gemeinsamer Beginn wird geteilt; „a“ [30,5, 30,51), „b“ [30,51, 30,8).</summary>
    public static WordExample W10a { get; } = Example(
        "W10a", [Word("a", 30_500, 30_510), Word("b", 30_510, 30_800)], Seg(" a b", Tok(" a", 50, 50), Tok(" b", 50, 80)));

    /// <summary>„b“ ohne Dauer bei 30,45 s liegt in „a“ [30,3, 30,6); W8 legt es hinter „a“, ohne Überlappung (Kapitel 10.6).</summary>
    public static WordExample W10b { get; } = Example(
        "W10b",
        [Word("a", 30_300, 30_600), Word("b", 30_600, 30_610)],
        Seg(" a b", Tok(" a", 30, 60), Tok(" b", 45, 45)));

    /// <summary>Messbefund RM01: drei Wörter ohne Dauer auf demselben Zeitstempel erhalten je 10 ms hintereinander.</summary>
    public static WordExample W08f { get; } = Example(
        "W08f",
        [Word("a", 30_500, 30_510), Word("b", 30_510, 30_520), Word("c", 30_520, 30_530), Word("d", 30_600, 30_800)],
        Seg(" a b c d", Tok(" a", 50, 50), Tok(" b", 50, 50), Tok(" c", 50, 50), Tok(" d", 60, 80)));

    /// <summary>Zu wenig Platz bis „d“ (10 ms für drei): gleichmässig geteilt, auf ganze Ticks abgerundet.</summary>
    public static WordExample W08g { get; } = Example(
        "W08g",
        [
            new RecognizedWord("a", TimeSpan.FromTicks(305_000_000), TimeSpan.FromTicks(305_033_333), 0.9f),
            new RecognizedWord("b", TimeSpan.FromTicks(305_033_333), TimeSpan.FromTicks(305_066_666), 0.9f),
            new RecognizedWord("c", TimeSpan.FromTicks(305_066_666), TimeSpan.FromTicks(305_100_000), 0.9f),
            Word("d", 30_510, 30_800),
        ],
        Seg(" a b c d", Tok(" a", 50, 50), Tok(" b", 50, 50), Tok(" c", 50, 50), Tok(" d", 51, 80)));

    /// <summary>Alle Beispiele; die Nachrechnung der Matrix muss jedes exakt treffen.</summary>
    public static IReadOnlyList<WordExample> All { get; } =
    [
        R01, R02, R03, R04, R05a, R05b, R06, R07a, R07b, R07c, R08, R09, R10a, R10b, R11a, R11b, R12a, R12b,
        W01, W02, W03a, W03b, W04a, W04b, W05a, W05b, W05c, W06, W07a, W07b, W08a, W08b, W08c, W08d, W08e, W08f, W08g,
        W09a, W09b, W09c, W09d, W09e, W10a, W10b,
    ];
}
