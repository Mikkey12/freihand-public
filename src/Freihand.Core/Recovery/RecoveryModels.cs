using Freihand.Core.Segments;

namespace Freihand.Core.Recovery;

/// <summary>Ein gesicherter fester Abschnitt (Anhang B: fest). Ohne Audio und ohne Zeitmarken im Audio.</summary>
public sealed record RecoveredSegment(Guid Id, string Text);

/// <summary>
/// Ein Entwurf, der nach einem Neustart angeboten werden kann (AU-12). Er wird nie automatisch
/// eingefügt; was damit geschieht, entscheidet der Benutzer.
/// </summary>
/// <param name="Id">Kennung des Diktats.</param>
/// <param name="CreatedAt">Erste Sicherung; ab hier läuft die Frist von 24 Stunden.</param>
/// <param name="UpdatedAt">Letzte Änderung des festen Texts.</param>
/// <param name="HandedOverAt">Übernahme ins Ziel; ab hier läuft die Haltefrist. Leer, wenn nicht übernommen.</param>
/// <param name="DeleteAt">Spätester Zeitpunkt der Löschung (ER-06).</param>
/// <param name="Segments">Die festen Abschnitte in ihrer Reihenfolge.</param>
public sealed record RecoverableDraft(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? HandedOverAt,
    DateTimeOffset DeleteAt,
    IReadOnlyList<RecoveredSegment> Segments)
{
    /// <summary>Der feste Text, Abschnitte nach der Fügeregel aus P3 zusammengefügt (<see cref="TextJoin"/>).</summary>
    public string Text => TextJoin.Join(Segments.Select(segment => segment.Text));
}

/// <summary>Art eines Problems mit der Ablage. Probleme werden gemeldet, nie still übergangen (ER-06).</summary>
public enum RecoveryIssueKind
{
    /// <summary>Eine temporäre Datei blieb liegen: Abbruch zwischen Schreiben und Umbenennen.</summary>
    IncompleteWrite,

    /// <summary>Datei unvollständig, verändert, nicht entschlüsselbar oder unbekannt.</summary>
    Damaged,

    ReadFailed,

    WriteFailed,

    DeleteFailed,
}

/// <summary>
/// Ein Problem mit der Ablage. Die Meldung enthält nie Text aus dem Diktat (SI-11: Protokoll nur Metadaten).
/// </summary>
/// <param name="DraftId">Betroffener Entwurf, falls bekannt; damit lässt er sich einzeln löschen.</param>
public sealed record RecoveryIssue(RecoveryIssueKind Kind, string FileName, Guid? DraftId, string Message);

/// <summary>Ergebnis der Durchsicht nach einem Neustart: anzubietende Entwürfe und gefundene Probleme (AU-12).</summary>
public sealed record RecoveryScan(IReadOnlyList<RecoverableDraft> Drafts, IReadOnlyList<RecoveryIssue> Issues);

public sealed class RecoveryIssueEventArgs(RecoveryIssue issue) : EventArgs
{
    public RecoveryIssue Issue { get; } = issue;
}

/// <summary>Die Ablage konnte nicht geschrieben, gelesen oder gelöscht werden.</summary>
public sealed class RecoveryException : Exception
{
    public RecoveryException(RecoveryIssue issue, Exception? innerException = null)
        : base(issue?.Message, innerException)
    {
        ArgumentNullException.ThrowIfNull(issue);
        Issue = issue;
    }

    public RecoveryIssue Issue { get; }
}
