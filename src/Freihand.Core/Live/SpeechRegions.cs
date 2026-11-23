using Freihand.Core.Recognition;

namespace Freihand.Core.Live;

/// <summary>Rechnen mit Sprachbereichen (Vertrag 3): zusammenfassen, begrenzen, Abdeckung.</summary>
internal static class SpeechRegions
{
    /// <summary>
    /// Fasst überlappende, doppelte und aneinanderstossende Bereiche zusammen (T27) und begrenzt sie auf
    /// [<paramref name="from"/>, <paramref name="to"/>); leere Bereiche entfallen.
    /// </summary>
    public static List<SpeechRegion> Merge(IEnumerable<SpeechRegion> regions, TimeSpan from, TimeSpan to)
    {
        var clipped = regions
            .Where(region => region is not null)
            .Select(region => (Start: Max(region.Start, from), End: Min(region.End, to)))
            .Where(region => region.Start < region.End)
            .OrderBy(region => region.Start)
            .ToList();
        var merged = new List<SpeechRegion>();
        foreach (var (start, end) in clipped)
        {
            if (merged.Count > 0 && start <= merged[^1].End)
            {
                if (end > merged[^1].End)
                {
                    merged[^1] = merged[^1] with { End = end };
                }
            }
            else
            {
                merged.Add(new SpeechRegion(start, end));
            }
        }

        return merged;
    }

    /// <summary>Wie viel von [<paramref name="start"/>, <paramref name="end"/>) in den zusammengefassten Bereichen liegt.</summary>
    public static TimeSpan Overlap(IReadOnlyList<SpeechRegion> merged, TimeSpan start, TimeSpan end)
    {
        var total = TimeSpan.Zero;
        foreach (var region in merged)
        {
            if (region.Start >= end)
            {
                break;
            }

            var from = Max(region.Start, start);
            var to = Min(region.End, end);
            if (from < to)
            {
                total += to - from;
            }
        }

        return total;
    }

    /// <summary>
    /// Fasst Bereiche zusammen, zwischen denen weniger als <paramref name="pause"/> liegt (T77). Vor dem ersten und hinter
    /// dem letzten Bereich ändert sich nichts.
    /// </summary>
    public static List<SpeechRegion> BridgeShortGaps(IReadOnlyList<SpeechRegion> merged, TimeSpan pause)
    {
        var bridged = new List<SpeechRegion>(merged.Count);
        foreach (var region in merged)
        {
            if (bridged.Count > 0 && region.Start - bridged[^1].End < pause)
            {
                bridged[^1] = bridged[^1] with { End = region.End };
            }
            else
            {
                bridged.Add(region);
            }
        }

        return bridged;
    }

    /// <summary>Liegt mindestens die Hälfte der Dauer in Sprache (Kapitel 5.5, T05, T27)?</summary>
    public static bool HalfInSpeech(IReadOnlyList<SpeechRegion> merged, RecognizedWord word) =>
        Overlap(merged, word.Start, word.End).Ticks * 2 >= (word.End - word.Start).Ticks;

    /// <summary>Enthält der Bereich überhaupt Sprache?</summary>
    public static bool ContainsSpeech(IReadOnlyList<SpeechRegion> merged, TimeSpan start, TimeSpan end) =>
        Overlap(merged, start, end) > TimeSpan.Zero;

    public static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    public static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}

/// <summary>
/// Was über Sprache bekannt ist: gesicherte Bereiche vor dem Pufferbeginn und das jüngste Ergebnis von
/// <see cref="ISpeechDetector.Detect"/> über den Puffer. Bereiche vor dem Pufferbeginn ändern sich nicht mehr.
/// </summary>
internal sealed class SpeechMap
{
    private List<SpeechRegion> _settled = [];
    private List<SpeechRegion> _recent = [];
    private List<SpeechRegion>? _merged;

    /// <summary>Alle bekannten Bereiche, zusammengefasst und nach Beginn geordnet.</summary>
    public IReadOnlyList<SpeechRegion> Merged =>
        _merged ??= SpeechRegions.Merge(_settled.Concat(_recent), TimeSpan.Zero, TimeSpan.MaxValue);

    /// <summary>Ersetzt das Ergebnis über den Puffer [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public void SetRecent(IEnumerable<SpeechRegion> regions, TimeSpan from, TimeSpan to)
    {
        _recent = SpeechRegions.Merge(regions, from, to);
        _merged = null;
    }

    /// <summary>Der Pufferbeginn rückt vor: Bereiche davor gelten als gesichert.</summary>
    public void Settle(TimeSpan until)
    {
        var settled = _recent.Where(region => region.Start < until)
            .Select(region => region with { End = SpeechRegions.Min(region.End, until) });
        _settled = SpeechRegions.Merge(_settled.Concat(settled), TimeSpan.Zero, TimeSpan.MaxValue);
        _recent = [.. _recent.Where(region => region.End > until)
            .Select(region => region with { Start = SpeechRegions.Max(region.Start, until) })];
        _merged = null;
    }

    /// <summary>Vergisst gesicherte Bereiche, die vor <paramref name="before"/> enden.</summary>
    public void Prune(TimeSpan before)
    {
        if (_settled.Count > 0 && _settled[0].End <= before)
        {
            _settled.RemoveAll(region => region.End <= before);
            _merged = null;
        }
    }
}
