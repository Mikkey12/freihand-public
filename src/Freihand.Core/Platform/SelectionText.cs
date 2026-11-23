using Freihand.Core.Routing;

namespace Freihand.Core.Platform;

/// <summary>
/// Regel für eine gelesene Markierung: Zeilenenden werden zu „\n“; nur Fliesstext mit
/// Zeilenumbrüchen und Tabulatoren gilt. Jedes andere Steuerzeichen (Tabellenzelle, Feld, Objekt, manueller Umbruch in
/// Word) heisst: Freihand kann die Stelle nicht verlustfrei zurückschreiben, also wird sie gar nicht erst gelesen.
/// Die Länge zählt am gelesenen Rohtext, vor jeder Umwandlung: Der Leser liest höchstens <see cref="MaxLength"/> + 1
/// Zeichen, also kann ein längerer Rohtext abgeschnitten sein und wird abgelehnt (Codex zu PR #55).
/// </summary>
public static class SelectionText
{
    /// <summary>Höchstens so viele Zeichen; mehr wird abgelehnt statt gekürzt.</summary>
    public const int MaxLength = 20_000;

    public static SelectionRead From(string? raw, ReadPurpose purpose, DataClass dataClass)
    {
        ArgumentNullException.ThrowIfNull(dataClass);
        if (raw is null)
        {
            return SelectionRead.Refused(SelectionRefusal.Unreadable);
        }

        if (raw.Length > MaxLength)
        {
            return SelectionRead.Refused(SelectionRefusal.TooLong);
        }

        var text = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (text.Trim().Length == 0)
        {
            return SelectionRead.Refused(SelectionRefusal.NoSelection);
        }

        foreach (var c in text)
        {
            // U+FFFC steht für ein eingebettetes Objekt (Bild, Diagramm)
            if ((char.IsControl(c) && c is not '\n' and not '\t') || c == '\uFFFC')
            {
                return SelectionRead.Refused(SelectionRefusal.SpecialContent);
            }
        }

        return new SelectionRead(new ForeignText(text, purpose, dataClass), SelectionRefusal.None);
    }
}
