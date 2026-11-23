using System.Runtime.CompilerServices;
using Freihand.Core.Commands;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;

namespace Freihand.Core.Live;

// Verträge aus dem P3-Konzept (Vertrag 6).

/// <summary>Ausgabe der Sitzung.</summary>
public abstract record DictationOutput;

/// <summary>Fester und vorläufiger Text an <c>DraftRecorder.Apply</c> (P5) und die Ausgabe.</summary>
public sealed record TextOutput(RecognitionUpdate Update) : DictationOutput;

public sealed record CommandOutput(RunCommand Command) : DictationOutput;

public sealed record HintOutput(CommandHint? Hint, LiveNotice? Notice) : DictationOutput;

/// <summary>
/// Eine abgeschlossene Äusserung mit den Wörtern, wie die Erkennung sie geliefert hat (P11 S4, Lernen); nur wenn die Sitzung
/// mit <c>reportHeard</c> gebaut ist, nie beim Diktat.
/// </summary>
public sealed record HeardOutput(Utterance Utterance) : DictationOutput;

/// <summary>
/// Verbindet Erkennung, Auslegung und Formatierung; einzige Stelle, die festen Text veröffentlicht (Kapitel 3, Stufe 4).
/// Je Textäusserung entsteht genau ein fester Abschnitt mit der Kennung der Äusserung; keine Kennung wird zweimal fest
/// gemeldet.
/// </summary>
public sealed class DictationSession
{
    private readonly ITranscriptSource _source;
    private readonly DictationInterpreter _interpreter;
    private readonly Func<CommandScope> _scope;
    private readonly bool _reportHeard;

    /// <param name="reportHeard">
    /// Fürs Lernen: vor der Auslegung jeder abgeschlossenen Äusserung ein <see cref="HeardOutput"/> mit ihren Wörtern.
    /// </param>
    public DictationSession(ITranscriptSource source, DictationInterpreter interpreter, Func<CommandScope> scope, bool reportHeard = false)
    {
        _reportHeard = reportHeard;
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(interpreter);
        ArgumentNullException.ThrowIfNull(scope);
        _source = source;
        _interpreter = interpreter;
        _scope = scope;
    }

    /// <summary>
    /// Liefert die Ausgabe des Diktats. Nach dem Befehl „abbrechen“ kommt nichts mehr, und die Erkennung wird beendet;
    /// nach einem Abbruch über <paramref name="cancellationToken"/> ebenso (Kapitel 5.4, BE-06).
    /// </summary>
    public async IAsyncEnumerable<DictationOutput> RunAsync(
        IAsyncEnumerable<AudioChunk> audio,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var previews = new List<TextSegment>();
        // Eigene Lebensdauer der Sitzung (R03): Nur am regulären Ende der Quelle wird auf ihre Freigabe gewartet. Bei
        // Abbruchbefehl, Fehler, Abbruch des Aufrufers oder vorzeitigem Beenden durch den Leser wird zuerst die Quelle
        // abgebrochen, dann ihre Freigabe nur angestossen; eine Quelle, die den Abbruch übergeht, hält nichts auf.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var events = new CancellableEnumerator<TranscriptEvent>(_source.RunAsync(audio, lifetime.Token), lifetime.Token);
        var regular = false;
        try
        {
            while (true)
            {
                if (!await events.MoveNextAsync(lifetime.Token).ConfigureAwait(false))
                {
                    regular = true;
                    break;
                }

                // Späte Ereignisse einer Quelle, die den Abbruch übergeht, werden nicht ausgegeben (T35, T55)
                cancellationToken.ThrowIfCancellationRequested();
                var (outputs, stop) = Handle(events.Current, previews);
                foreach (var output in outputs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return output;
                }

                if (stop)
                {
                    // Nach dem Abbruchbefehl nichts mehr; wartende Decodes werden verworfen (T17, T55)
                    break;
                }
            }
        }
        finally
        {
            if (!regular)
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
            }

            await events.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Setzt die Grenze bei <see cref="ITranscriptSource.AcceptedEnd"/> und meldet die Taste der Auslegung.</summary>
    public void PressCommandKey()
    {
        // Grenze am Ende des bis dahin angenommenen Audios, nie danach (Vertrag 6). Die Auslegung kennt die Taste zuerst,
        // damit keine Äusserung nach der Grenze ohne sie ausgelegt wird.
        var at = _source.AcceptedEnd;
        _interpreter.PressCommandKey(at);
        _source.MarkBoundary(at);
    }

    private (List<DictationOutput> Outputs, bool Stop) Handle(TranscriptEvent transcriptEvent, List<TextSegment> previews)
    {
        switch (transcriptEvent)
        {
            case ProvisionalText preview:
                return (ShowPreview(preview, previews), false);

            case UtteranceCompleted completed when _reportHeard:
                var (outputs, stop) = Complete(completed.Utterance, previews);
                outputs.Insert(0, new HeardOutput(completed.Utterance));
                return (outputs, stop);

            case UtteranceCompleted completed:
                return Complete(completed.Utterance, previews);

            case LiveNotice notice:
                return ([new HintOutput(null, notice)], false);

            default:
                return ([], false);
        }
    }

    /// <summary>Jede Vorschau ersetzt die vorige derselben Äusserung ganz; auch scharf geschaltete zeigen ihren Rohtext.</summary>
    private static List<DictationOutput> ShowPreview(ProvisionalText preview, List<TextSegment> previews)
    {
        var index = previews.FindIndex(segment => segment.Id == preview.UtteranceId);
        var text = preview.Text ?? string.Empty;
        if (index >= 0 && previews[index].Text == text)
        {
            return [];
        }

        if (text.Length == 0)
        {
            if (index < 0)
            {
                return [];
            }

            previews.RemoveAt(index);
        }
        else
        {
            // Die Quelle meldet zum vorläufigen Text keine Zeit; P5 sichert ihn nie (Anhang B)
            var segment = new TextSegment(preview.UtteranceId, text, SegmentState.Provisional, TimeSpan.Zero, TimeSpan.Zero);
            if (index >= 0)
            {
                previews[index] = segment;
            }
            else
            {
                previews.Add(segment);
            }
        }

        return [new TextOutput(new RecognitionUpdate([], [.. previews]))];
    }

    private (List<DictationOutput> Outputs, bool Stop) Complete(Utterance utterance, List<TextSegment> previews)
    {
        var hadPreview = previews.RemoveAll(segment => segment.Id == utterance.Id) > 0;
        var steps = _interpreter.Interpret(utterance, _scope());
        var outputs = new List<DictationOutput>();
        var shown = false;
        foreach (var step in steps)
        {
            switch (step)
            {
                case InsertText insert:
                    outputs.Add(new TextOutput(new RecognitionUpdate([insert.Segment], [.. previews])));
                    shown = true;
                    break;

                case RunCommand { Command.Effect: CommandEffect.Cancel } cancel:
                    // Erst die Vorschau leeren, dann genau der Befehl, danach nichts mehr (Kapitel 5.4, T17, T55)
                    previews.Clear();
                    outputs.Add(new TextOutput(new RecognitionUpdate([], [])));
                    outputs.Add(new CommandOutput(cancel));
                    return (outputs, true);

                case RunCommand command:
                    outputs.Add(new CommandOutput(command));
                    break;

                case CommandHint hint when utterance.Words.Count > 0:
                    // Ohne Wörter weder Text noch Hinweis (T51)
                    outputs.Add(new HintOutput(hint, null));
                    break;
            }
        }

        if (hadPreview && !shown)
        {
            // Eine Befehls- oder Hinweisäusserung hat keinen festen Abschnitt; ihre Vorschau verschwindet (T41)
            outputs.Insert(0, new TextOutput(new RecognitionUpdate([], [.. previews])));
        }

        return (outputs, false);
    }
}
