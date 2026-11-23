using Freihand.Core.Routing;
using Freihand.Core.Segments;

namespace Freihand.Core.Recognition;

/// <summary>Ein Stück Audio, 16-Bit-PCM mono. Audio liegt nur im Arbeitsspeicher (SI-02).</summary>
public readonly record struct AudioChunk(ReadOnlyMemory<byte> Pcm16Mono, int SampleRate, TimeSpan Offset);

/// <summary>Fähigkeiten, die ein Erkennungsadapter meldet (WE-02).</summary>
public sealed record RecognitionCapabilities(
    bool PartialResults,
    bool FixedSegments,
    bool VocabularyHints,
    bool Cancellation,
    TimeSpan? MaxSessionDuration,
    string? DataRegion,
    string ModelVersion);

/// <summary>Einstellungen einer Erkennungssitzung.</summary>
public sealed record RecognitionOptions(string Language, IReadOnlyList<string> VocabularyHints);

/// <summary>
/// Zwischenstand der Erkennung. <see cref="Fixed"/> enthält nur die seit dem letzten Stand neu festen
/// Abschnitte; <see cref="Provisional"/> ersetzt den bisherigen vorläufigen Text vollständig (ER-02).
/// </summary>
/// <param name="CompleteUntil">
/// Zusage der Quelle: Alle festen Abschnitte, die vor dieser Sitzungszeit beginnen, sind mit diesem oder einem
/// früheren Stand geliefert. Sie sinkt nie; leer heisst keine neue Zusage (P3-Konzept, Kapitel 3).
/// </param>
public sealed record RecognitionUpdate(
    IReadOnlyList<TextSegment> Fixed,
    IReadOnlyList<TextSegment> Provisional,
    TimeSpan? CompleteUntil = null);

/// <summary>Einheitliche Schnittstelle für jede Erkennung, am Stück und live (WE-02).</summary>
public interface IRecognitionEngine
{
    RecognitionRoute Route { get; }

    RecognitionCapabilities Capabilities { get; }

    IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        RecognitionOptions options,
        CancellationToken cancellationToken = default);
}
