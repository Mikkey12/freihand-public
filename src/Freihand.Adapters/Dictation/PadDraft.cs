using Freihand.Core.Recovery;
using Freihand.Core.Segments;

namespace Freihand.Adapters.Dictation;

/// <summary>
/// Verlustschutz der Schreibfläche des Diktatfensters (Grundentscheid 2: im Pilot für das Diktatfenster; ER-06). Jede
/// Änderung ersetzt den gesicherten Text; der Takt des <see cref="RecoveryStore"/> schreibt spätestens nach einer Sekunde.
/// Übernommen beginnt die Haltefrist, Leeren löscht die Sicherung. Beim Beenden bleibt der Text wiederherstellbar.
/// Ohne WPF, damit er prüfbar ist; Fehler werden gemeldet, nie geworfen, und nennen nie Text (SI-01).
/// </summary>
public sealed class PadDraft(RecoveryStore? store) : IDisposable
{
    /// <summary>Eine feste Kennung für den einen Abschnitt „ganzer Text der Schreibfläche“ je Entwurf.</summary>
    private Guid _segment = Guid.NewGuid();

    private DraftRecorder? _recorder;

    /// <summary>Problem mit der Sicherung; nur die Art, nie Text oder Pfad.</summary>
    public event EventHandler<string>? Failed;

    /// <summary>Kennung des laufenden Entwurfs, null ohne Text; beim Durchsehen wird er nicht angeboten.</summary>
    public Guid? DraftId => _recorder?.Id;

    /// <summary>Neuer Stand der Schreibfläche; leer heisst nichts anzubieten.</summary>
    public void Changed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (store is null || (_recorder is null && text.Length == 0))
        {
            return;
        }

        Run(() =>
        {
            _recorder ??= store.StartDraft();
            _recorder.Replace(text.Length == 0
                ? []
                : [new TextSegment(_segment, PadText.ToCore(text), SegmentState.Fixed, TimeSpan.Zero, TimeSpan.Zero)]);
        });
    }

    /// <summary>Sichert sofort, ohne auf den Takt zu warten; wahr, wenn danach nichts ungesichert ist.</summary>
    public bool Flush()
    {
        if (_recorder is not { } recorder)
        {
            return true;
        }

        return Run(recorder.Flush);
    }

    /// <summary>Der Text ist ins gemerkte Feld übernommen: Haltefrist beginnt, neuer Text kommt in einen neuen Entwurf.</summary>
    public void HandedOver()
    {
        if (_recorder is not { } recorder)
        {
            return;
        }

        if (Run(recorder.MarkHandedOver))
        {
            Next();
        }
    }

    /// <summary>„Leeren“: Wer Text loswerden will, löscht ihn hier (Kapitel 3.2); die Sicherung wird sofort gelöscht.</summary>
    public void Cleared()
    {
        if (_recorder is not { } recorder)
        {
            return;
        }

        if (Run(recorder.Discard))
        {
            Next();
        }
    }

    /// <summary>
    /// Regel (Codex zu PR #53): Wird die Sicherung der Schreibfläche von aussen gelöscht (etwa „Alle löschen“ mit
    /// unlesbaren Dateien), wird der aktuelle Text sofort neu gesichert, nicht erst im nächsten Takt; sonst verlöre ein
    /// Absturz dazwischen schon gesicherten Text. Wahr, wenn danach nichts ungesichert ist.
    /// </summary>
    public bool Restart(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Next();
        Changed(text);
        return Flush();
    }

    /// <summary>Beim Beenden: sichern und stehen lassen, damit der Text beim nächsten Start angeboten wird (AU-12).</summary>
    public void Dispose()
    {
        if (_recorder is { } recorder)
        {
            Run(recorder.Dispose);
            _recorder = null;
        }
    }

    private void Next()
    {
        _recorder = null;
        _segment = Guid.NewGuid();
    }

    private bool Run(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (RecoveryException exception)
        {
            Failed?.Invoke(this, exception.Issue.Kind.ToString());
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // Entwurf beendet oder gelöscht (z. B. „Alle Sicherungen löschen“): ab hier ein neuer
            Next();
            Failed?.Invoke(this, exception.GetType().Name);
        }

        return false;
    }
}
