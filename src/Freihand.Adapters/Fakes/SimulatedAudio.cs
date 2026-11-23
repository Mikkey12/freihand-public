using System.Runtime.CompilerServices;
using Freihand.Core.Recognition;

namespace Freihand.Adapters.Fakes;

/// <summary>
/// Erfundenes Audio für Tests: nur Stille, keine Aufnahme (SI-02). Liegt nur im Arbeitsspeicher.
/// </summary>
public static class SimulatedAudio
{
    public const int DefaultSampleRate = 16_000;

    /// <summary>Ein Stück Stille von <paramref name="duration"/> ab <paramref name="offset"/>.</summary>
    public static AudioChunk Silence(TimeSpan duration, TimeSpan offset, int sampleRate = DefaultSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var samples = (int)Math.Round(duration.TotalSeconds * sampleRate);
        return new AudioChunk(new byte[samples * 2], sampleRate, offset);
    }

    /// <summary>Dauer eines Stücks aus Länge und Abtastrate (16-Bit-PCM mono).</summary>
    public static TimeSpan DurationOf(AudioChunk chunk) =>
        chunk.SampleRate > 0
            ? TimeSpan.FromTicks((long)Math.Round(chunk.Pcm16Mono.Length / 2.0 * TimeSpan.TicksPerSecond / chunk.SampleRate))
            : TimeSpan.Zero;

    /// <summary>
    /// Live-Quelle: liefert jedes Stück erst, wenn es auf der Uhr fertig gesprochen ist, also am Ende
    /// seiner Audiozeit. Ist die Verarbeitung im Rückstand, kommt das Stück sofort (ER-05).
    /// </summary>
    public static async IAsyncEnumerable<AudioChunk> LiveAsync(
        ISimulationClock clock,
        int count,
        TimeSpan chunkDuration,
        int sampleRate = DefaultSampleRate,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);

        for (var i = 0; i < count; i++)
        {
            var offset = chunkDuration * i;
            await clock.WaitUntilAsync(offset + chunkDuration, cancellationToken).ConfigureAwait(false);
            yield return Silence(chunkDuration, offset, sampleRate);
        }
    }
}
