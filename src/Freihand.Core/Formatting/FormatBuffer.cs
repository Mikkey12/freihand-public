using System.Text;
using System.Text.RegularExpressions;

namespace Freihand.Core.Formatting;

/// <summary>
/// Text während der Nachbearbeitung samt gesperrten Bereichen. Eine Regel ändert nur, was ausserhalb gesperrter
/// Bereiche liegt; so bleiben geschützte Wörter unverändert (BA-17) und fertige Daten, Uhrzeiten und Beträge
/// werden von späteren Regeln nicht nochmals umgeschrieben.
/// </summary>
internal sealed class FormatBuffer(string text)
{
    // Sortiert und ohne Überlappung
    private List<Range> _locked = [];

    public string Text { get; private set; } = text;

    /// <summary>
    /// Sperrt jeden Treffer, ohne den Text zu ändern. Überlappt ein Treffer eine vorhandene Sperre, werden beide vereinigt;
    /// sonst verlöre z. B. eine Adresse ihren Schutz, sobald eine gelernte Schreibweise einen Teil davon sperrt (Codex zu
    /// PR #66).
    /// </summary>
    public void Lock(Regex regex) =>
        LockRanges([.. regex.Matches(Text).Where(match => match.Length > 0).Select(match => (match.Index, match.Length))]);

    /// <summary>
    /// Ersetzt jeden Treffer, der keinen gesperrten Bereich berührt. Gibt <paramref name="replace"/> null zurück,
    /// bleibt der Treffer unverändert und ungesperrt.
    /// </summary>
    public void Apply(Regex regex, Func<Match, string?> replace, bool lockResult)
    {
        var builder = new StringBuilder(Text.Length);
        var locked = new List<Range>(_locked.Count);
        var lockIndex = 0;
        var position = 0;
        var changed = false;

        foreach (Match match in regex.Matches(Text))
        {
            if (match.Length == 0 || Overlaps(match.Index, match.Length))
            {
                continue;
            }

            var replacement = replace(match);
            if (replacement is null)
            {
                continue;
            }

            // Gesperrte Bereiche vor dem Treffer verschieben sich um die bisherige Längenänderung
            var shift = builder.Length - position;
            while (lockIndex < _locked.Count && _locked[lockIndex].Start < match.Index)
            {
                locked.Add(_locked[lockIndex] with { Start = _locked[lockIndex].Start + shift });
                lockIndex++;
            }

            builder.Append(Text, position, match.Index - position);
            if (lockResult && replacement.Length > 0)
            {
                locked.Add(new Range(builder.Length, replacement.Length));
            }

            builder.Append(replacement);
            position = match.Index + match.Length;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        var tailShift = builder.Length - position;
        for (; lockIndex < _locked.Count; lockIndex++)
        {
            locked.Add(_locked[lockIndex] with { Start = _locked[lockIndex].Start + tailShift });
        }

        builder.Append(Text, position, Text.Length - position);
        Text = builder.ToString();
        _locked = locked;
    }

    /// <summary>
    /// Sperrt Bereiche nach ihrer Lage (gelernte Schreibweisen, P11 S4); überlappende und angrenzende Sperren werden
    /// vereinigt. Vor allen anderen Regeln aufrufen.
    /// </summary>
    public void LockRanges(IEnumerable<(int Start, int Length)> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var all = _locked.Select(range => (range.Start, range.Length)).Concat(ranges).Where(range => range.Length > 0).ToList();
        if (all.Any(range => range.Start < 0 || range.Start + range.Length > Text.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(ranges), "Bereich ausserhalb des Texts.");
        }

        var merged = new List<Range>();
        foreach (var (start, length) in all.OrderBy(range => range.Start))
        {
            if (merged.Count > 0 && start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = last with { Length = Math.Max(last.Start + last.Length, start + length) - last.Start };
            }
            else
            {
                merged.Add(new Range(start, length));
            }
        }

        _locked = merged;
    }

    private bool Overlaps(int start, int length)
    {
        var end = start + length;
        foreach (var range in _locked)
        {
            if (range.Start >= end)
            {
                return false;
            }

            if (range.Start + range.Length > start)
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct Range(int Start, int Length);
}
