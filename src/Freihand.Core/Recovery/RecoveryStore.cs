using System.Security.Cryptography;

namespace Freihand.Core.Recovery;

/// <summary>
/// Wiederherstellung gemäss Grundentscheid 2 (ER-06, AU-12, SI-11): sichert festen Text je Diktat
/// verschlüsselt, hält ihn nach der Übernahme eine Haltefrist lang, löscht ihn spätestens nach
/// 24 Stunden und bietet ihn nach einem Neustart an, ohne ihn je einzufügen. Audio und vorläufiger
/// Text gelangen nie in die Ablage. Ein Takt (<see cref="RecoveryOptions.SaveInterval"/>) sichert
/// geänderte Entwürfe und löscht abgelaufene.
/// </summary>
public sealed class RecoveryStore : IDisposable
{
    private readonly IRecoveryFileSystem _files;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;

    // Löschfristen aller bekannten Dateien, damit der Takt ohne Entschlüsseln löschen kann (ER-06)
    private readonly Dictionary<Guid, DateTimeOffset> _deadlines = [];
    private readonly Dictionary<Guid, DraftRecorder> _active = [];

    // Vorübergehend unlesbare Entwürfe: der Takt liest sie erneut, um die echte Frist zu ermitteln (Codex-Nachprüfung N1)
    private readonly HashSet<Guid> _unreadable = [];

    // Probleme des letzten Takts: ein andauerndes Problem wird einmal gemeldet, nicht jede Sekunde
    private HashSet<(RecoveryIssueKind Kind, string FileName, Guid? DraftId)> _lastTickIssues = [];
    private bool _indexed;
    private bool _disposed;

    public RecoveryStore(
        IRecoveryFileSystem files, IDataProtector protector, TimeProvider time, RecoveryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(time);

        Options = options ?? new RecoveryOptions();
        Options.Validate();
        _files = files;
        _protector = protector;
        _time = time;
        _timer = time.CreateTimer(
            static state => ((RecoveryStore)state!).OnTick(), this, Options.SaveInterval, Options.SaveInterval);
    }

    /// <summary>Probleme beim Sichern und Löschen im Takt; nie still übergangen (ER-06).</summary>
    public event EventHandler<RecoveryIssueEventArgs>? IssueDetected;

    public RecoveryOptions Options { get; }

    internal Lock Gate { get; } = new();

    internal TimeProvider Time => _time;

    /// <summary>Beginnt die Sicherung eines neuen Diktats.</summary>
    public DraftRecorder StartDraft()
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var recorder = new DraftRecorder(this, Guid.NewGuid(), _time.GetUtcNow());
            _active.Add(recorder.Id, recorder);
            return recorder;
        }
    }

    /// <summary>
    /// Durchsicht nach einem Neustart (AU-12, BE-18): liefert die anzubietenden Entwürfe, neueste zuerst,
    /// und meldet abgebrochene und beschädigte Dateien. Abgelaufenes wird dabei gelöscht, liegengebliebene
    /// temporäre Dateien ebenso. Fügt nie etwas ein. Ist die Sicherung abgeschaltet, wird alles gelöscht.
    /// </summary>
    public RecoveryScan Scan()
    {
        var drafts = new List<RecoverableDraft>();
        var issues = new List<RecoveryIssue>();
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ScanCore(drafts, issues);
        }

        return new([.. drafts.OrderByDescending(draft => draft.UpdatedAt)], issues);
    }

    /// <summary>Löscht einen Entwurf samt allfälliger temporärer Datei (SI-11); ein laufendes Diktat sichert danach nicht mehr.</summary>
    /// <exception cref="RecoveryException">Die Datei liess sich nicht löschen.</exception>
    public void Delete(Guid draftId)
    {
        var issues = new List<RecoveryIssue>();
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active.Remove(draftId, out var recorder))
            {
                recorder.Detach();
            }

            TryDeleteIndexed(draftId, issues);
        }

        ThrowIfAny(issues);
    }

    /// <summary>Löscht die ganze Ablage (SI-11), auch beschädigte und unbekannte Dateien; laufende Diktate sichern danach nicht mehr.</summary>
    /// <exception cref="RecoveryException">Mindestens eine Datei liess sich nicht löschen.</exception>
    public void DeleteAll()
    {
        var issues = new List<RecoveryIssue>();
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var recorder in _active.Values)
            {
                recorder.Detach();
            }

            _active.Clear();
            DeleteAllCore(issues);
        }

        ThrowIfAny(issues);
    }

    /// <summary>
    /// Sichert alle offenen Entwürfe sofort, ohne auf den Takt zu warten; die Ablage bleibt offen. Für ein Ende, nach dem
    /// der Prozess jederzeit enden darf, während die übrige Freigabe noch läuft. Fehler meldet sie wie der Takt.
    /// </summary>
    public void Flush()
    {
        var issues = new List<RecoveryIssue>();
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var recorder in _active.Values.ToList())
            {
                recorder.FlushInto(issues);
            }
        }

        Report(issues);
    }

    /// <summary>Sichert offene Entwürfe ein letztes Mal und hält den Takt an. Die Entwürfe bleiben wiederherstellbar.</summary>
    public void Dispose()
    {
        var issues = new List<RecoveryIssue>();
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var recorder in _active.Values.ToList())
            {
                recorder.FlushInto(issues);
                recorder.Detach();
            }

            _active.Clear();
            _disposed = true;
        }

        _timer.Dispose();
        Report(issues);
    }

    internal DateTimeOffset DeadlineOf(DateTimeOffset createdAt, DateTimeOffset? handedOverAt)
    {
        // Haltefrist nach der Übernahme, aber nie über 24 Stunden nach dem Anlegen hinaus (ER-06)
        var latest = createdAt + RecoveryOptions.MaxRetention;
        return handedOverAt is { } handedOver && handedOver + Options.HoldPeriod < latest
            ? handedOver + Options.HoldPeriod
            : latest;
    }

    /// <summary>Schreibt den Entwurf absturzsicher: erst temporäre Datei, dann Umbenennen (ER-06).</summary>
    /// <exception cref="RecoveryException">Schreiben oder Umbenennen ist fehlgeschlagen.</exception>
    internal void SaveCore(DraftRecorder recorder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Options.Enabled)
        {
            return;
        }

        if (recorder.Segments.Count == 0)
        {
            // Ohne festen Text gibt es nichts anzubieten
            DeleteDraftCore(recorder.Id);
            return;
        }

        var payload = new DraftPayload(
            recorder.Id, recorder.CreatedAt, recorder.UpdatedAt, recorder.HandedOverAt, [.. recorder.Segments]);
        var tempName = DraftFile.TempNameOf(recorder.Id);
        var name = DraftFile.NameOf(recorder.Id);

        try
        {
            _files.WriteAllBytes(tempName, DraftFile.Encode(payload, _protector));
            _files.Replace(tempName, name);
        }
        catch (Exception exception) when (IsIoFailure(exception) || exception is CryptographicException)
        {
            // Die vorherige Fassung bleibt gültig; eine liegengebliebene temporäre Datei meldet die nächste Durchsicht
            TryDelete(tempName, recorder.Id, []);
            throw new RecoveryException(
                new(RecoveryIssueKind.WriteFailed, name, recorder.Id, $"Sichern fehlgeschlagen: {exception.Message}"),
                exception);
        }

        _deadlines[recorder.Id] = DeadlineOf(recorder.CreatedAt, recorder.HandedOverAt);
    }

    /// <exception cref="RecoveryException">Die Datei liess sich nicht löschen.</exception>
    internal void DeleteDraftCore(Guid draftId)
    {
        var issues = new List<RecoveryIssue>();
        if (!TryDeleteIndexed(draftId, issues))
        {
            throw new RecoveryException(issues[0]);
        }
    }

    internal void Release(Guid draftId) => _active.Remove(draftId);

    internal void Report(IReadOnlyList<RecoveryIssue> issues)
    {
        foreach (var issue in issues)
        {
            IssueDetected?.Invoke(this, new(issue));
        }
    }

    private static bool IsIoFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static void ThrowIfAny(List<RecoveryIssue> issues)
    {
        if (issues.Count > 0)
        {
            throw new RecoveryException(issues[0]);
        }
    }

    private void OnTick()
    {
        var issues = new List<RecoveryIssue>();
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            // Spätestens jede Sekunde sichern (ER-06), dann Abgelaufenes löschen. Eine Kopie, weil eine
            // nachgeholte Übernahme den Entwurf aus der Liste der laufenden entfernt
            foreach (var recorder in _active.Values.ToList())
            {
                recorder.FlushInto(issues);
            }

            PurgeCore(issues);

            var previous = _lastTickIssues;
            _lastTickIssues = [.. issues.Select(issue => (issue.Kind, issue.FileName, issue.DraftId))];
            issues.RemoveAll(issue => previous.Contains((issue.Kind, issue.FileName, issue.DraftId)));
        }

        Report(issues);
    }

    private void PurgeCore(List<RecoveryIssue> issues)
    {
        var now = _time.GetUtcNow();
        if (!_indexed)
        {
            // Erster Takt ohne vorherige Durchsicht oder eine gescheiterte: Dateien (neu) erfassen
            ScanCore(null, issues);
        }
        else
        {
            // Zuvor unlesbare Entwürfe erneut lesen; die echte Frist kann vor der Ersatzfrist liegen (Codex-Nachprüfung N1)
            foreach (var id in _unreadable.ToList())
            {
                _indexed &= IndexDraft(DraftFile.NameOf(id), id, now, null, issues);
            }
        }

        // Bekannte Fristen gelten immer, auch wenn das Erfassen gescheitert ist (Codex-Nachprüfung 2, F1)
        foreach (var (id, _) in _deadlines.Where(entry => entry.Value <= now).ToList())
        {
            if (_active.Remove(id, out var recorder))
            {
                // Auch ein offenes Diktat wird spätestens nach 24 Stunden nicht mehr gesichert
                recorder.Detach();
            }

            TryDeleteIndexed(id, issues);
        }
    }

    private void ScanCore(List<RecoverableDraft>? drafts, List<RecoveryIssue> issues)
    {
        if (!Options.Enabled)
        {
            // Abgeschaltet heisst: nichts liegt in der Ablage (ER-06)
            DeleteAllCore(issues);
            _indexed = true;
            return;
        }

        IReadOnlyList<string> names;
        try
        {
            names = _files.ListFiles();
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            issues.Add(new(RecoveryIssueKind.ReadFailed, string.Empty, null, $"Ablage nicht lesbar: {exception.Message}"));
            _indexed = false;
            return;
        }

        // Was sich jetzt weder lesen noch löschen lässt, erfasst der nächste Takt erneut (Codex-Review R1)
        var rescan = false;
        var now = _time.GetUtcNow();
        foreach (var name in names)
        {
            var id = DraftFile.IdOf(name);
            if (id is { } activeId && _active.ContainsKey(activeId))
            {
                // Laufendes Diktat dieses Laufs: wird nicht angeboten, sein Takt kümmert sich darum
                continue;
            }

            if (name.EndsWith(DraftFile.TempExtension, StringComparison.Ordinal))
            {
                issues.Add(new(
                    RecoveryIssueKind.IncompleteWrite,
                    name,
                    id,
                    "Abbruch zwischen Schreiben und Umbenennen; die vorherige Fassung gilt, die temporäre Datei wird gelöscht."));
                rescan |= !TryDelete(name, id, issues);
                continue;
            }

            if (id is not { } draftId)
            {
                issues.Add(new(
                    RecoveryIssueKind.Damaged, name, null, "Unbekannte Datei in der Ablage; sie wird mit „alles löschen“ entfernt."));
                continue;
            }

            rescan |= !IndexDraft(name, draftId, now, drafts, issues);
        }

        _indexed = !rescan;
    }

    /// <summary>
    /// Liest einen Entwurf, trägt seine Frist ein und löscht ihn, wenn sie abgelaufen ist; ein lesbarer, noch
    /// gültiger Entwurf kommt in <paramref name="drafts"/>. Gibt <c>false</c> zurück, wenn die nächste
    /// Durchsicht ihn erneut erfassen muss.
    /// </summary>
    private bool IndexDraft(
        string name, Guid draftId, DateTimeOffset now, List<RecoverableDraft>? drafts, List<RecoveryIssue> issues)
    {
        try
        {
            var payload = DraftFile.Decode(_files.ReadAllBytes(name), draftId, _protector);
            var deleteAt = DeadlineOf(payload.CreatedAt, payload.HandedOverAt);
            _unreadable.Remove(draftId);

            // Frist zuerst eintragen: scheitert das Löschen, versucht es der Takt erneut (Codex-Review R1)
            _deadlines[draftId] = deleteAt;
            if (deleteAt <= now)
            {
                TryDeleteIndexed(draftId, issues);
                return true;
            }

            drafts?.Add(new(
                payload.Id, payload.CreatedAt, payload.UpdatedAt, payload.HandedOverAt, deleteAt, payload.Segments));
        }
        catch (InvalidDataException exception)
        {
            _unreadable.Remove(draftId);
            IndexDamaged(name, draftId, exception.Message, now, issues);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Inzwischen anderweitig entfernt: nichts mehr zu tun
            _unreadable.Remove(draftId);
            _deadlines.Remove(draftId);
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            issues.Add(new(RecoveryIssueKind.ReadFailed, name, draftId, $"Lesen fehlgeschlagen: {exception.Message}"));
            _unreadable.Add(draftId);
            return IndexUnreadable(name, draftId, now, issues);
        }

        return true;
    }

    private void IndexDamaged(string name, Guid draftId, string reason, DateTimeOffset now, List<RecoveryIssue> issues)
    {
        // Die Frist von 24 Stunden gilt auch für Unlesbares; massgeblich ist dann der Zeitpunkt des letzten Schreibens
        DateTimeOffset deleteAt;
        try
        {
            deleteAt = _files.GetLastWriteTime(name) + RecoveryOptions.MaxRetention;
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            deleteAt = now;
        }

        deleteAt = NotLaterThanKnown(draftId, deleteAt);
        _deadlines[draftId] = deleteAt;
        if (deleteAt <= now)
        {
            issues.Add(new(RecoveryIssueKind.Damaged, name, draftId, $"{reason} Älter als 24 Stunden, wird gelöscht."));
            TryDeleteIndexed(draftId, issues);
            return;
        }

        issues.Add(new(RecoveryIssueKind.Damaged, name, draftId, reason));
    }

    /// <summary>
    /// Vorübergehend unlesbare Datei: Bis sie wieder lesbar ist, gilt als Ersatzfrist das letzte Schreiben plus
    /// 24 Stunden. Sie löscht nie zu früh, weil das letzte Schreiben nie vor dem Anlegen liegt, kann aber später
    /// liegen als die echte Frist (Anlegen + 24 h oder Übernahme + Haltefrist). Darum liest der Takt die Datei
    /// erneut, und eine schon bekannte frühere Frist wird nie verlängert (ER-06, Codex-Nachprüfung N1).
    /// Gibt <c>false</c> zurück, wenn auch der Zeitpunkt des letzten Schreibens nicht lesbar ist.
    /// </summary>
    private bool IndexUnreadable(string name, Guid draftId, DateTimeOffset now, List<RecoveryIssue> issues)
    {
        DateTimeOffset deleteAt;
        try
        {
            deleteAt = _files.GetLastWriteTime(name) + RecoveryOptions.MaxRetention;
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return false;
        }

        deleteAt = NotLaterThanKnown(draftId, deleteAt);
        _deadlines[draftId] = deleteAt;
        if (deleteAt <= now)
        {
            TryDeleteIndexed(draftId, issues);
        }

        return true;
    }

    private DateTimeOffset NotLaterThanKnown(Guid draftId, DateTimeOffset fallback) =>
        _deadlines.TryGetValue(draftId, out var known) && known < fallback ? known : fallback;

    private void DeleteAllCore(List<RecoveryIssue> issues)
    {
        _deadlines.Clear();
        _unreadable.Clear();
        IReadOnlyList<string> names;
        try
        {
            names = _files.ListFiles();
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            issues.Add(new(RecoveryIssueKind.DeleteFailed, string.Empty, null, $"Ablage nicht lesbar: {exception.Message}"));
            _indexed = false;
            return;
        }

        var failed = false;
        foreach (var name in names)
        {
            failed |= !TryDelete(name, DraftFile.IdOf(name), issues);
        }

        // Was nicht gelöscht werden konnte, erfasst die nächste Durchsicht erneut samt Frist
        _indexed = !failed;
    }

    /// <summary>Löscht einen Entwurf und nimmt ihn erst bei Erfolg aus dem Fristenindex (Codex-Review R1).</summary>
    private bool TryDeleteIndexed(Guid draftId, List<RecoveryIssue> issues)
    {
        if (!TryDeleteDraft(draftId, issues))
        {
            return false;
        }

        _deadlines.Remove(draftId);
        _unreadable.Remove(draftId);
        return true;
    }

    private bool TryDeleteDraft(Guid draftId, List<RecoveryIssue> issues) =>
        TryDelete(DraftFile.NameOf(draftId), draftId, issues) & TryDelete(DraftFile.TempNameOf(draftId), draftId, issues);

    private bool TryDelete(string name, Guid? draftId, List<RecoveryIssue> issues)
    {
        try
        {
            _files.Delete(name);
            return true;
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            issues.Add(new(RecoveryIssueKind.DeleteFailed, name, draftId, $"Löschen fehlgeschlagen: {exception.Message}"));
            return false;
        }
    }
}
