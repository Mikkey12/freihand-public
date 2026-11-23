namespace Freihand.Core.Quality;

/// <summary>Ob der Text vor oder nach der Umformung gemessen wurde; Rohtext wird separat gemessen (QM-02).</summary>
public enum TextStage
{
    Raw,
    Transformed,
}

/// <summary>Wozu die Messung gehört: pro Anbieter, Modell und Gerät (QM-02, QM-08). Nur Metadaten.</summary>
/// <param name="Provider">Anbieter bzw. Weg, z. B. „whisper.cpp“, „Windows-Sprachzugriff“ (QM-06).</param>
/// <param name="Model">Modell samt Version, wie vom Adapter gemeldet (WE-02).</param>
/// <param name="Device">Gerät, z. B. „PC CUDA-Grafikkarte“, „Notebook-Profil“, „Referenznotebook Akku“ (QM-08).</param>
/// <param name="Language">Sprache bzw. Profil des Referenzsatzes, z. B. „de-CH“.</param>
/// <param name="Stage">Rohtext oder umgeformter Text.</param>
/// <param name="ReferenceSet">Kennung und Fassung des Referenzsatzes (QM-01).</param>
/// <param name="MeasuredAt">Zeitpunkt der Messung.</param>
/// <param name="Notes">Freie Angaben ohne Inhalte, z. B. „Office- und Teams-Last“.</param>
public sealed record MeasurementInfo(
    string Provider,
    string Model,
    string Device,
    string Language,
    TextStage Stage,
    string? ReferenceSet = null,
    DateTimeOffset? MeasuredAt = null,
    string? Notes = null);

/// <summary>Ein Testsatz: Referenz aus dem Referenzsatz, erkannter Text und markierte Begriffe.</summary>
public sealed record ReferenceSentence(
    string Id,
    string Reference,
    string Hypothesis,
    IReadOnlyList<ExpectedTerm> Terms);

/// <summary>Einstellungen der Auswertung.</summary>
/// <param name="PauseThreshold">Eingestellte Pausenschwelle für die Latenz „fest“ (ER-01).</param>
public sealed record QualitySettings(TimeSpan PauseThreshold)
{
    public TimeSpan Window { get; init; } = TimeWindows.DefaultWindow;

    public TimeSpan Step { get; init; } = TimeWindows.DefaultStep;

    public TimeSpan MaxUtterance { get; init; } = Latency.DefaultMaxUtterance;
}

/// <summary>Eingabe einer Auswertung: nur Metadaten und Texte aus Testsätzen, nie Audio (QM-02, SI-02).</summary>
public sealed record QualityRun(MeasurementInfo Measurement, TimeSpan AudioDuration, QualitySettings Settings)
{
    public IReadOnlyList<ComputeSample> Compute { get; init; } = [];

    public IReadOnlyList<UtteranceTiming> Utterances { get; init; } = [];

    public IReadOnlyList<ReferenceSentence> Sentences { get; init; } = [];

    public IReadOnlyList<CommandObservation> Commands { get; init; } = [];
}

/// <summary>Echtzeitfaktor gesamt und über gleitende Fenster; <c>null</c> ohne Rechenzeiten.</summary>
public sealed record RealTimeFactorReport(
    double? Overall,
    double? WindowP95,
    double? WindowMax,
    IReadOnlyList<WindowedRealTimeFactor> Windows);

/// <summary>
/// Latenzen gesamt und je Fenster. <c>…WindowP95Max</c> ist das höchste 95. Perzentil aller Fenster,
/// also der schlechteste 10-Minuten-Abschnitt.
/// </summary>
public sealed record LatencyReport(
    LatencySummary FirstPartial,
    LatencySummary Fixed,
    TimeSpan? FirstPartialWindowP95Max,
    TimeSpan? FixedWindowP95Max,
    IReadOnlyList<WindowedLatency> FirstPartialWindows,
    IReadOnlyList<WindowedLatency> FixedWindows);

/// <summary>Wortfehler eines Testsatzes, nur mit Kennung.</summary>
public sealed record SentenceWordErrors(string Id, WordErrorCounts Counts);

/// <summary>Wortfehlerrate über alle Testsätze (summiert) und je Satz.</summary>
public sealed record WordErrorReport(WordErrorCounts Total, IReadOnlyList<SentenceWordErrors> Sentences);

/// <summary>
/// Messergebnis (QM-02). Enthält nur Kennungen, Zahlen und Metadaten; die Texte der Testsätze bleiben in
/// der Eingabe und erscheinen nicht im Bericht.
/// </summary>
public sealed record QualityReport(
    string Schema,
    MeasurementInfo Measurement,
    TimeSpan AudioDuration,
    QualitySettings Settings,
    RealTimeFactorReport RealTimeFactor,
    LatencyReport Latency,
    WordErrorReport WordErrors,
    IReadOnlyList<CategoryErrors> Terms,
    DictationMarkErrors DictationMarks,
    CommandErrors Commands)
{
    /// <summary>Kennung des JSON-Formats; ändert sich bei inkompatiblen Änderungen.</summary>
    public const string CurrentSchema = "freihand.quality/1";
}
