using System.Runtime.CompilerServices;
using Freihand.Core.Recognition;
using Freihand.Core.Routing;

namespace Freihand.Adapters.Fakes;

/// <summary>
/// Attrappe für Tests ohne Mikrofon und Modell: liefert festgelegte Zwischenstände, je einen pro
/// empfangenem Audiostück. Grundlage für die Pakete P3 und P10 in Cloud-Sitzungen.
/// </summary>
public sealed class ScriptedRecognitionEngine(IEnumerable<RecognitionUpdate> script) : IRecognitionEngine
{
    private readonly IReadOnlyList<RecognitionUpdate> _script = [.. script];

    public RecognitionRoute Route => RecognitionRoute.Device;

    public RecognitionCapabilities Capabilities { get; } = new(
        PartialResults: true,
        FixedSegments: true,
        VocabularyHints: false,
        Cancellation: true,
        MaxSessionDuration: null,
        DataRegion: null,
        ModelVersion: "scripted");

    public async IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        RecognitionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = 0;
        await foreach (var _ in audio.WithCancellation(cancellationToken))
        {
            // Abbrechen wirkt auch, wenn die Audioquelle den Token nicht beachtet (BE-06)
            cancellationToken.ThrowIfCancellationRequested();

            if (index >= _script.Count)
            {
                yield break;
            }

            yield return _script[index++];
        }
    }
}
