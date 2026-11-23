namespace Freihand.Core.Commands;

/// <summary>Wo ein Eintrag der Befehlsliste gilt (Anhang A, Spalte „Geltungsbereich“).</summary>
public enum CommandScope
{
    /// <summary>Überall; in fremden Programmen regelt AU-13, wie ein Umbruch ankommt.</summary>
    Everywhere,

    /// <summary>Nur im eigenen Diktatfenster (AU-04).</summary>
    DictationWindow,
}

/// <summary>
/// Wirkung eines Eintrags (Anhang A, Spalte „Wirkung“). Die Wirkungen sind fest im Code; Daten wählen nur
/// aus, damit Befehle nie ausführbare Inhalte tragen (OF-01). Es gibt bewusst keine Wirkung, die
/// „wörtlich schreiben“ per Stimme beendet: Aus nur über die Befehlstaste (BA-22).
/// </summary>
public enum CommandEffect
{
    /// <summary>Setzt ein Satzzeichen (BA-20); das Zeichen steht in <see cref="CommandDefinition.Text"/>.</summary>
    InsertPunctuation,

    /// <summary>Neue Zeile, nie Senden (BA-20, AU-13).</summary>
    LineBreak,

    /// <summary>Neuer Absatz, nie Senden (BA-20, AU-13).</summary>
    ParagraphBreak,

    /// <summary>Schaltet Befehle, Diktatzeichen und Umformung ab (BA-22).</summary>
    EnterLiteralMode,

    /// <summary>Löscht das letzte Wort (BA-05).</summary>
    DeleteLastWord,

    /// <summary>Löscht den letzten Satz (BA-05).</summary>
    DeleteLastSentence,

    /// <summary>Nimmt den letzten Abschnitt zurück (AU-02).</summary>
    Undo,

    /// <summary>Fügt den festen Text nach Zielprüfung ins gebundene Ziel ein (AU-10).</summary>
    Commit,

    /// <summary>Bindet das aktuelle Eingabefeld als Ziel (AU-10).</summary>
    Rebind,

    /// <summary>Beendet die Aufnahme und verwirft Ausstehendes (BE-06).</summary>
    Cancel,
}

/// <summary>
/// Ein Eintrag der Befehlsliste als Daten (OF-01, Grundentscheid 5): gesprochene Wendungen, Klasse,
/// Geltungsbereich und Wirkung. Die Erkennung im Text ist Sache von Paket P3.
/// </summary>
public sealed record CommandDefinition
{
    public CommandDefinition(
        string id,
        CommandClass commandClass,
        IEnumerable<string> phrases,
        CommandScope scope,
        CommandEffect effect,
        string? text,
        string description)
    {
        Id = id;
        Class = commandClass;
        Phrases = [.. phrases];
        Scope = scope;
        Effect = effect;
        Text = text;
        Description = description;
    }

    /// <summary>Fester Bezeichner, z. B. <c>comma</c>.</summary>
    public string Id { get; }

    public CommandClass Class { get; }

    /// <summary>Gesprochene Wendungen; die erste ist die Hauptform für die Einblendung (BE-16).</summary>
    public IReadOnlyList<string> Phrases { get; }

    public CommandScope Scope { get; }

    public CommandEffect Effect { get; }

    /// <summary>Das Satzzeichen bei <see cref="CommandEffect.InsertPunctuation"/>, sonst leer.</summary>
    public string? Text { get; }

    /// <summary>Wirkung in Worten für die Einblendung (BE-12, BE-16).</summary>
    public string Description { get; }

    public bool Equals(CommandDefinition? other) =>
        other is not null
        && Id == other.Id
        && Class == other.Class
        && Phrases.SequenceEqual(other.Phrases)
        && Scope == other.Scope
        && Effect == other.Effect
        && Text == other.Text
        && Description == other.Description;

    public override int GetHashCode() => HashCode.Combine(Id, Class, Scope, Effect, Text, Description, Phrases.Count);
}
