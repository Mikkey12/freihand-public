namespace Freihand.Core.Recognition;

/// <summary>Bereich erkannter Sprache in Sitzungszeit (P3-Konzept, Vertrag 3).</summary>
public sealed record SpeechRegion(TimeSpan Start, TimeSpan End);

/// <summary>
/// Sprachaktivitätserkennung: Silero VAD in P8, Attrappe in P3. Jeder Aufruf ist ohne Zustand; ein Bereich, der am
/// Audioende endet, gilt als offen. Eine Mindestdauer setzt nur P8 (Vertrag 3).
/// </summary>
public interface ISpeechDetector
{
    IReadOnlyList<SpeechRegion> Detect(ReadOnlyMemory<float> audio, TimeSpan offset);
}
