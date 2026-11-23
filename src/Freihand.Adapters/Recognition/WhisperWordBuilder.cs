using Freihand.Core.Recognition;

namespace Freihand.Adapters.Recognition;

/// <summary>Ein Token von whisper.cpp.</summary>
/// <param name="Text">Token-Text, kann U+FFFD für geteilte UTF-8-Bytes enthalten.</param>
/// <param name="Start">Beginn in Einheiten von 10 ms ab dem Anfang des übergebenen Audios (whisper.cpp).</param>
/// <param name="End">Ende in Einheiten von 10 ms.</param>
/// <param name="Probability">Wahrscheinlichkeit des Tokens.</param>
/// <param name="IsSpecial">Sonder- oder Zeitstempel-Token; setzt der Erkennungsprozess (Kapitel 5.5).</param>
public sealed record WhisperTokenData(string Text, long Start, long End, float Probability, bool IsSpecial);

/// <summary>Ein Segment von whisper.cpp mit seinen Tokens.</summary>
public sealed record WhisperSegmentData(string Text, IReadOnlyList<WhisperTokenData> Tokens);

/// <param name="NeighborOverlapTolerance">Kleine Überlappung, die W9 kürzt, Startwert 0,1 s.</param>
public sealed record WordBuilderOptions(TimeSpan NeighborOverlapTolerance)
{
    public static WordBuilderOptions Default { get; } = new(TimeSpan.FromMilliseconds(100));
}

/// <summary>Tokens und Segmenttext passen nicht zusammen, oder die Zeitfolge ist nicht reparierbar (W3, W8, W10).</summary>
public sealed class TokenAlignmentException : Exception
{
    public TokenAlignmentException()
        : base("Tokens und Wörter passen nicht zusammen.")
    {
    }
}

/// <summary>Wortbildung aus Tokens nach den Regeln W1–W10 (Kapitel 5.5 und 10.4), ohne Abhängigkeit auf Whisper.net.</summary>
public static class WhisperWordBuilder
{
    /// <summary>10 ms in Ticks, die Zeiteinheit von whisper.cpp.</summary>
    private const long Unit = 100_000;

    /// <summary>Ticks je Sample bei 16 kHz.</summary>
    private const long TicksPerSample = 625;

    /// <param name="segments">Segmente in der Reihenfolge von whisper.cpp.</param>
    /// <param name="offset">Sitzungszeit des ersten Samples (AudioStart).</param>
    /// <param name="sampleCount">Zahl der Samples bei 16 kHz; AudioEnd = offset + sampleCount × 625 Ticks.</param>
    /// <param name="options">Toleranzen.</param>
    /// <exception cref="TokenAlignmentException">W3, W8 oder W10 verletzt.</exception>
    public static RecognitionHypothesis Build(
        IReadOnlyList<WhisperSegmentData> segments, TimeSpan offset, int sampleCount, WordBuilderOptions options)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);

        var audioStart = offset.Ticks;
        var audioEnd = audioStart + (sampleCount * TicksPerSample);
        var (texts, tokens) = Group(segments);
        var n = texts.Count;
        var start = new long[n];
        var end = new long[n];
        var timed = new bool[n];

        // W4, W5: kleinster Beginn und grösstes Ende der gültigen, begrenzten Token-Zeiten
        for (var i = 0; i < n; i++)
        {
            foreach (var token in tokens[i])
            {
                if (token.Start < 0 || token.End < token.Start)
                {
                    continue;
                }

                var tokenStart = Clamp(token.Start, audioStart, audioEnd);
                var tokenEnd = Clamp(token.End, audioStart, audioEnd);
                start[i] = timed[i] ? Math.Min(start[i], tokenStart) : tokenStart;
                end[i] = timed[i] ? Math.Max(end[i], tokenEnd) : tokenEnd;
                timed[i] = true;
            }
        }

        var tokenStartSnapshot = start.ToArray();
        var tokenEndSnapshot = end.ToArray();
        ShareGaps(start, end, timed, audioStart, audioEnd);
        // Entscheid 31.10.2026: Zugehörigkeit ausschliesslich nach W4/W5, vor jeder W7/W8-Reparatur.
        var audioEndZero = Enumerable.Range(0, n).Select(index => start[index] == audioEnd && end[index] == audioEnd).ToArray();

        // W7 und W8 Wort für Wort: U ist der Start des Vorgängers nach dessen eigenem W8 (Kapitel 10.4)
        var estimated = new bool[n];
        for (var i = 0; i < n;)
        {
            var lower = i == 0 ? audioStart : start[i - 1];
            if (start[i] < lower)
            {
                start[i] = lower;
            }

            if (end[i] > start[i])
            {
                i++;
                continue;
            }

            // W8: eine Folge von Wörtern ohne Dauer hintereinander, ab dem Ende des Vorgängers, je 10 ms oder
            // gleichmässig im Platz bis zum nächsten Wort (Kapitel 10.6, Messbefund RM01)
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
                var spread = to - from < k * Unit;
                for (var m = 0; m < k; m++)
                {
                    estimated[i + m] = true;
                    start[i + m] = spread ? from + (m * (to - from) / k) : from + (m * Unit);
                    end[i + m] = spread ? (m == k - 1 ? to : from + ((m + 1) * (to - from) / k)) : from + ((m + 1) * Unit);
                }

                i = j;
                continue;
            }

            // W8/RM01: Nur Nullwörter am exakt gemeinsamen Beginn eines positiven Folgeworts teilen dessen
            // bereits belegten Bereich. Die normale Verteilung oben hat Vorrang; frühere echte Wörter bleiben frei.
            var sharedStart = start[i];
            var sameZeroStart = true;
            for (var m = i; m < j; m++)
            {
                sameZeroStart &= start[m] == sharedStart && end[m] == sharedStart;
            }

            if (j < n && sameZeroStart && start[j] == sharedStart && end[j] > sharedStart
                && end[j] <= audioEnd && (i == 0 || end[i - 1] <= sharedStart))
            {
                var duration = Math.Min(Unit, (end[j] - sharedStart) / (k + 1L));
                if (duration >= 1)
                {
                    for (var m = 0; m < k; m++)
                    {
                        estimated[i + m] = true;
                        start[i + m] = sharedStart + m * duration;
                        end[i + m] = sharedStart + (m + 1L) * duration;
                    }

                    start[j] = sharedStart + k * duration;
                    i = j;
                    continue;
                }
            }

            // Entscheid 31.10.2026: letzte Nullfolge zuerst in einen freien Suffix legen; nur ein unverändertes,
            // tokenbelegtes positives Vorgängerwort darf bei Bedarf selbst Dauer abgeben. W9/W10 bleiben aktiv.
            if (j == n && Enumerable.Range(i, k).All(index => audioEndZero[index])
                && (i == 0 || end[i - 1] > start[i - 1]))
            {
                var tailStart = Math.Max(i == 0 ? audioStart : end[i - 1], audioEnd - k * Unit);
                if (audioEnd - tailStart >= k)
                {
                    for (var m = 0; m < k; m++)
                    {
                        estimated[i + m] = true;
                        start[i + m] = tailStart + m * (audioEnd - tailStart) / k;
                        end[i + m] = m == k - 1 ? audioEnd : tailStart + (m + 1L) * (audioEnd - tailStart) / k;
                    }

                    i = j;
                    continue;
                }

                if (i > 0 && timed[i - 1] && !estimated[i - 1] && end[i - 1] == audioEnd
                    && start[i - 1] == tokenStartSnapshot[i - 1] && end[i - 1] == tokenEndSnapshot[i - 1])
                {
                    var duration = Math.Min(Unit, (audioEnd - start[i - 1]) / (k + 1L));
                    if (duration >= 1)
                    {
                        tailStart = audioEnd - k * duration;
                        end[i - 1] = tailStart;
                        estimated[i - 1] = true;
                        for (var m = 0; m < k; m++)
                        {
                            estimated[i + m] = true;
                            start[i + m] = tailStart + m * duration;
                            end[i + m] = tailStart + (m + 1L) * duration;
                        }

                        i = j;
                        continue;
                    }
                }
            }

            // Kein Platz, etwa am Audioende: dieses Wort wie bisher im zulässigen Intervall [U, AudioEnd]
            estimated[i] = true;
            end[i] = Math.Min(start[i] + Unit, audioEnd);
            if (end[i] <= start[i])
            {
                // Liegt das Wort genau am Audioende, ordnet W11 es unten ein
                start[i] = Math.Max(lower, audioEnd - Unit);
                end[i] = start[i] >= audioEnd ? start[i] : audioEnd;
            }

            i++;
        }

        // W9: kleine Überlappung mit dem Vorgänger kürzt dessen Ende
        var tolerance = options.NeighborOverlapTolerance.Ticks;
        for (var i = 1; i < n; i++)
        {
            if (start[i] < end[i - 1] && end[i - 1] - start[i] <= tolerance && start[i] > start[i - 1])
            {
                end[i - 1] = start[i];
            }
        }

        // W11 vor W10: Zeitfehler am Rand kippen nie die ganze Erkennung (Entscheid 05.11.2026); W10 gilt danach unverändert
        var repaired = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var broken = start[i] >= end[i] || start[i] < audioStart || end[i] > audioEnd
                || (i > 0 && start[i] < start[i - 1]);
            var ownTime = timed[i] && tokenEndSnapshot[i] > tokenStartSnapshot[i];
            var overlapsNeighbor = estimated[i] && !repaired[i] && !ownTime
                && ((i > 0 && end[i - 1] > start[i]) || (i + 1 < n && end[i] > start[i + 1]));
            if (broken || overlapsNeighbor)
            {
                if (repaired[i])
                {
                    throw new TokenAlignmentException();
                }

                i = Respace(start, end, repaired, i, audioEnd, tolerance) - 1;
            }
        }

        var words = new RecognizedWord[n];
        for (var i = 0; i < n; i++)
        {
            words[i] = new RecognizedWord(texts[i], TimeSpan.FromTicks(start[i]), TimeSpan.FromTicks(end[i]), Probability(tokens[i]));
        }

        return new RecognitionHypothesis(TimeSpan.FromTicks(audioStart), TimeSpan.FromTicks(audioEnd), words);

        long Clamp(long t, long low, long high) => Math.Clamp(audioStart + (t * Unit), low, high);
    }

    /// <summary>
    /// W11: Wörter ohne gültige Zeit ab <paramref name="first"/> teilen sich den Bereich bis zum nächsten Wort mit gültiger
    /// Zeit, sonst bis zum Audioende, je höchstens 10 ms; das Wort davor gibt dafür Dauer ab. Reicht das nicht, teilen sich
    /// auch frühere Wörter den Bereich gleichmässig. Liefert den Index des ersten geänderten Worts.
    /// </summary>
    /// <exception cref="TokenAlignmentException">Es gibt nicht einmal je Wort einen Tick Platz.</exception>
    private static int Respace(long[] start, long[] end, bool[] repaired, int first, long audioEnd, long tolerance)
    {
        var n = start.Length;
        var last = first;
        while (last + 1 < n && end[last + 1] <= start[last + 1])
        {
            last++;
        }

        var limit = last + 1 < n ? start[last + 1] : audioEnd;
        var count = last - first + 1;
        var donor = Math.Max(first - 1, 0);
        while (donor > 0 && limit - start[donor] < last - donor + 1)
        {
            donor--;
        }

        var from = start[donor];
        var words = last - donor + 1;
        if (limit - from < words)
        {
            throw new TokenAlignmentException();
        }

        var share = first > 0 && donor == first - 1 ? Math.Min(Unit, (limit - from) / (count + 1L)) : 0;
        if (share >= 1)
        {
            // Das Wort davor behält seinen Beginn, der Rand liegt je höchstens 10 ms am Schluss. Reicht es um mehr als die
            // Toleranz von W9 über die Grenze hinaus, überlappt es das nächste Wort echt und behält sein Ende
            var tail = limit - count * share;
            if (end[donor] - limit <= tolerance)
            {
                end[donor] = Math.Min(end[donor], tail);
            }

            for (var m = 0; m < count; m++)
            {
                start[first + m] = tail + m * share;
                end[first + m] = tail + (m + 1L) * share;
                repaired[first + m] = true;
            }

            return first;
        }

        for (var m = 0; m < words; m++)
        {
            var shared = m == words - 1 ? limit : from + (m + 1L) * (limit - from) / words;
            start[donor + m] = from + m * (limit - from) / words;
            end[donor + m] = end[donor + m] - limit > tolerance ? end[donor + m] : shared;
            repaired[donor + m] = true;
        }

        return donor;
    }

    /// <summary>W1–W3: Sondertokens weg, Gruppen je Segment, Abgleich mit dem Segmenttext.</summary>
    private static (List<string> Texts, List<List<WhisperTokenData>> Tokens) Group(IReadOnlyList<WhisperSegmentData> segments)
    {
        var texts = new List<string>();
        var tokens = new List<List<WhisperTokenData>>();
        foreach (var segment in segments)
        {
            var groups = new List<List<WhisperTokenData>>();
            foreach (var token in segment.Tokens)
            {
                if (token.IsSpecial)
                {
                    continue;
                }

                if (groups.Count == 0 || (token.Text.Length > 0 && char.IsWhiteSpace(token.Text[0])))
                {
                    groups.Add([]);
                }

                groups[^1].Add(token);
            }

            var kept = groups
                .Select(group => (Text: string.Concat(group.Select(token => token.Text)).Trim(), Tokens: group))
                .Where(group => group.Text.Length > 0)
                .ToList();

            var spoken = segment.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (spoken.Length != kept.Count)
            {
                throw new TokenAlignmentException();
            }

            for (var i = 0; i < kept.Count; i++)
            {
                if (!kept[i].Text.Contains('�', StringComparison.Ordinal)
                    && !string.Equals(kept[i].Text, spoken[i], StringComparison.Ordinal))
                {
                    throw new TokenAlignmentException();
                }

                texts.Add(spoken[i]);
                tokens.Add(kept[i].Tokens);
            }
        }

        return (texts, tokens);
    }

    /// <summary>W5: Wörter ohne gültige Zeit teilen die Lücke zwischen ihren Nachbarn gleichmässig; b &lt; a gilt als leer.</summary>
    private static void ShareGaps(long[] start, long[] end, bool[] timed, long audioStart, long audioEnd)
    {
        var n = start.Length;
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
            var b = Math.Max(j < n ? start[j] : audioEnd, a);
            for (var m = 0; m < k; m++)
            {
                start[i + m] = a + (m * (b - a) / k);
                end[i + m] = m == k - 1 ? b : a + ((m + 1) * (b - a) / k);
            }

            i = j;
        }
    }

    /// <summary>W6: kleinste Wahrscheinlichkeit der Tokens des Worts, NaN als 0, begrenzt auf [0, 1].</summary>
    private static float Probability(List<WhisperTokenData> tokens)
    {
        var lowest = tokens.Min(token => float.IsNaN(token.Probability) ? 0f : token.Probability);
        return Math.Clamp(lowest, 0f, 1f);
    }
}
