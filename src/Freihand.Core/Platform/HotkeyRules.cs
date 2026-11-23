namespace Freihand.Core.Platform;

/// <summary>Prüfregeln 1–5 für Tastenkombinationen ohne Windows (P7-Konzept, Kapitel 5.1).</summary>
public static class HotkeyRules
{
    /// <summary>Prüft in der Reihenfolge des Konzepts; der erste Treffer gilt. Ruft keine Windows-API auf.</summary>
    public static HotkeyConflict Check(
        HotkeyAction action,
        HotkeyGesture gesture,
        IReadOnlyDictionary<HotkeyAction, HotkeyGesture> registered,
        IKeyboardLayoutInfo layout)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(layout);
        var key = gesture.VirtualKey;
        var modifiers = gesture.Modifiers;
        if (IsInvalid(key))
        {
            return HotkeyConflict.Invalid;
        }

        if (key == 0x7B)
        {
            return HotkeyConflict.Reserved;
        }

        if ((modifiers == HotkeyModifiers.None || modifiers == HotkeyModifiers.Shift) && IsTypingKey(key))
        {
            return HotkeyConflict.TypingKey;
        }

        if (registered.Any(entry => entry.Key != action && entry.Value == gesture))
        {
            return HotkeyConflict.OwnDuplicate;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Control) && modifiers.HasFlag(HotkeyModifiers.Alt) &&
            !modifiers.HasFlag(HotkeyModifiers.Windows) &&
            layout.AltGrCharacter(key, modifiers.HasFlag(HotkeyModifiers.Shift)) is not null)
        {
            return HotkeyConflict.AltGrCharacter;
        }

        return HotkeyConflict.None;
    }

    /// <summary>Regel 1: ausserhalb 0x07–0xFE, Maustaste oder Modifikator als Taste.</summary>
    private static bool IsInvalid(int key) =>
        key is < 0x07 or > 0xFE or (>= 0x10 and <= 0x12) or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    /// <summary>Regel 3: Buchstaben, Ziffern, Ziffernblock, Leertaste, Enter, Tab, Rücktaste, Escape, OEM-Tasten.</summary>
    private static bool IsTypingKey(int key) =>
        key is (>= 0x41 and <= 0x5A) or (>= 0x30 and <= 0x39) or (>= 0x60 and <= 0x6F) or 0x20 or 0x0D or 0x09 or 0x08 or 0x1B
            or (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDF) or 0xE2;
}
