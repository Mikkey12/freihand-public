using Freihand.Core.Recognition;
using Freihand.Core.Segments;

namespace Freihand.Core.Recovery;

/// <summary>
/// Sicherung eines einzelnen Diktats (ER-06). Nimmt nur festen Text an; vorläufiger Text wird
/// abgewiesen oder übergangen (Grundentscheid 2, Anhang B). Geänderter Text wird im Takt der
/// <see cref="RecoveryStore"/> gesichert, spätestens nach einer Sekunde.
/// </summary>
public sealed class DraftRecorder : IDisposable
{
    private readonly RecoveryStore _store;
    private readonly List<RecoveredSegment> _segments = [];
    private RecoveryIssue? _lastIssue;
    private bool _dirty;
    private bool _closed;
    private bool _detached;

    internal DraftRecorder(RecoveryStore store, Guid id, DateTimeOffset createdAt)
    {
        _store = store;
        Id = id;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? HandedOverAt { get; private set; }

    /// <summary>
    /// Wird noch gesichert? Nicht mehr nach gesicherter Übernahme, Verwerfen, <see cref="Dispose"/>, Löschen
    /// durch den Benutzer (SI-11) oder nach Ablauf der Frist (ER-06). Eine Übernahme, deren Sicherung
    /// gescheitert ist, gilt bis zum Erfolg als laufend.
    /// </summary>
    public bool IsRecording
    {
        get
        {
            lock (_store.Gate)
            {
                return !_closed && !_detached;
            }
        }
    }

    /// <summary>Letztes Problem beim Sichern im Takt; leer, sobald wieder gesichert werden konnte.</summary>
    public RecoveryIssue? LastIssue
    {
        get
        {
            lock (_store.Gate)
            {
                return _lastIssue;
            }
        }
    }

    internal IReadOnlyList<RecoveredSegment> Segments => _segments;

    /// <summary>Übernimmt die neu festen Abschnitte eines Zwischenstands; der vorläufige Teil wird nie gesichert.</summary>
    public void Apply(RecognitionUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        EnsureFixed(update.Fixed);
        lock (_store.Gate)
        {
            ThrowIfNotAcceptingText();
            foreach (var segment in update.Fixed)
            {
                Upsert(segment);
            }
        }
    }

    /// <summary>Hängt einen festen Abschnitt an oder ersetzt den Abschnitt mit gleicher Kennung.</summary>
    /// <exception cref="ArgumentException">Der Abschnitt ist vorläufig.</exception>
    public void Append(TextSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        EnsureFixed([segment]);
        lock (_store.Gate)
        {
            ThrowIfNotAcceptingText();
            Upsert(segment);
        }
    }

    /// <summary>Ersetzt den ganzen festen Text, etwa nach einer Korrektur im Diktatfenster (BA-05, AU-02).</summary>
    /// <exception cref="ArgumentException">Mindestens ein Abschnitt ist vorläufig.</exception>
    public void Replace(IEnumerable<TextSegment> fixedSegments)
    {
        ArgumentNullException.ThrowIfNull(fixedSegments);
        var segments = fixedSegments.ToList();
        EnsureFixed(segments);
        lock (_store.Gate)
        {
            ThrowIfNotAcceptingText();
            _segments.Clear();
            _segments.AddRange(segments.Select(segment => new RecoveredSegment(segment.Id, segment.Text)));
            Touch();
        }
    }

    /// <summary>Sichert sofort, ohne auf den Takt zu warten; holt auch eine gescheiterte Übernahme nach.</summary>
    /// <exception cref="RecoveryException">Schreiben fehlgeschlagen; der Takt versucht es weiter.</exception>
    public void Flush()
    {
        lock (_store.Gate)
        {
            if (_closed || _detached || !_dirty)
            {
                return;
            }

            Persist();
        }
    }

    /// <summary>
    /// Der feste Text ist ins Ziel übernommen (AU-04, AU-10). Ab jetzt läuft die Haltefrist; ist sie
    /// abgeschaltet, wird der Entwurf sofort gelöscht (ER-06). Danach nimmt der Entwurf keinen Text mehr an.
    /// Scheitert das Sichern oder Löschen, bleibt die Übernahme ausstehend: Takt, <see cref="Flush"/> oder
    /// ein erneuter Aufruf versuchen es wieder, mit dem Zeitpunkt der ersten Übernahme (Codex-Review R2).
    /// </summary>
    /// <exception cref="RecoveryException">Sichern oder Löschen fehlgeschlagen; wird wiederholt.</exception>
    /// <exception cref="InvalidOperationException">Die Übernahme ist schon gesichert oder der Entwurf beendet.</exception>
    public void MarkHandedOver()
    {
        lock (_store.Gate)
        {
            ThrowIfClosed();
            HandedOverAt ??= _store.Time.GetUtcNow();
            _dirty = true;
            if (_detached)
            {
                Close();
                return;
            }

            Persist();
        }
    }

    /// <summary>Verwirft den Entwurf, etwa mit „abbrechen“ (BE-06); er wird sofort gelöscht.</summary>
    /// <exception cref="RecoveryException">Löschen fehlgeschlagen.</exception>
    public void Discard()
    {
        lock (_store.Gate)
        {
            if (_closed)
            {
                return;
            }

            Close();
            if (!_detached)
            {
                _store.DeleteDraftCore(Id);
            }
        }
    }

    /// <summary>
    /// Beendet die Sicherung ohne Übernahme, etwa beim Beenden der App. Ausstehender Text wird gesichert;
    /// der Entwurf bleibt wiederherstellbar und wird beim nächsten Start angeboten (BE-18, AU-12).
    /// </summary>
    public void Dispose()
    {
        var issues = new List<RecoveryIssue>();
        lock (_store.Gate)
        {
            if (_closed)
            {
                return;
            }

            FlushInto(issues);
            Close();
        }

        _store.Report(issues);
    }

    /// <summary>Sichern im Takt; Fehler landen in <paramref name="issues"/> statt als Ausnahme.</summary>
    internal void FlushInto(List<RecoveryIssue> issues)
    {
        if (_closed || _detached || !_dirty)
        {
            return;
        }

        try
        {
            Persist();
        }
        catch (RecoveryException exception)
        {
            issues.Add(exception.Issue);
        }
    }

    /// <summary>Gelöscht durch den Benutzer oder nach Frist: ab jetzt wird nichts mehr geschrieben.</summary>
    internal void Detach() => _detached = true;

    private static void EnsureFixed(IEnumerable<TextSegment> segments)
    {
        // Nie vorläufiger Text in der Ablage (Grundentscheid 2, Anhang B)
        if (segments.Any(segment => segment is null || segment.State != SegmentState.Fixed))
        {
            throw new ArgumentException("Gesichert wird nur fester Text.", nameof(segments));
        }
    }

    /// <summary>Bringt die Ablage auf den Stand dieses Entwurfs: sichern oder, wenn die Frist um ist, löschen.</summary>
    private void Persist()
    {
        var expired = _store.Time.GetUtcNow() >= _store.DeadlineOf(CreatedAt, HandedOverAt);
        try
        {
            if (expired || (HandedOverAt is not null && _store.Options.HoldPeriod == TimeSpan.Zero))
            {
                _store.DeleteDraftCore(Id);
            }
            else
            {
                _store.SaveCore(this);
            }

            _dirty = false;
            _lastIssue = null;
        }
        catch (RecoveryException exception)
        {
            // Bleibt geändert und laufend, damit der nächste Takt es erneut versucht
            _lastIssue = exception.Issue;
            throw;
        }

        if (HandedOverAt is not null)
        {
            Close();
        }
        else if (expired)
        {
            // Spätestens nach 24 Stunden wird auch ein offenes Diktat nicht mehr gesichert (ER-06)
            _detached = true;
            _store.Release(Id);
        }
    }

    private void Close()
    {
        _closed = true;
        _store.Release(Id);
    }

    private void Upsert(TextSegment segment)
    {
        var index = _segments.FindIndex(existing => existing.Id == segment.Id);
        var recovered = new RecoveredSegment(segment.Id, segment.Text);
        if (index < 0)
        {
            _segments.Add(recovered);
        }
        else if (_segments[index] != recovered)
        {
            _segments[index] = recovered;
        }
        else
        {
            return;
        }

        Touch();
    }

    private void Touch()
    {
        UpdatedAt = _store.Time.GetUtcNow();
        _dirty = true;
    }

    private void ThrowIfNotAcceptingText()
    {
        ThrowIfClosed();
        if (HandedOverAt is not null)
        {
            throw new InvalidOperationException("Der Entwurf ist übernommen und nimmt keinen Text mehr an.");
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new InvalidOperationException("Der Entwurf ist übernommen, verworfen oder beendet.");
        }
    }
}
