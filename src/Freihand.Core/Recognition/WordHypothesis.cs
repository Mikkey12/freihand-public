namespace Freihand.Core.Recognition;

/// <summary>Ein erkanntes Wort mit Sitzungszeiten (P3-Konzept, Vertrag 2).</summary>
/// <param name="Text">Wortlaut, nicht leer und ohne Leerraum am Rand.</param>
/// <param name="Start">Beginn in Sitzungszeit.</param>
/// <param name="End">Ende in Sitzungszeit, nach <paramref name="Start"/>.</param>
/// <param name="Probability">Kleinste Wahrscheinlichkeit der Tokens des Worts, 0 bis 1.</param>
public sealed record RecognizedWord(string Text, TimeSpan Start, TimeSpan End, float Probability);

/// <summary>
/// Ergebnis eines Decodes über das Audio [<paramref name="AudioStart"/>, <paramref name="AudioEnd"/>). P3 prüft
/// Grundregeln, entfernt zeitliche Wiederholungen und verlangt danach Wörter ohne Überlappung (Vertrag 2).
/// </summary>
public sealed record RecognitionHypothesis(TimeSpan AudioStart, TimeSpan AudioEnd, IReadOnlyList<RecognizedWord> Words);

/// <summary>Decoder mit Wortzeiten: whisper.cpp in P8, Attrappe in P3. Liefert Sitzungszeiten (Vertrag 1).</summary>
public interface IHypothesisDecoder
{
    /// <param name="audio">16 kHz mono, Bereich [−1, 1); gültig und unverändert bis zum Ende des Aufrufs.</param>
    /// <param name="offset">Sitzungszeit des ersten Samples.</param>
    Task<RecognitionHypothesis> DecodeAsync(
        ReadOnlyMemory<float> audio, TimeSpan offset, RecognitionOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Die Hypothese verstösst gegen die Gültigkeitsregeln aus Vertrag 2; P3 behandelt das wie einen Decoderfehler.</summary>
public sealed class InvalidHypothesisException : Exception
{
    public InvalidHypothesisException()
    {
    }

    public InvalidHypothesisException(string message)
        : base(message)
    {
    }

    public InvalidHypothesisException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
