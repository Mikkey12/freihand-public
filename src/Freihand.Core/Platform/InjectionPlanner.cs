using System.Globalization;
using Freihand.Core.Output;

namespace Freihand.Core.Platform;

/// <summary>Zerlegt den Text eines Abschnitts in Teile (P7-Konzept, Kapitel 5.6).</summary>
public static class InjectionPlanner
{
    private const int Enter = 0x0D;
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    /// <summary><see cref="ArgumentOutOfRangeException"/> bei <see cref="TargetRule.MaxPartLength"/> ≤ 0.</summary>
    public static InjectionPlan Plan(string text, TargetRule rule, ControlKeySafety safety)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rule.MaxPartLength);

        // 1. Steuerzeichen ausser LF könnten den Fokus verschieben oder senden
        if (text.Any(c => (c != '\n' && char.GetUnicodeCategory(c) == UnicodeCategory.Control) || c is LineSeparator or ParagraphSeparator))
        {
            return new PlanRefused(OutputReason.ControlCharacter);
        }

        // 2. Umbrüche je Regel; echte Tasten nur mit freigegebener Sicherung (Kapitel 5.8). Freigegeben ist nur Neutralize;
        // Check braucht den Entscheid zu SI-05 und eine Abfrage der acht Modifikatortasten und gilt bis dahin wie NotApproved
        var lineBreak = rule.LineBreak;
        if (lineBreak is LineBreakMode.Enter or LineBreakMode.ShiftEnter && safety != ControlKeySafety.Neutralize)
        {
            lineBreak = LineBreakMode.Refuse;
        }

        if (lineBreak == LineBreakMode.Refuse && text.Contains('\n', StringComparison.Ordinal))
        {
            return new PlanRefused(OutputReason.LineBreakRefused);
        }

        // 3. und 4. Teile an Satzende, LF, Leerzeichen oder Textelementgrenze; ein zu langes Textelement verweigert alles
        var boundaries = TextElementBoundaries(text);
        var parts = new List<InjectionPart>();
        var start = 0;
        while (start < text.Length)
        {
            var end = text.Length - start <= rule.MaxPartLength ? text.Length : PartEnd(text, start, rule.MaxPartLength, boundaries);
            if (end <= start)
            {
                return new PlanRefused(OutputReason.TextElementTooLong);
            }

            var partText = text[start..end];
            parts.Add(new InjectionPart(Steps(partText, lineBreak, safety), partText));
            start = end;
        }

        return new PlannedParts(parts);
    }

    /// <summary>Letzte erlaubte Schnittstelle in (start, start + max]; <paramref name="start"/>, wenn es keine gibt.</summary>
    private static int PartEnd(string text, int start, int max, HashSet<int> boundaries)
    {
        var limit = start + max;
        int? space = null;
        int? element = null;
        for (var end = limit; end > start; end--)
        {
            if (!boundaries.Contains(end))
            {
                continue;
            }

            element ??= end;
            var last = text[end - 1];
            if (last == '\n' || (last == ' ' && end - 2 >= start && text[end - 2] is '.' or '!' or '?' or '…'))
            {
                return end;
            }

            if (last == ' ')
            {
                space ??= end;
            }
        }

        return space ?? element ?? start;
    }

    /// <summary>Grenzen der Textelemente (<see cref="StringInfo"/>), samt Textende.</summary>
    private static HashSet<int> TextElementBoundaries(string text)
    {
        var boundaries = new HashSet<int>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            boundaries.Add(enumerator.ElementIndex);
        }

        boundaries.Add(text.Length);
        return boundaries;
    }

    private static List<InputStep> Steps(string part, LineBreakMode lineBreak, ControlKeySafety safety)
    {
        if (lineBreak is LineBreakMode.UnicodeReturn or LineBreakMode.Refuse)
        {
            return [new TypeText(part.Replace('\n', '\r'))];
        }

        var steps = new List<InputStep>();
        var lines = part.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                steps.Add(new PressKey(Enter, Shift: lineBreak == LineBreakMode.ShiftEnter, safety));
            }

            if (lines[i].Length > 0)
            {
                steps.Add(new TypeText(lines[i]));
            }
        }

        return steps;
    }
}
