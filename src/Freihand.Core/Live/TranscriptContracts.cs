using Freihand.Core.Recognition;

namespace Freihand.Core.Live;

/// <summary>Wirksamer Erkennungsmodus, gilt für das ganze Diktat (ER-19, ER-20).</summary>
public enum LiveMode
{
    Live,
    Sentence,
}

/// <summary>Einstellungen der Erkennung (P3-Konzept, Vertrag 4); ungültige Werte weist der Konstruktor der Quelle ab.</summary>
/// <param name="Mode">Wirksamer Modus (ER-19, ER-20).</param>
/// <param name="Step">Nur live: Takt der Neuberechnung in Audiozeit, Startwert 0,5 s.</param>
/// <param name="Window">Nur live: höchstens so viel Audio je Decode, Startwert 15 s; live mindestens <c>MaxUtterance</c> (E1).</param>
/// <param name="PauseThreshold">Stille, die eine Äusserung beendet, Startwert 0,6 s (ER-15).</param>
/// <param name="MaxUtterance">Spätestens hier wird eine Äusserung geschnitten, Startwert 15 s (ER-01, Entscheid 03.11.2026).</param>
/// <param name="CutGuard">Ein Wort, das so nah vor einem Schnitt endet, kommt in den nächsten Teil, Startwert 0,3 s.</param>
/// <param name="BacklogWarning">Warnung ab diesem Verarbeitungsrückstand, Startwert 10 s (ER-05, E2).</param>
/// <param name="BacklogLimit">Puffer, danach keine Annahme mehr, Startwert 120 s (ER-05).</param>
/// <param name="Filter">Schutz vor erfundenem Text (ER-16).</param>
public sealed record LiveOptions(
    LiveMode Mode,
    TimeSpan Step,
    TimeSpan Window,
    TimeSpan PauseThreshold,
    TimeSpan MaxUtterance,
    TimeSpan CutGuard,
    TimeSpan BacklogWarning,
    TimeSpan BacklogLimit,
    HallucinationFilter Filter)
{
    /// <summary>
    /// Ton vor dem erkannten Sprachbeginn, den eine Äusserung mitnimmt, höchstens bis zur letzten Grenze (P11 S3,
    /// Auswertung S2 vom 18.11.2026: Silero setzt bei leisen Wortanfängen spät ein, „Zu zwei“ wurde „2.“). An einem
    /// Längenschnitt ist die letzte Grenze der Schnitt selbst, dort gibt es keinen Vorlauf. Standard 0 (wie bisher).
    /// </summary>
    public TimeSpan Lead { get; init; } = TimeSpan.Zero;
}

/// <summary>Floskelfilter (P3-Konzept, Kapitel 5.5).</summary>
/// <param name="KnownPhrases">Bekannte Floskeln als Daten, z. B. Dankesformeln, Untertitelhinweise.</param>
/// <param name="MaxPhraseProbability">Startwert 0,5: darunter entfällt eine Floskel, die die ganze Äusserung bildet.</param>
public sealed record HallucinationFilter(IReadOnlyList<string> KnownPhrases, float MaxPhraseProbability);

/// <summary>Warum eine Äusserung endete.</summary>
public enum UtteranceEnd
{
    Pause,
    LengthCut,
    StreamEnd,
    AudioGap,
    CommandKey,
    DecoderFailed,

    /// <summary>
    /// Abgeschlossen, weil Detect warf (P3-Konzept, Kapitel 5.4, Detektorfehler): mit den schon übernommenen Wörtern bzw.
    /// festen Deltas; bei <c>LiveTranscription</c> sind das seit Entscheid E1 keine.
    /// </summary>
    DetectorFailed,
}

/// <summary>Eine abgeschlossene Äusserung; ihre Kennung ist auch die ihres vorläufigen und festen Abschnitts.</summary>
public sealed record Utterance(Guid Id, IReadOnlyList<RecognizedWord> Words, TimeSpan Start, TimeSpan End, UtteranceEnd Ending);

/// <summary>Ereignis einer Quelle an die Sitzung.</summary>
public abstract record TranscriptEvent;

/// <summary>Ersetzt den vorläufigen Rohtext der offenen Äusserung ganz.</summary>
public sealed record ProvisionalText(Guid UtteranceId, string Text) : TranscriptEvent;

public sealed record UtteranceCompleted(Utterance Utterance) : TranscriptEvent;

/// <summary>Sichtbare Meldung mit Zeitspanne in Sitzungszeit.</summary>
public sealed record LiveNotice(LiveNoticeKind Kind, TimeSpan Start, TimeSpan End) : TranscriptEvent;

public enum LiveNoticeKind
{
    BacklogWarning,
    BacklogCleared,
    BufferFull,
    AudioGap,
    SpeechWithoutText,

    /// <summary>
    /// Längenschnitt mit übernommenem Text: Am Schnitt können Wörter fehlen oder doppelt sein, die Stelle ist zu prüfen.
    /// Spanne vom Kürzungspunkt bis zur Grenze des Auftrags (Regel 5 der unsicheren Ränder, T78). Der Text bleibt unverändert.
    /// </summary>
    LengthCutUncertain,
    DecoderFailed,

    /// <summary>
    /// <see cref="ISpeechDetector.Detect"/> warf (P8 bei Ausfall oder Zeitüberschreitung): Verlust nach den schon
    /// übernommenen Wörtern bzw. festen Deltas, bei <c>LiveTranscription</c> ab Beginn der offenen Äusserung, bis
    /// <see cref="ITranscriptSource.AcceptedEnd"/>, danach kein Text mehr (P3-Konzept, Kapitel 5.4).
    /// </summary>
    DetectorFailed,
}

/// <summary>Eingang der Sitzung, je Diktat eine Instanz (P3-Konzept, Vertrag 4).</summary>
public interface ITranscriptSource
{
    IAsyncEnumerable<TranscriptEvent> RunAsync(IAsyncEnumerable<AudioChunk> audio, CancellationToken cancellationToken = default);

    /// <summary>Befehlstaste: Äusserungsgrenze an dieser Sitzungszeit.</summary>
    void MarkBoundary(TimeSpan at);

    /// <summary>Ende des bisher angenommenen Audios.</summary>
    TimeSpan AcceptedEnd { get; }

    /// <summary>Angenommenes, noch nicht abschliessend verarbeitetes Audio (ER-05).</summary>
    TimeSpan Backlog { get; }
}
