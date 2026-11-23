using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;

namespace Freihand.Core.Live;

/// <summary>
/// Quellen über <see cref="IRecognitionEngine"/> ohne Wort-Hypothesen (P3-Konzept, Kapitel 3, Andere Quellen). Feste
/// Abschnitte werden wortweise auf Äusserungen verteilt; eine Äusserung schliesst erst, wenn ihre Grenze feststeht und
/// die Quelle bis dorthin bestätigt (<see cref="RecognitionUpdate.CompleteUntil"/>) oder ihren Auftrag beendet hat. Die
/// Filter aus Kapitel 5.5 gelten hier nicht.
/// </summary>
public sealed class EngineTranscription : ITranscriptSource
{
    /// <summary>Höchstens so viel Audio erhält ein Detektoraufruf (Kapitel 3, Speichergrenze; T69).</summary>
    internal static readonly TimeSpan DetectorWindow = TimeSpan.FromSeconds(30);

    /// <summary>Warnung ab diesem Rückstand (ER-05, wie Vertrag 4).</summary>
    internal static readonly TimeSpan BacklogWarning = TimeSpan.FromSeconds(10);

    /// <summary>Höchstens so viel unbestätigtes oder noch nicht übergebenes Audio, dann keine Annahme mehr (ER-05).</summary>
    internal static readonly TimeSpan BacklogLimit = TimeSpan.FromSeconds(120);

    private readonly IRecognitionEngine _engine;
    private readonly ISpeechDetector _detector;
    private readonly RecognitionOptions _recognition;
    private readonly TimeSpan _pauseThreshold;
    private readonly Lock _gate = new();
    private readonly Channel<TranscriptEvent> _events =
        Channel.CreateUnbounded<TranscriptEvent>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Channel<AudioChunk> _toEngine =
        Channel.CreateUnbounded<AudioChunk>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly AudioBuffer _buffer = new();
    private readonly SpeechMap _speech = new();

    // Abschnitte zwischen den Grenzen, lückenlos und in Reihenfolge; der letzte ist offen
    private readonly List<Segment> _segments = [new(TimeSpan.Zero)];

    // Tatsächlich angenommene Audiointervalle, die noch nicht verarbeitet sind (ohne Lücken)
    private readonly List<(TimeSpan Start, TimeSpan End)> _spans = [];
    private TimeSpan _heldStart;
    private readonly Queue<TimeSpan> _queuedStarts = new();
    private TimeSpan? _completeUntil;
    private TimeSpan _processedUntil;
    private long _acceptedTicks;
    private long _processed;
    private long _queuedSamples;
    private int _started;
    private bool _audioEnded;
    private bool _engineEnded;
    private bool _warned;
    private bool _full;
    private bool _closed;

    // Der Lauf endete regulär mit dem Ende der Erkennung; das ist kein Abbruch (Z01)
    private bool _finished;
    private CancellationTokenSource? _acceptStop;
    private ExceptionDispatchInfo? _failure;
    private CancellationTokenSource? _stop;

    public EngineTranscription(
        IRecognitionEngine engine, ISpeechDetector detector, RecognitionOptions recognition, TimeSpan pauseThreshold)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(recognition);
        if (pauseThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pauseThreshold), pauseThreshold, "Die Pausenschwelle muss positiv sein.");
        }

        _engine = engine;
        _detector = detector;
        _recognition = recognition;
        _pauseThreshold = pauseThreshold;
    }

    public TimeSpan AcceptedEnd => TimeSpan.FromTicks(Interlocked.Read(ref _acceptedTicks));

    /// <summary>
    /// Angenommenes Audio, für das die Quelle noch nicht bestätigt hat, dass sie fertig ist, oder das sie noch nicht
    /// abgeholt hat (ER-05; Kapitel 3, Speichergrenze).
    /// </summary>
    public TimeSpan Backlog
    {
        get
        {
            lock (_gate)
            {
                return Held();
            }
        }
    }

    /// <summary>
    /// Testanschluss als Verarbeitungsbarriere (P3-Konzept, Kapitel 6): Anzahl der Updates der Erkennung, die P3
    /// vollständig verarbeitet hat. Sinkt nie und zählt in der Reihenfolge der Updates.
    /// </summary>
    internal long ProcessedResults => Interlocked.Read(ref _processed);

    private TimeSpan Accepted => PcmAudio.ToTime(_buffer.EndSample);

    private TimeSpan BufferStart => PcmAudio.ToTime(_buffer.StartSample);

    private Segment Open => _segments[^1];

    public async IAsyncEnumerable<TranscriptEvent> RunAsync(
        IAsyncEnumerable<AudioChunk> audio,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("Je Diktat eine Instanz: RunAsync läuft nur einmal.");
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Volatile.Write(ref _stop, stop);

        // Eigenes Abbruchsignal der Annahme: Endet die Erkennung regulär vor dem Audio, wird nur das Lesen des Audios
        // abgebrochen, nicht die reguläre Freigabe der Engine (Z01)
        using var acceptStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        Volatile.Write(ref _acceptStop, acceptStop);
        var accept = Task.Run(() => AcceptAsync(audio, acceptStop.Token, stop.Token), CancellationToken.None);
        var recognize = Task.Run(() => RecognizeAsync(stop.Token), CancellationToken.None);
        var done = CompleteAfterAsync(accept, recognize);
        try
        {
            var reader = _events.Reader;
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var next))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return next;
                }
            }

            ExceptionDispatchInfo? failure;
            lock (_gate)
            {
                failure = _failure;
            }

            failure?.Throw();
        }
        finally
        {
            lock (_gate)
            {
                _closed = true;
            }

            await stop.CancelAsync().ConfigureAwait(false);
            await done.ConfigureAwait(false);
        }
    }

    public void MarkBoundary(TimeSpan at)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            // Grenze bei AcceptedEnd; auch ohne Inhalt, damit spät gelieferte Wörter davor ihren Platz haben (T65)
            var boundary = SpeechRegions.Min(at, Accepted);
            if (boundary > Open.Lower)
            {
                Close(boundary, UtteranceEnd.CommandKey);
                Reevaluate();
                TryComplete();
                UpdateBacklog();
            }
        }
    }

    private async Task CompleteAfterAsync(Task accept, Task recognize)
    {
        try
        {
            await Task.WhenAll(accept, recognize).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _failure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    // ---- Annahme -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Annahme: Die Audioquelle gehört genau dieser Aufgabe (<see cref="CancellableEnumerator{T}.ConsumeAsync"/>). Jeder
    /// Fehler beendet den Lauf, bevor die Quelle freigegeben wird (R02).
    /// </summary>
    /// <param name="token">Beendet das Lesen, auch wenn die Erkennung regulär vor dem Audio endet.</param>
    /// <param name="runToken">Abbruch oder Fehler des Laufs; nur er beendet das Warten auf eine reguläre Freigabe (W01).</param>
    private async Task AcceptAsync(IAsyncEnumerable<AudioChunk> audio, CancellationToken token, CancellationToken runToken)
    {
        await CancellableEnumerator<AudioChunk>.ConsumeAsync(
            _ => audio,
            token,
            runToken,
            chunks => PumpAsync(chunks, token),
            Failed,
            IsFailedOrStopped,
            CancelAcceptance,
            ReleaseFailed).ConfigureAwait(false);

        // Die Erkennung erhält kein Audio mehr; ihr Auftrag endet mit dem Audio
        _toEngine.Writer.TryComplete();
        StopIfClosed();
    }

    /// <summary>Lauf gescheitert oder abgebrochen, nicht regulär beendet (Z01)?</summary>
    private bool IsAborted()
    {
        lock (_gate)
        {
            return _closed && !_finished;
        }
    }

    /// <summary>Für die Annahme: Lauf gescheitert oder abgebrochen, oder die Erkennung endete schon vor dem Audio.</summary>
    private bool IsFailedOrStopped()
    {
        lock (_gate)
        {
            return _closed && (!_finished || !_audioEnded);
        }
    }

    private void CancelAcceptance()
    {
        try
        {
            Volatile.Read(ref _acceptStop)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Der Lauf ist schon beendet
        }
    }

    /// <summary>
    /// Eine Freigabe scheiterte nach regulärem Ende: Fehler des Laufs wie jeder andere (Kapitel 5.4, W02). Die erste
    /// Ursache bleibt; eine noch laufende Erkennung wird abgebrochen, statt den Lauf offen zu halten.
    /// </summary>
    private void ReleaseFailed(Exception exception) => Failed(exception);

    /// <summary>Beendet den Lauf mit Fehler und bricht seine Aufgaben ab; die erste Ursache bleibt (Kapitel 5.4).</summary>
    private void Failed(Exception exception)
    {
        lock (_gate)
        {
            Fail(exception);
        }

        StopIfClosed();
    }

    /// <summary>
    /// Liest und nimmt Blöcke an. Erreicht der Rückstand <see cref="BacklogLimit"/>, stoppt P3 die Aufnahme selbst
    /// (ER-05, N02, R01): Es liest keine Blöcke mehr, gibt die Audiofolge frei und übergibt der Quelle das Streamende.
    /// Eine Quelle ohne <c>CompleteUntil</c> wird nur so fertig, denn eine Folge zeigt ihr Ende nur beim Lesen, und
    /// Lesen hiesse annehmen. Der nicht angenommene Rest des laufenden Blocks wird als <c>BufferFull</c> mit seiner
    /// Zeitspanne gemeldet (Regel 2, S04).
    /// </summary>
    private async Task PumpAsync(CancellableEnumerator<AudioChunk> chunks, CancellationToken token)
    {
        while (await chunks.MoveNextAsync(token).ConfigureAwait(false))
        {
            var chunk = chunks.Current;
            lock (_gate)
            {
                AudioChunk? rest = chunk;
                while (rest is { } next && !_full && !_closed)
                {
                    rest = Accept(next);
                }

                if (_closed)
                {
                    return;
                }

                if (_full)
                {
                    if (rest is { } refused)
                    {
                        Refuse(refused);
                    }

                    EndOfAudio();
                    return;
                }
            }
        }

        lock (_gate)
        {
            if (!_closed)
            {
                EndOfAudio();
            }
        }
    }

    /// <summary>Streamende oder Aufnahmestopp: die offene Äusserung schliesst, die Quelle erhält das Streamende.</summary>
    private void EndOfAudio()
    {
        _audioEnded = true;
        Close(Accepted, UtteranceEnd.StreamEnd);
        SetBufferStart(Accepted);
        TryComplete();
        UpdateBacklog();
        _toEngine.Writer.TryComplete();
    }

    /// <summary>Meldet den nicht angenommenen Rest eines Blocks sichtbar statt ihn still zu verwerfen (Regel 2).</summary>
    private void Refuse(AudioChunk chunk)
    {
        PcmAudio.Validate(chunk);
        var start = SpeechRegions.Max(chunk.Offset, Accepted);
        var end = chunk.Offset + PcmAudio.ToTime(PcmAudio.SampleCount(chunk));
        if (end > start)
        {
            Emit(new LiveNotice(LiveNoticeKind.BufferFull, start, end));
        }
    }

    /// <summary>
    /// Nimmt einen Block an, höchstens eine Sekunde und nie über <see cref="BacklogLimit"/> hinaus; der Rest kommt zurück
    /// (U02). <c>Detect</c> sieht höchstens <see cref="DetectorWindow"/>.
    /// </summary>
    private AudioChunk? Accept(AudioChunk chunk)
    {
        PcmAudio.Validate(chunk);
        var count = PcmAudio.SampleCount(chunk);
        var first = PcmAudio.ToSample(chunk.Offset);
        if (count == 0)
        {
            return null;
        }

        if (first > _buffer.EndSample)
        {
            var gapStart = Accepted;
            var gapEnd = PcmAudio.ToTime(first);
            Emit(new LiveNotice(LiveNoticeKind.AudioGap, gapStart, gapEnd));

            Close(gapStart, UtteranceEnd.AudioGap);
            SetBufferStart(gapStart);
            _buffer.Reset(first);
            Interlocked.Exchange(ref _acceptedTicks, gapEnd.Ticks);

            // Durch die Lücke abgeschlossene und schon bestätigte Äusserungen vor der Platzentscheidung abschliessen (N04)
            TryComplete();
        }

        var skip = _buffer.EndSample - first;
        if (skip >= count)
        {
            return null;
        }

        var free = (BacklogLimit - Held()).Ticks / PcmAudio.TicksPerSample;
        var take = (int)Math.Min(Math.Min(count - skip, PcmAudio.SampleRate), Math.Max(free, 0));
        if (take == 0)
        {
            UpdateBacklog();
            return chunk;
        }

        // Nur der neue Teil, als eigene Kopie an die Erkennung (Vertrag 1)
        var start = Accepted;
        var pcm = chunk.Pcm16Mono.Slice((int)(skip * 2), take * 2);
        _buffer.Append(pcm.Span);
        Interlocked.Exchange(ref _acceptedTicks, Accepted.Ticks);
        _queuedSamples += take;
        _queuedStarts.Enqueue(start);
        AddSpan(start, Accepted);
        _toEngine.Writer.TryWrite(new AudioChunk(pcm.ToArray(), PcmAudio.SampleRate, start));
        AudioChunk? rest = skip + take < count
            ? new AudioChunk(chunk.Pcm16Mono[(int)((skip + take) * 2)..], chunk.SampleRate, Accepted)
            : null;

        // Rollendes Detektorfenster: älteres Audio entfällt ohne Äusserungsgrenze; Sprachbeginn und bestätigte
        // Sprachenden bleiben als Bereiche erhalten (Kapitel 3, Speichergrenze; T69)
        SetBufferStart(_buffer.EndSample - (DetectorWindow.Ticks / PcmAudio.TicksPerSample));

        IReadOnlyList<SpeechRegion> regions;
        try
        {
            regions = _detector.Detect(_buffer.Slice(_buffer.StartSample, _buffer.EndSample), BufferStart)
                ?? throw new InvalidOperationException("Der Detektor lieferte kein Ergebnis.");
        }
        catch (Exception exception)
        {
            // Detektorfehler (Kapitel 5.4, T68): ohne Wiederholung, mit den schon gelieferten festen Deltas
            var lossStart = CompleteAfterFailure(UtteranceEnd.DetectorFailed);
            Emit(new LiveNotice(LiveNoticeKind.DetectorFailed, lossStart, SpeechRegions.Max(lossStart, Accepted)));
            Fail(new InvalidOperationException("Die Sprachbereichserkennung ist ausgefallen (Kapitel 5.4).", exception));
            return null;
        }

        _speech.SetRecent(regions, BufferStart, Accepted);
        Reevaluate();
        TryComplete();
        UpdateBacklog();
        return rest;
    }

    /// <summary>Sprachbeginn und Pausengrenze des offenen Abschnitts nach dem Detektor (Kapitel 3, 5.1).</summary>
    private void Reevaluate()
    {
        while (true)
        {
            var open = Open;
            if (open.SpeechStart is null)
            {
                if (_audioEnded || FirstSpeech(open.Lower, Accepted) is not { } speechStart)
                {
                    // Bestätigte Nicht-Sprache wird freigegeben (Regel 5)
                    SetBufferStart(SpeechRegions.Max(open.Lower, Accepted - _pauseThreshold));
                    return;
                }

                open.SpeechStart = speechStart;
                SetBufferStart(speechStart);
            }

            if (PauseAfter(open.SpeechStart.Value) is not { } speechEnd)
            {
                return;
            }

            Close(speechEnd, UtteranceEnd.Pause);
            SetBufferStart(speechEnd);
        }
    }

    private TimeSpan? PauseAfter(TimeSpan from)
    {
        TimeSpan? speechEnd = null;
        foreach (var region in _speech.Merged)
        {
            if (region.End <= from)
            {
                continue;
            }

            if (speechEnd is { } end && region.Start - end >= _pauseThreshold)
            {
                return end;
            }

            speechEnd = speechEnd is { } previous ? SpeechRegions.Max(previous, region.End) : region.End;
        }

        var last = speechEnd ?? from;
        return last < Accepted && Accepted - last >= _pauseThreshold ? last : null;
    }

    private TimeSpan? FirstSpeech(TimeSpan from, TimeSpan to)
    {
        foreach (var region in _speech.Merged)
        {
            if (region.End > from && region.Start < to)
            {
                return SpeechRegions.Max(region.Start, from);
            }
        }

        return null;
    }

    /// <summary>Setzt die Grenze des offenen Abschnitts; dahinter beginnt ein neuer offener Abschnitt.</summary>
    private void Close(TimeSpan boundary, UtteranceEnd ending)
    {
        var open = Open;
        boundary = SpeechRegions.Max(boundary, open.Lower);
        open.Close(boundary, ending);
        _segments.Add(new Segment(boundary));
    }

    private void SetBufferStart(TimeSpan start) => SetBufferStart(PcmAudio.ToSample(start));

    private void SetBufferStart(long start)
    {
        var sample = Math.Clamp(start, _buffer.StartSample, _buffer.EndSample);
        if (sample > _buffer.StartSample)
        {
            var time = PcmAudio.ToTime(sample);
            _speech.Settle(time);
            _buffer.ReleaseUntil(sample);

            // Für Sprachbeginn und Pause genügen die Bereiche ab der letzten Grenze, in Sprache die jüngsten: Lücken
            // zwischen älteren Bereichen sind schon ausgewertet
            _speech.Prune(Open.SpeechStart is null ? SpeechRegions.Min(Open.Lower, time) : time - _pauseThreshold);
        }
    }

    /// <summary>
    /// Gehalten (Kapitel 3, Speichergrenze; N04): tatsächlich angenommenes Audio nach dem Verarbeitungsstand oder, falls
    /// mehr, noch nicht an die Quelle übergebenes Audio. Lücken zählen nicht, weil dort kein Audio liegt.
    /// </summary>
    private TimeSpan Held()
    {
        var done = SpeechRegions.Max(_processedUntil, SpeechRegions.Min(_completeUntil ?? TimeSpan.Zero, Accepted));
        if (_segments.All(segment => !segment.HasContent))
        {
            // Ohne Äusserung ist freigegebene Nicht-Sprache abschliessend verarbeitet (Regel 5)
            done = SpeechRegions.Max(done, BufferStart);
        }

        _spans.RemoveAll(span => span.End <= done);

        // Ältestes gehaltenes Audio aus beiden Mengen: unbestätigt oder noch nicht abgeholt (S05, R04)
        _heldStart = _spans.Count > 0 ? SpeechRegions.Max(_spans[0].Start, done) : Accepted;
        if (_queuedStarts.TryPeek(out var queuedStart) && queuedStart < _heldStart)
        {
            _heldStart = queuedStart;
        }

        var open = _spans.Aggregate(
            TimeSpan.Zero,
            (sum, span) => sum + (span.End - SpeechRegions.Max(span.Start, done)));
        var queued = TimeSpan.FromTicks(_queuedSamples * PcmAudio.TicksPerSample);
        return SpeechRegions.Max(open, queued);
    }

    /// <summary>Führt die angenommenen Audiointervalle nach; ein neues beginnt nach einer Lücke.</summary>
    private void AddSpan(TimeSpan start, TimeSpan end)
    {
        if (_spans.Count > 0 && _spans[^1].End == start)
        {
            _spans[^1] = (_spans[^1].Start, end);
        }
        else
        {
            _spans.Add((start, end));
        }
    }

    /// <summary>Rückstand melden; an der Grenze <c>BufferFull</c> und keine Annahme mehr (ER-05, Regel 6).</summary>
    private void UpdateBacklog()
    {
        var held = Held();

        // Beginn des ältesten gehaltenen Audios, nicht mitten in einer Lücke (S05)
        var start = _heldStart;
        if (!_warned && held >= BacklogWarning)
        {
            _warned = true;
            Emit(new LiveNotice(LiveNoticeKind.BacklogWarning, start, Accepted));
        }
        else if (_warned && held < BacklogWarning)
        {
            _warned = false;
            Emit(new LiveNotice(LiveNoticeKind.BacklogCleared, start, Accepted));
        }

        var full = BacklogLimit - held < TimeSpan.FromTicks(PcmAudio.TicksPerSample);
        if (full && !_full)
        {
            Emit(new LiveNotice(LiveNoticeKind.BufferFull, start, Accepted));
        }

        _full = full;
    }

    // ---- Erkennung -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Erkennung: Die Folge der Engine gehört genau dieser Aufgabe (<see cref="CancellableEnumerator{T}.ConsumeAsync"/>).
    /// Eine Engine, die den Abbruch übergeht oder beim Lesen wirft, wird wie ein solcher Decoder verworfen (U03, R02).
    /// </summary>
    private Task RecognizeAsync(CancellationToken token) =>
        CancellableEnumerator<RecognitionUpdate>.ConsumeAsync(
            open => _engine.RecognizeAsync(EngineAudioAsync(open), _recognition, open),
            token,
            token,
            updates => ReadUpdatesAsync(updates, token),
            EngineFailed,
            IsAborted,
            () => Volatile.Read(ref _stop)?.Cancel(),
            ReleaseFailed);

    private async Task ReadUpdatesAsync(CancellableEnumerator<RecognitionUpdate> updates, CancellationToken token)
    {
        while (await updates.MoveNextAsync(token).ConfigureAwait(false))
        {
            var update = updates.Current;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                Apply(update);
                UpdateBacklog();
            }

            Interlocked.Increment(ref _processed);
            if (StopIfClosed())
            {
                // Nach einem Vertragsverstoss nicht mehr lesen; die Quelle wird ohne Warten freigegeben
                return;
            }
        }

        lock (_gate)
        {
            if (!_closed)
            {
                // Der Erkennungsauftrag ist vollständig beendet: alle Äusserungen schliessen (Kapitel 3)
                _engineEnded = true;
                if (!_audioEnded)
                {
                    Close(Accepted, UtteranceEnd.StreamEnd);
                }

                TryComplete();

                // Ohne Erkennung entsteht kein Text mehr; weiteres Audio wird nicht mehr angenommen. Das ist ein
                // reguläres Ende: Die Engine wird mit Warten freigegeben, nur das Lesen des Audios endet (Z01)
                _closed = true;
                _finished = true;
            }
        }

        CancelAcceptance();
    }

    /// <summary>Endgültiger Fehler der Quelle, ohne Wiederholung (Kapitel 5.4); beendet den Lauf vor ihrer Freigabe.</summary>
    private void EngineFailed(Exception exception)
    {
        lock (_gate)
        {
            if (!_closed)
            {
                var lossStart = CompleteAfterFailure(UtteranceEnd.DecoderFailed);
                Emit(new LiveNotice(LiveNoticeKind.DecoderFailed, lossStart, SpeechRegions.Max(lossStart, Accepted)));
                Fail(new InvalidOperationException("Die Erkennung ist ausgefallen; der gemeldete Bereich fehlt (Kapitel 5.4).", exception));
            }
        }

        StopIfClosed();
    }

    /// <summary>Audio an die Erkennung; was sie abgeholt hat, zählt nicht mehr zur offenen Übergabe.</summary>
    private async IAsyncEnumerable<AudioChunk> EngineAudioAsync([EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var chunk in _toEngine.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            lock (_gate)
            {
                _queuedSamples -= PcmAudio.SampleCount(chunk);
                _queuedStarts.TryDequeue(out _);
                if (!_closed)
                {
                    UpdateBacklog();
                }
            }

            yield return chunk;
        }
    }

    /// <summary>Verarbeitet ein Update: feste Abschnitte prüfen und wortweise zuordnen, Zusage nachführen, Vorschau.</summary>
    private void Apply(RecognitionUpdate? update)
    {
        if (update?.Fixed is null || update.Provisional is null)
        {
            InvalidSegment(null, "Die Quelle lieferte ein leeres Update.");
            return;
        }

        // Ein fester Abschnitt beginnt nicht vor der grössten Zusage früherer Updates (Kapitel 3, T66)
        var promised = _completeUntil;
        foreach (var segment in update.Fixed)
        {
            var words = segment is null ? [] : Split(segment);
            if (segment is null || segment.Start >= segment.End || words.Count == 0 || (promised is { } until && segment.Start < until))
            {
                InvalidSegment(segment, "Die Quelle lieferte einen ungültigen festen Abschnitt.");
                return;
            }

            foreach (var word in words)
            {
                SegmentAt(word.Start).Add(word);
            }
        }

        // Die Zusage sinkt nie: P3 rechnet mit dem grössten bisher gemeldeten Wert (H02, T66)
        if (update.CompleteUntil is { } complete && (_completeUntil is null || complete > _completeUntil))
        {
            _completeUntil = complete;
        }

        ShowPreviews(update.Provisional);
        TryComplete();
    }

    /// <summary>
    /// Teilt einen Abschnitt am Leerraum in Wörter, gleichmässig über seine Zeitspanne, auf ganze Ticks abgerundet, mit
    /// Wahrscheinlichkeit 1; das letzte Wort endet genau am Ende des Abschnitts (Befund H04).
    /// </summary>
    private static List<RecognizedWord> Split(TextSegment segment)
    {
        var parts = (segment.Text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var from = segment.Start.Ticks;
        var span = segment.End.Ticks - from;
        return [.. parts.Select((part, i) => new RecognizedWord(
            part,
            TimeSpan.FromTicks(from + (i * span / parts.Length)),
            TimeSpan.FromTicks(from + ((i + 1) * span / parts.Length)),
            1f))];
    }

    /// <summary>Der Abschnitt, zwischen dessen Grenzen diese Zeit liegt (Kapitel 5.1).</summary>
    private Segment SegmentAt(TimeSpan time)
    {
        foreach (var segment in _segments)
        {
            if (segment.Upper is not { } upper || time < upper)
            {
                return segment;
            }
        }

        return Open;
    }

    private void ShowPreviews(IReadOnlyList<TextSegment> provisional)
    {
        var shown = _segments.ToDictionary(segment => segment, _ => new List<RecognizedWord>());
        foreach (var segment in provisional.Where(segment => segment is not null && segment.Start <= segment.End))
        {
            foreach (var word in Split(segment))
            {
                shown[SegmentAt(word.Start)].Add(word);
            }
        }

        foreach (var (segment, words) in shown)
        {
            var text = TextJoin.Join(words.Select(word => word.Text));
            if (text != segment.Preview)
            {
                segment.Preview = text;
                Emit(new ProvisionalText(segment.Id, text));
            }
        }
    }

    /// <summary>
    /// Schliesst Abschnitte in Reihenfolge ab, deren Grenze feststeht und die die Quelle bis dorthin bestätigt oder deren
    /// Auftrag beendet ist. Ein Abschnitt ohne Sprache und ohne Wörter ergibt keine Äusserung.
    /// </summary>
    private void TryComplete()
    {
        while (_segments.Count > 0)
        {
            var head = _segments[0];
            if (head.Upper is not { } upper
                || !(upper <= head.Lower || _engineEnded || (_completeUntil is { } until && until >= upper)))
            {
                return;
            }

            _segments.RemoveAt(0);
            _processedUntil = SpeechRegions.Max(_processedUntil, upper);
            if (head.HasContent)
            {
                Emit(new UtteranceCompleted(head.ToUtterance(upper, head.Ending)));
            }
            else if (head.Preview.Length > 0)
            {
                Emit(new ProvisionalText(head.Id, string.Empty));
            }
        }
    }

    // ---- Fehler --------------------------------------------------------------------------------------------------

    /// <summary>Ungültiger fester Abschnitt (Kapitel 3, T66): nicht übernommen, Meldung mit seiner eigenen Zeitspanne.</summary>
    private void InvalidSegment(TextSegment? segment, string message)
    {
        CompleteAfterFailure(UtteranceEnd.DecoderFailed);
        var start = segment?.Start ?? Accepted;
        var end = segment?.End ?? Accepted;
        Emit(new LiveNotice(LiveNoticeKind.DecoderFailed, start, SpeechRegions.Max(start, end)));
        Fail(new InvalidOperationException(message));
    }

    /// <summary>
    /// Schliesst die noch nicht abgeschlossenen Äusserungen mit den schon gelieferten festen Deltas ab und liefert den
    /// Beginn der Verlustspanne: Ende des letzten so abgeschlossenen Worts, ohne solche Wörter Beginn der ältesten
    /// offenen Äusserung, ohne offene Äusserung das Ende des zuletzt abschliessend verarbeiteten Audios.
    /// </summary>
    private TimeSpan CompleteAfterFailure(UtteranceEnd ending)
    {
        TimeSpan? lastWord = null;
        TimeSpan? firstOpen = null;
        foreach (var segment in _segments.Where(segment => segment.HasContent))
        {
            var utterance = segment.ToUtterance(null, ending);
            Emit(new UtteranceCompleted(utterance));
            firstOpen ??= utterance.Start;
            if (utterance.Words.Count > 0)
            {
                var end = utterance.Words.Max(word => word.End);
                lastWord = lastWord is { } previous ? SpeechRegions.Max(previous, end) : end;
            }
        }

        _segments.Clear();
        return lastWord ?? firstOpen ?? SpeechRegions.Max(_processedUntil, BufferStart);
    }

    private void Fail(Exception exception)
    {
        _failure ??= ExceptionDispatchInfo.Capture(exception);
        _closed = true;

        // Ein Fehler hat Vorrang vor einem früheren regulären Ende (Y01): Scheitert eine Freigabe nach dem Ende der
        // Engine, ist der Lauf ein Fehlerlauf, und das Warten auf die andere Freigabe wird abgebrochen
        _finished = false;
    }

    /// <summary>Bricht den Lauf ab, wenn er gescheitert oder abgebrochen ist; liefert, ob er beendet ist (Z01).</summary>
    private bool StopIfClosed()
    {
        bool closed;
        bool aborted;
        lock (_gate)
        {
            closed = _closed;
            aborted = _closed && !_finished;
        }

        if (aborted)
        {
            try
            {
                Volatile.Read(ref _stop)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Der Lauf ist schon beendet
            }
        }

        return closed;
    }

    private void Emit(TranscriptEvent transcriptEvent)
    {
        if (!_closed)
        {
            _events.Writer.TryWrite(transcriptEvent);
        }
    }

    /// <summary>Audio zwischen zwei Grenzen und die Wörter, die darin beginnen.</summary>
    private sealed class Segment(TimeSpan lower)
    {
        private readonly List<RecognizedWord> _words = [];

        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>Vorherige Grenze; Wörter ab hier gehören hierher.</summary>
        public TimeSpan Lower { get; } = lower;

        /// <summary>Grenze am Ende, sobald sie feststeht.</summary>
        public TimeSpan? Upper { get; private set; }

        public UtteranceEnd Ending { get; private set; }

        /// <summary>Beginn der ersten Sprache nach <see cref="Lower"/>.</summary>
        public TimeSpan? SpeechStart { get; set; }

        public string Preview { get; set; } = string.Empty;

        /// <summary>Eine Äusserung entsteht nur mit Sprache oder Wörtern.</summary>
        public bool HasContent => SpeechStart is not null || _words.Count > 0;

        public void Close(TimeSpan upper, UtteranceEnd ending)
        {
            Upper = upper;
            Ending = ending;
        }

        /// <summary>Innerhalb der Äusserung nach Beginn, bei gleichem Beginn in der Reihenfolge der Lieferung.</summary>
        public void Add(RecognizedWord word)
        {
            var index = _words.FindLastIndex(existing => existing.Start <= word.Start);
            _words.Insert(index + 1, word);
        }

        public Utterance ToUtterance(TimeSpan? end, UtteranceEnd ending)
        {
            var start = SpeechStart ?? (_words.Count > 0 ? _words[0].Start : Lower);
            if (_words.Count > 0 && _words[0].Start < start)
            {
                start = _words[0].Start;
            }

            var last = end ?? Upper ?? (_words.Count > 0 ? _words.Max(word => word.End) : start);
            return new Utterance(Id, [.. _words], start, last, ending);
        }
    }
}
