namespace Freihand.Core.Platform;

/// <summary>Ereignisplan eines Teils und Freigabe nach Teilerfolg (P7-Konzept, Kapitel 5.7); rein, plattformunabhängig.</summary>
public static class InputEventPlanner
{
    private const int Enter = 0x0D;
    private const int Shift = 0x10;

    /// <summary>Linke und rechte Strg-, Alt-, Windows- und Umschalttaste (Kapitel 5.8, Neutralize).</summary>
    private static readonly int[] Modifiers = [0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C, 0xA0, 0xA1];

    public static IReadOnlyList<InputEvent> Events(InjectionPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var events = new List<InputEvent>();
        var unit = 0;
        foreach (var step in part.Steps)
        {
            switch (step)
            {
                case TypeText text:
                    foreach (var c in text.Text)
                    {
                        events.Add(new InputEvent(InputEventKind.Unicode, c, Down: true, unit));
                        events.Add(new InputEvent(InputEventKind.Unicode, c, Down: false, unit));
                        unit++;
                    }

                    break;

                case PressKey key:
                    // Ohne freigegebene Sicherung keine echte Taste, auch nicht auf direktem Weg (Kapitel 5.8)
                    if (key.Safety != ControlKeySafety.Neutralize)
                    {
                        throw new ArgumentException($"Echte Taste ohne freigegebene Sicherung ({key.Safety}).", nameof(part));
                    }

                    events.AddRange(Modifiers.Select(code => new InputEvent(InputEventKind.VirtualKey, code, Down: false, Unit: null)));

                    if (key.Shift)
                    {
                        events.Add(new InputEvent(InputEventKind.VirtualKey, Shift, Down: true, unit));
                    }

                    events.Add(new InputEvent(InputEventKind.VirtualKey, key.VirtualKey, Down: true, unit));
                    events.Add(new InputEvent(InputEventKind.VirtualKey, key.VirtualKey, Down: false, unit));
                    if (key.Shift)
                    {
                        events.Add(new InputEvent(InputEventKind.VirtualKey, Shift, Down: false, unit));
                    }

                    unit++;
                    break;

                default:
                    throw new ArgumentException($"Unbekannter Schritt {step.GetType().Name}.", nameof(part));
            }
        }

        return events;
    }

    /// <summary>
    /// Auf-Ereignisse für jede Taste, deren Ab-Ereignis unter den ersten <paramref name="inserted"/> Ereignissen liegt und
    /// deren Auf-Ereignis nicht, in umgekehrter Reihenfolge des Drückens; auch für Modifikatoren.
    /// </summary>
    public static IReadOnlyList<InputEvent> ReleaseAfter(IReadOnlyList<InputEvent> events, int inserted)
    {
        ArgumentNullException.ThrowIfNull(events);
        var pressed = new List<InputEvent>();
        foreach (var item in events.Take(Math.Clamp(inserted, 0, events.Count)))
        {
            if (item.Down)
            {
                pressed.Add(item);
            }
            else if (pressed.FindLastIndex(down => down.Kind == item.Kind && down.Code == item.Code) is var index and >= 0)
            {
                pressed.RemoveAt(index);
            }
        }

        return [.. Enumerable.Reverse(pressed).Select(down => down with { Down = false })];
    }

    /// <summary>Texteinheiten von vorn, deren tragendes Ab-Ereignis (Unicode-Ab bzw. Enter-Ab) unter den ersten <paramref name="inserted"/> liegt.</summary>
    public static int UnitsInserted(IReadOnlyList<InputEvent> events, int inserted)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events.Take(Math.Clamp(inserted, 0, events.Count)).Count(item =>
            item.Down && item.Unit is not null && (item.Kind == InputEventKind.Unicode || item.Code == Enter));
    }
}
