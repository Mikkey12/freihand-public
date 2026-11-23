namespace Freihand.Adapters.Dictation;

/// <summary>
/// Pegel für die Pille: zeigt nur, dass Ton ankommt. Effektivwert eines
/// Blocks PCM 16 bit mono, von −50 dBFS (leer) bis 0 dBFS (voll) auf 0 bis 1 abgebildet. Kein Audio wird behalten.
/// </summary>
public static class AudioLevel
{
    private const double Floor = -50;

    public static float Of(ReadOnlySpan<byte> pcm16)
    {
        var samples = pcm16.Length / 2;
        if (samples == 0)
        {
            return 0;
        }

        double sum = 0;
        for (var index = 0; index < samples; index++)
        {
            double sample = (short)(pcm16[2 * index] | (pcm16[(2 * index) + 1] << 8));
            sum += sample * sample;
        }

        var rms = Math.Sqrt(sum / samples) / 32768.0;
        if (rms <= 0)
        {
            return 0;
        }

        var decibels = 20 * Math.Log10(rms);
        return (float)Math.Clamp((decibels - Floor) / -Floor, 0, 1);
    }
}
