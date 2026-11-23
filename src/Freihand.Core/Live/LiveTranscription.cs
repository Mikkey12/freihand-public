using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;

namespace Freihand.Core.Live;

/// <summary>
/// Lokale Erkennung mit Wortzeiten, live oder satzweise (P3-Konzept, Kapitel 5). Die Annahme der Blöcke und die
/// Decoderarbeit laufen auf getrennten Aufgaben (Kapitel 5.2 und 6); beide teilen einen Zustand unter einer Sperre.
/// Decoder und Detektor gehören P8 und werden hier nicht freigegeben.
/// </summary>
public sealed class LiveTranscription : ITranscriptSource
{
    private readonly IHypothesisDecoder _decoder;
    private readonly ISpeechDetector _detector;
    private readonly RecognitionOptions _recognition;
    private readonly LiveOptions _options;
    private readonly HallucinationGuard _guard;
    private readonly Lock _gate = new();
    private readonly Channel<TranscriptEvent> _events =
        Channel.CreateUnbounded<TranscriptEvent>(new UnboundedChannelOptions { SingleReader = true });

    private readonly AsyncSignal _workSignal = new();
    private readonly AsyncSignal _acceptSignal = new();
    private readonly AudioBuffer _buffer = new();
    private readonly SpeechMap _speech = new();

    // Noch nicht abgeschlossene Äusserungen in Reihenfolge; nur die letzte kann offen sein (ohne Grenze)
    private readonly List<LiveUtterance> _utterances = [];
    private TimeSpan _lastBoundary;
    private long _acceptedTicks;
    private long _processed;
    private long _invalidHypotheses;
    private int _started;
    private bool _audioEnded;
    private bool _closed;
    private bool _busy;
    private Task? _decoding;
    private LiveUtterance? _stepFor;
    private bool _warned;
    private bool _full;
    private ExceptionDispatchInfo? _failure;
    private CancellationTokenSource? _stop;

    public LiveTranscription(
        IHypothesisDecoder decoder, ISpeechDetector detector, RecognitionOptions recognition, LiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(recognition);
        ArgumentNullException.ThrowIfNull(options);
        CheckOptions(options);

        _decoder = decoder;
        _detector = detector;
        _recognition = recognition;
        _options = options;
        _guard = new HallucinationGuard(options.Filter, options.PauseThreshold);
    }

    public TimeSpan AcceptedEnd => TimeSpan.FromTicks(Interlocked.Read(ref _acceptedTicks));

    /// <summary>Angenommenes, noch nicht abschliessend verarbeitetes Audio, ohne die Kopie eines laufenden Decodes (T67).</summary>
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
    /// Testanschluss als Verarbeitungsbarriere (P3-Konzept, Kapitel 6): Anzahl der zurückgekehrten Decode-Aufrufe, deren
    /// Ergebnis oder Fehler P3 vollständig verarbeitet hat. Sinkt nie und zählt in der Reihenfolge der Aufrufe.
    /// </summary>
    internal long ProcessedResults => Interlocked.Read(ref _processed);

    /// <summary>Anzahl der von P3 verworfenen Hypothesen, einschliesslich erfolgreicher Wiederholungen (RM01).</summary>
    internal long InvalidHypotheses => Interlocked.Read(ref _invalidHypotheses);

    private bool IsLive => _options.Mode == LiveMode.Live;

    private TimeSpan Accepted => PcmAudio.ToTime(_buffer.EndSample);

    private TimeSpan BufferStart => PcmAudio.ToTime(_buffer.StartSample);

    private LiveUtterance? Open => _utterances.Count > 0 && _utterances[^1].Boundary is null ? _utterances[^1] : null;

    /// <summary>Ein Aufruf von <c>DecodeAsync</c> ist noch nicht zurückgekehrt (Kapitel 5.2, A01).</summary>
    private bool DecoderBusy => _busy && (_decoding is null || !_decoding.IsCompleted);

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
        var accept = Task.Run(() => AcceptAsync(audio, stop.Token), CancellationToken.None);
        var work = Task.Run(() => WorkAsync(stop.Token), CancellationToken.None);
        var done = CompleteAfterAsync(accept, work);
        try
        {
            var reader = _events.Reader;
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var next))
                {
                    // Nach einem Abbruch keine Ausgabe mehr, auch nicht aus schon bereitliegenden Ereignissen (BE-06)
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
                _workSignal.Set();
                _acceptSignal.Set();
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

            // Grenze am Ende des angenommenen Audios beim Tastendruck, nie danach (Vertrag 6)
            var boundary = SpeechRegions.Min(at, Accepted);
            if (Open is { } open && boundary > open.Start)
            {
                CloseOpen(boundary, UtteranceEnd.CommandKey);
            }
            else
            {
                _lastBoundary = SpeechRegions.Max(_lastBoundary, boundary);
            }

            Reevaluate();
            UpdateBacklog();
        }

        _workSignal.Set();
    }

    private static void CheckOptions(LiveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Filter);
        ArgumentNullException.ThrowIfNull(options.Filter.KnownPhrases);
        if (!Enum.IsDefined(options.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Mode, "Unbekannter Erkennungsmodus.");
        }

        Positive(options.Step, nameof(options.Step));
        Positive(options.Window, nameof(options.Window));
        Positive(options.PauseThreshold, nameof(options.PauseThreshold));
        Positive(options.MaxUtterance, nameof(options.MaxUtterance));
        Positive(options.CutGuard, nameof(options.CutGuard));
        Positive(options.BacklogWarning, nameof(options.BacklogWarning));
        Positive(options.BacklogLimit, nameof(options.BacklogLimit));
        Reject(options.Window < options.Step + options.PauseThreshold, "Window ist kleiner als Step plus PauseThreshold.");
        Reject(options.CutGuard >= options.MaxUtterance, "CutGuard ist nicht kleiner als MaxUtterance.");
        Reject(options.CutGuard >= options.Window, "CutGuard ist nicht kleiner als Window.");
        Reject(options.BacklogWarning >= options.BacklogLimit, "BacklogWarning ist nicht kleiner als BacklogLimit.");
        Reject(
            options.BacklogLimit < options.MaxUtterance + options.PauseThreshold,
            "BacklogLimit ist kleiner als MaxUtterance plus PauseThreshold.");
        // Live übernimmt nur am Längenschnitt; ein kürzeres Window würde K vorher verschieben (E1, P3 Kapitel 4)
        Reject(
            options.Mode == LiveMode.Live && options.Window < options.MaxUtterance,
            "Window ist live kleiner als MaxUtterance.");
        var probability = options.Filter.MaxPhraseProbability;
        Reject(!(probability >= 0 && probability <= 1), "MaxPhraseProbability liegt nicht in [0, 1].");

        static void Positive(TimeSpan value, string name) =>
            Reject(value <= TimeSpan.Zero, $"{name} muss positiv sein.");

        static void Reject(bool invalid, string message)
        {
            if (invalid)
            {
                throw new ArgumentOutOfRangeException(nameof(options), message);
            }
        }
    }

    private async Task CompleteAfterAsync(Task accept, Task work)
    {
        try
        {
            await Task.WhenAll(accept, work).ConfigureAwait(false);
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
    /// Fehler beendet den Lauf, bevor die Quelle freigegeben wird; auch eine Quelle, die den Abbruch übergeht oder deren
    /// Freigabe hängt, hält ihn nicht auf (U03, N01, R02).
    /// </summary>
    private async Task AcceptAsync(IAsyncEnumerable<AudioChunk> audio, CancellationToken token)
    {
        await CancellableEnumerator<AudioChunk>.ConsumeAsync(
            _ => audio,
            token,
            token,
            chunks => PumpAsync(chunks, token),
            Failed,
            IsClosed,
            () => Volatile.Read(ref _stop)?.Cancel(),
            ReleaseFailed).ConfigureAwait(false);
        _workSignal.Set();
    }

    /// <summary>Lauf gescheitert oder abgebrochen? Live endet regulär nur mit dem Streamende.</summary>
    private bool IsClosed()
    {
        lock (_gate)
        {
            return _closed;
        }
    }

    /// <summary>
    /// Die Freigabe der Audioquelle scheiterte nach regulärem Ende: Fehler des Laufs wie jeder andere (Kapitel 5.4, W02).
    /// Die erste Ursache bleibt; noch laufende Decoderarbeit wird abgebrochen, statt den Lauf offen zu halten.
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
    /// Holt Blöcke ab und nimmt sie an (Vertrag 1, Regel 6). Bei vollem Puffer holt P3 nichts mehr ab (T31): Die
    /// Decoderarbeit schafft ohne Streamende Platz, denn ein voller Puffer enthält immer einen Längenschnitt oder eine
    /// abgeschlossene Äusserung. Der Rest eines grossen Blocks wartet und wird angenommen, sobald Platz ist (U02).
    /// </summary>
    private async Task PumpAsync(CancellableEnumerator<AudioChunk> chunks, CancellationToken token)
    {
        var waiting = new List<AudioChunk>();
        var ended = false;
        while (true)
        {
            bool full;
            lock (_gate)
            {
                if (_closed || !AcceptWaiting(waiting))
                {
                    return;
                }

                if (ended && waiting.Count == 0)
                {
                    EndOfAudio();
                    return;
                }

                full = _full;
            }

            _workSignal.Set();
            if (ended || full)
            {
                // Puffer voll (ER-05): keine Annahme, bis die Erkennung wieder Platz geschaffen hat
                if (!await WaitForSpaceAsync(token).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            if (await chunks.MoveNextAsync(token).ConfigureAwait(false))
            {
                waiting.Add(chunks.Current);
            }
            else
            {
                ended = true;
            }
        }
    }

    /// <summary>Nimmt wartende Blöcke an, solange Platz ist; falsch, wenn der Lauf dabei endete.</summary>
    private bool AcceptWaiting(List<AudioChunk> waiting)
    {
        while (!_full && waiting.Count > 0)
        {
            // Ein grosser Block wird in Teilen übernommen, nie über die Puffergrenze hinaus (U02)
            var rest = Accept(waiting[0]);
            if (_closed)
            {
                return false;
            }

            if (rest is { } remaining)
            {
                waiting[0] = remaining;
            }
            else
            {
                waiting.RemoveAt(0);
            }
        }

        return true;
    }

    private async Task<bool> WaitForSpaceAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_closed)
                {
                    return false;
                }

                if (!_full)
                {
                    return true;
                }

                wait = _acceptSignal.Next;
            }

            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Nimmt einen Block an (Vertrag 1): nur den neuen Teil, eine Lücke wird gemeldet und ist eine Grenze. Übernommen
    /// wird höchstens eine Sekunde und nie mehr, als der Puffer bis <c>BacklogLimit</c> noch fasst (Regel 6); der Rest
    /// kommt zurück und wird angenommen, sobald wieder Platz ist.
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
            Gap(first);
        }

        var skip = _buffer.EndSample - first;
        if (skip >= count)
        {
            // Wiederanlauf: schon angenommenes Audio wird nicht noch einmal genommen (T41, T49)
            return null;
        }

        var free = (_options.BacklogLimit - Held()).Ticks / PcmAudio.TicksPerSample;
        var take = (int)Math.Min(Math.Min(count - skip, PcmAudio.SampleRate), Math.Max(free, 0));
        if (take == 0)
        {
            UpdateBacklog();
            return chunk;
        }

        var previousEnd = Accepted;
        _buffer.Append(chunk.Pcm16Mono.Span.Slice((int)(skip * 2), take * 2));
        Interlocked.Exchange(ref _acceptedTicks, Accepted.Ticks);
        AudioChunk? rest = skip + take < count
            ? new AudioChunk(chunk.Pcm16Mono[(int)((skip + take) * 2)..], chunk.SampleRate, Accepted)
            : null;

        IReadOnlyList<SpeechRegion> regions;
        try
        {
            regions = _detector.Detect(_buffer.Slice(_buffer.StartSample, _buffer.EndSample), BufferStart)
                ?? throw new InvalidOperationException("Der Detektor lieferte kein Ergebnis.");
        }
        catch (Exception exception)
        {
            DetectorFailed(exception);
            return null;
        }

        _speech.SetRecent(regions, BufferStart, Accepted);
        Reevaluate();

        // Fällig ist ein Schritt, sobald das angenommene Audio ein Vielfaches von Step erreicht; er entfällt ohne offene
        // Äusserung oder solange ein Decode-Aufruf noch nicht zurückgekehrt ist (Kapitel 5.2)
        if (IsLive && Open is { } open && !DecoderBusy
            && Accepted.Ticks / _options.Step.Ticks > previousEnd.Ticks / _options.Step.Ticks)
        {
            _stepFor = open;
        }

        UpdateBacklog();
        return rest;
    }

    private void Gap(long first)
    {
        var gapStart = Accepted;
        var gapEnd = PcmAudio.ToTime(first);
        Emit(new LiveNotice(LiveNoticeKind.AudioGap, gapStart, gapEnd));
        if (Open is not null)
        {
            CloseOpen(gapStart, UtteranceEnd.AudioGap);
        }

        SetBufferStart(gapStart);
        _buffer.Reset(first);
        _lastBoundary = gapEnd;
        Interlocked.Exchange(ref _acceptedTicks, gapEnd.Ticks);
    }

    private void EndOfAudio()
    {
        _audioEnded = true;
        if (Open is not null)
        {
            CloseOpen(Accepted, UtteranceEnd.StreamEnd);
        }

        ReleaseNonSpeech();
        UpdateBacklog();
    }

    /// <summary>
    /// Öffnet eine Äusserung am Beginn des ersten Sprachbereichs nach der letzten Grenze und schliesst sie, sobald nach
    /// ihrem letzten Sprachbereich mindestens <c>PauseThreshold</c> ohne Sprache liegen (Kapitel 5.1).
    /// </summary>
    private void Reevaluate()
    {
        while (true)
        {
            var open = Open;
            if (open is null)
            {
                if (_audioEnded || FirstSpeech(_lastBoundary, Accepted) is not { } start)
                {
                    ReleaseNonSpeech();
                    return;
                }

                // Beginn am Sprachbeginn bzw. an der Grenze, auch zwischen zwei Samples (S02), mit Vorlauf (P11 S3), aber nie
                // vor der letzten Grenze und nie vor dem ersten gehaltenen Sample
                var early = SpeechRegions.Max(start - _options.Lead, SpeechRegions.Max(_lastBoundary, TimeSpan.Zero));
                var begin = PcmAudio.ToSampleAfter(early) < _buffer.StartSample ? BufferStart : early;
                open = new LiveUtterance(begin, start, _options.MaxUtterance);
                _utterances.Add(open);
                KeepFromCutPoint(open);
            }

            if (PauseAfter(open) is not { } speechEnd)
            {
                return;
            }

            CloseOpen(speechEnd, UtteranceEnd.Pause);
        }
    }

    /// <summary>Ende des letzten Sprachbereichs der offenen Äusserung, wenn danach eine Pause bestätigt ist.</summary>
    private TimeSpan? PauseAfter(LiveUtterance open)
    {
        TimeSpan? speechEnd = null;
        foreach (var region in _speech.Merged)
        {
            if (region.End <= open.Start)
            {
                continue;
            }

            if (speechEnd is { } end && region.Start - end >= _options.PauseThreshold)
            {
                return end;
            }

            speechEnd = speechEnd is { } previous ? SpeechRegions.Max(previous, region.End) : region.End;
        }

        // Ein Bereich, der am Audioende endet, gilt als offen (Vertrag 3)
        var last = speechEnd ?? open.Start;
        return last < Accepted && Accepted - last >= _options.PauseThreshold ? last : null;
    }

    /// <summary>
    /// Sprachbereiche für den Filter einer Äusserung: Der Vorlauf zählt als Sprache, sonst entfernte der Filter gerade die
    /// Wortanfänge wieder, die der Vorlauf retten soll (Regel: weniger als die Hälfte in Sprache entfällt).
    /// </summary>
    private IReadOnlyList<SpeechRegion> Speech(LiveUtterance utterance)
    {
        var merged = _speech.Merged;
        if (utterance.Start >= utterance.SpeechStart)
        {
            return merged;
        }

        // Der Vorlauf selbst ist der Bereich, nicht eine Verlängerung des Sprachbereichs: Silero meldet den Bereich bei einer
        // späteren Erkennung oft einige Millisekunden verschoben
        return SpeechRegions.Merge([.. merged, new SpeechRegion(utterance.Start, utterance.SpeechStart)],
            TimeSpan.Zero, TimeSpan.MaxValue);
    }

    /// <summary>Beginn der ersten Sprache in [<paramref name="from"/>, <paramref name="to"/>), frühestens bei <paramref name="from"/>.</summary>
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

    /// <summary>Setzt die Grenze der offenen Äusserung; ihr Audio wird kopiert, der Puffer kann dahinter freigeben.</summary>
    private void CloseOpen(TimeSpan boundary, UtteranceEnd ending)
    {
        var open = Open!;
        boundary = SpeechRegions.Max(boundary, open.CutPoint);
        open.Close(boundary, ending);
        // Kopie nach denselben Rasterregeln wie ein Auftrag: ab dem ersten Sample nach K bis vor die Grenze
        var from = PcmAudio.ToSampleAfter(open.CutPoint);
        var to = Math.Max(from, PcmAudio.ToSample(boundary));
        open.Audio = new DetachedAudio(from, _buffer.Copy(from, to));
        _lastBoundary = SpeechRegions.Max(_lastBoundary, boundary);
        if (_stepFor == open)
        {
            _stepFor = null;
        }
    }

    /// <summary>Gibt bestätigte Nicht-Sprache frei, wenn keine Äusserung offen ist (Regel 5, T29, T67).</summary>
    private void ReleaseNonSpeech()
    {
        if (Open is not null)
        {
            return;
        }

        SetBufferStart(_audioEnded ? Accepted : SpeechRegions.Max(_lastBoundary, Accepted - _options.PauseThreshold));
    }

    /// <summary>
    /// Freigabe bis zu einer Zeit, abgerundet: Das Sample, in dem sie liegt, bleibt erhalten, damit Nicht-Sprache nie
    /// Audio mitnimmt, das noch zu Sprache gehören kann. Kürzungspunkte geben aufgerundet frei (KeepFromCutPoint).
    /// </summary>
    private void SetBufferStart(TimeSpan start) => SetBufferStart(PcmAudio.ToSample(start));

    private void SetBufferStart(long start)
    {
        var sample = Math.Clamp(start, _buffer.StartSample, _buffer.EndSample);
        if (sample > _buffer.StartSample)
        {
            _speech.Settle(PcmAudio.ToTime(sample));
            _buffer.ReleaseUntil(sample);
        }
    }

    /// <summary>
    /// Gehaltenes Audio: der Puffer ab dem ältesten noch nötigen Sample und die Kopien abgeschlossener Äusserungen. Lücken
    /// zählen nicht, weil dort kein Audio liegt. Eine Kopie zählt mit ihrer Zeitspanne [K, Grenze); gespeichert ist höchstens
    /// so viel, die Grenze wird also nie unterschätzt.
    /// </summary>
    private TimeSpan Held() => (Accepted - BufferStart) + _utterances.Aggregate(TimeSpan.Zero, (sum, item) => sum + item.Held);

    /// <summary>Beginn des ältesten gehaltenen Audios, für die Zeitspanne der Rückstandsmeldungen (S05).</summary>
    private TimeSpan HeldStart() =>
        _utterances.Where(item => item.Held > TimeSpan.Zero).Select(item => item.CutPoint).Append(BufferStart).Min();

    /// <summary>
    /// Verarbeitungsrückstand (ER-05, E2): gehaltenes Audio nach dem Verarbeitungsstand, Lücken zählen nicht. Der
    /// Verarbeitungsstand ist der früheste Punkt, ab dem ein fälliges Ergebnis noch aussteht: je Äusserung live das Ende
    /// des jüngsten verarbeiteten Schritts (sonst <c>K</c>), satzweise ihre Grenze bzw. die Schnittzeit <c>C</c>, an der
    /// erst ein Decode fällig wird; ohne Äusserung das angenommene Audioende. Gewolltes Halten bis zur Grenze zählt so
    /// nicht, ein hängender Decoder sammelt dagegen Rückstand an, auch bei offener Äusserung.
    /// </summary>
    private TimeSpan Pending()
    {
        var stand = Accepted;
        foreach (var utterance in _utterances)
        {
            var due = IsLive
                ? SpeechRegions.Max(utterance.DecodedUntil, utterance.CutPoint)
                : utterance.Boundary is { } boundary ? SpeechRegions.Min(boundary, utterance.Limit) : utterance.Limit;
            stand = SpeechRegions.Min(stand, due);
        }

        var pending = After(BufferStart, Accepted);
        foreach (var utterance in _utterances)
        {
            if (utterance.Boundary is { } end)
            {
                pending += After(utterance.CutPoint, end);
            }
        }

        return pending;

        // Anteil des gehaltenen Stücks [from, to) nach dem Verarbeitungsstand
        TimeSpan After(TimeSpan from, TimeSpan to) =>
            to > SpeechRegions.Max(from, stand) ? to - SpeechRegions.Max(from, stand) : TimeSpan.Zero;
    }

    /// <summary>Rückstand und Puffer melden (ER-05, Regel 6): Warnung nach dem Verarbeitungsrückstand, Grenze nach dem Gehaltenen.</summary>
    private void UpdateBacklog()
    {
        var held = Held();
        var pending = Pending();
        var start = HeldStart();
        if (!_warned && pending >= _options.BacklogWarning)
        {
            _warned = true;
            Emit(new LiveNotice(LiveNoticeKind.BacklogWarning, start, Accepted));
        }
        else if (_warned && pending < _options.BacklogWarning)
        {
            _warned = false;
            Emit(new LiveNotice(LiveNoticeKind.BacklogCleared, start, Accepted));
        }

        // Voll, sobald nicht einmal ein Sample mehr Platz hat (Regel 6, U02)
        var full = _options.BacklogLimit - held < TimeSpan.FromTicks(PcmAudio.TicksPerSample);
        if (full && !_full)
        {
            Emit(new LiveNotice(LiveNoticeKind.BufferFull, start, Accepted));
        }

        if (_full && !full)
        {
            _acceptSignal.Set();
        }

        _full = full;
    }

    // ---- Decoderarbeit -------------------------------------------------------------------------------------------

    private async Task WorkAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                DecodeJob? job;
                Task wake;
                lock (_gate)
                {
                    if (_closed)
                    {
                        return;
                    }

                    job = PlanJob();
                    if (job is null && _audioEnded && _utterances.Count == 0)
                    {
                        return;
                    }

                    _busy = job is not null;
                    wake = _workSignal.Next;
                }

                if (job is null)
                {
                    await wake.WaitAsync(token).ConfigureAwait(false);
                    continue;
                }

                await RunJobAsync(job, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Abbruch oder Ende des Laufs: kein weiterer Decode (BE-06)
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                Fail(exception);
            }
        }
        finally
        {
            StopIfClosed();
        }
    }

    /// <summary>
    /// Nächster Decode-Auftrag für die älteste Äusserung (Kapitel 5.1, Regeln 3 und 4; Kapitel 5.2 und 5.3). Äusserungen,
    /// bei denen nichts mehr zu decodieren ist, schliesst P3 hier direkt ab.
    /// </summary>
    private DecodeJob? PlanJob()
    {
        while (_utterances.Count > 0)
        {
            var utterance = _utterances[0];
            var cut = utterance.CutPoint;
            if (utterance.Boundary is { } boundary)
            {
                var end = SpeechRegions.Min(boundary, utterance.Limit);
                if (IsLive)
                {
                    end = SpeechRegions.Min(end, cut + _options.Window);
                }

                var kind = end == boundary ? JobKind.Final : end == utterance.Limit ? JobKind.LimitCut : JobKind.FullWindow;
                if (Job(utterance, kind, end) is { } closedJob)
                {
                    return closedJob;
                }

                SkipEmpty(utterance, kind, end);
                continue;
            }

            var accepted = Accepted;
            if (IsLive)
            {
                // Ein fälliger Schritt, oder Rückstand, der einen vollen Auftrag oder die Schnittzeit erreicht (Regel 4)
                var due = _stepFor == utterance;
                var backlog = accepted >= cut + _options.Window || accepted >= utterance.Limit;
                if (!due && !backlog)
                {
                    return null;
                }

                _stepFor = null;
                var end = SpeechRegions.Min(SpeechRegions.Min(accepted, cut + _options.Window), utterance.Limit);
                var kind = end == utterance.Limit ? JobKind.LimitCut : end == cut + _options.Window ? JobKind.FullWindow : JobKind.Step;
                if (Job(utterance, kind, end) is { } openJob)
                {
                    return openJob;
                }

                // Ein Schritt ohne ganzes Sample hat nichts zu tun; erzwungene Schnitte schreiten trotzdem voran (N03)
                if (kind == JobKind.Step)
                {
                    return null;
                }

                SkipEmpty(utterance, kind, end);
                continue;
            }

            // Satzweise: offen wird nur am Längenschnitt decodiert (Kapitel 5.3)
            if (accepted < utterance.Limit)
            {
                return null;
            }

            if (Job(utterance, JobKind.LimitCut, utterance.Limit) is { } sentenceJob)
            {
                return sentenceJob;
            }

            SkipEmpty(utterance, JobKind.LimitCut, utterance.Limit);
        }

        return null;
    }

    /// <summary>
    /// Zwischen dem aufgerundeten <c>K</c> und dem abgerundeten Auftragsende liegt kein ganzes Sample (N03, S01): Es bleibt
    /// nichts zu decodieren und damit nichts zu verlieren, also weder Text noch Verlustmeldung. Der Rest bis zur Grenze
    /// schliesst die Äusserung ab, ein Längenschnitt endet bei <c>C</c> und die nächste beginnt dort, ein voller Auftrag
    /// rückt <c>K</c> an sein Ende. So schreitet die Planung immer voran; schon übernommene Wörter bleiben.
    /// </summary>
    private void SkipEmpty(LiveUtterance utterance, JobKind kind, TimeSpan end)
    {
        switch (kind)
        {
            case JobKind.Final:
                Finish(utterance, []);
                break;

            case JobKind.LimitCut:
                utterance.Cut([], end, []);
                FinishAtCut(utterance);
                break;

            default:
                utterance.Cut([], end, []);
                KeepFromCutPoint(utterance);
                break;
        }
    }

    /// <summary>
    /// Rasterregeln (U01, N03): Der Beginn wird auf das erste Sample an oder nach <c>K</c> aufgerundet, damit kein
    /// Auftrag Audio vor <c>K</c> erneut abfragt; das Ende (angenommenes Audio, <c>K</c> plus <c>Window</c>, <c>C</c> oder die
    /// Grenze) wird abgerundet, damit kein Auftrag über <c>C</c>, die Grenze oder das angenommene Audio hinausreicht.
    /// </summary>
    private DecodeJob? Job(LiveUtterance utterance, JobKind kind, TimeSpan end)
    {
        var from = PcmAudio.ToSampleAfter(utterance.CutPoint);
        var to = PcmAudio.ToSample(end);
        if (to <= from)
        {
            return null;
        }

        var audio = utterance.Audio is { } copy ? copy.Copy(from, to) : _buffer.Copy(from, to);
        return new DecodeJob(utterance, kind, PcmAudio.ToTime(from), PcmAudio.ToTime(to), audio);
    }

    private async Task RunJobAsync(DecodeJob job, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                // Nach einem Fehler oder Abbruch beginnt kein Decode mehr (Kapitel 5.4)
                if (_closed)
                {
                    return;
                }
            }

            Task<RecognitionHypothesis> call;
            try
            {
                call = _decoder.DecodeAsync(job.Audio, job.Start, _recognition, token);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                call = Task.FromException<RecognitionHypothesis>(exception);
            }

            lock (_gate)
            {
                _decoding = call;
            }

            RecognitionHypothesis? hypothesis = null;
            Exception? error = null;
            try
            {
                hypothesis = await call.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Ein Decoder, der den Abbruch übergeht, wird verworfen (T35); seine Ausnahme wird noch beobachtet
                _ = call.ContinueWith(
                    static task => task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }
            catch (Exception exception)
            {
                error = exception;
            }

            lock (_gate)
            {
                _decoding = null;
                if (_closed)
                {
                    return;
                }

                IReadOnlyList<RecognizedWord>? words = null;
                if (error is null)
                {
                    try
                    {
                        words = HypothesisCheck.Accept(hypothesis, job.Start, job.End);
                    }
                    catch (InvalidHypothesisException invalid)
                    {
                        Interlocked.Increment(ref _invalidHypotheses);
                        error = invalid;
                    }
                }

                if (words is not null)
                {
                    Process(job, words);
                }
                else if (attempt == 1)
                {
                    // Derselbe Decode wird einmal wiederholt (Kapitel 5.4)
                    Interlocked.Increment(ref _processed);
                    continue;
                }
                else
                {
                    DecoderFailed(job, error!);
                }

                _busy = false;
                UpdateBacklog();
                Interlocked.Increment(ref _processed);
                return;
            }
        }
    }

    private void Process(DecodeJob job, IReadOnlyList<RecognizedWord> words)
    {
        var utterance = job.Utterance;
        var kind = job.Kind;

        // Steht die Grenze inzwischen innerhalb des Auftrags fest, gehören nur Wörter davor zur Äusserung (Kapitel 5.1);
        // ein erzwungener Schnitt ist dann nicht mehr nötig
        if (kind != JobKind.Final && utterance.Boundary is { } boundary && boundary <= job.End)
        {
            words = [.. words.Where(word => word.Start < boundary)];
            if (!IsLive)
            {
                Finish(utterance, words);
                return;
            }

            kind = JobKind.Step;
        }

        switch (kind)
        {
            case JobKind.Step:
                if (utterance.Boundary is { } limit)
                {
                    words = [.. words.Where(word => word.Start < limit)];
                }

                utterance.ShowHypothesis(words, job.End);
                ShowPreview(utterance);
                break;

            case JobKind.FullWindow:
                ForcedCut(utterance, words, job.End);
                KeepFromCutPoint(utterance);
                ShowPreview(utterance);
                break;

            case JobKind.LimitCut:
                ForcedCut(utterance, words, job.End);
                FinishAtCut(utterance);
                break;

            default:
                Finish(utterance, words);
                break;
        }
    }

    /// <summary>
    /// Schnittwahl bei einem erzwungenen Schnitt an der Grenze <paramref name="limit"/> (Kapitel 5.1, Fall a bis c), auf
    /// den geprüften Wörtern vor den Filtern.
    /// </summary>
    private void ForcedCut(LiveUtterance utterance, IReadOnlyList<RecognizedWord> words, TimeSpan limit)
    {
        var units = WordComparison.Units(words);
        var from = utterance.CutPoint;
        var guard = _options.CutGuard;
        var adopt = units.FindLastIndex(unit => unit.End <= limit - guard);
        TimeSpan cut;
        if (adopt >= 0)
        {
            // a: bis einschliesslich des letzten Worts, das mindestens CutGuard vor der Grenze endet
            cut = units[adopt].End;
        }
        else if (units.Count > 0 && units[0].First.Start > from)
        {
            // b: am Beginn des ersten Worts; dieses und alle folgenden werden neu decodiert
            cut = units[0].First.Start;
        }
        else if (units.Count > 0)
        {
            // b: das erste Wort beginnt schon bei K und wird trotz CutGuard übernommen (T63)
            adopt = 0;
            cut = units[0].End;
        }
        else
        {
            // c: leer; bei G minus CutGuard, sonst bei G (T53; Rasterrandfall C01)
            cut = limit - guard > from ? limit - guard : limit;
        }

        if (adopt < 0 && SpeechRegions.ContainsSpeech(_speech.Merged, from, cut))
        {
            // Sichtbare Verlustmeldung statt stiller Freigabe (Regel 2)
            Emit(new LiveNotice(LiveNoticeKind.SpeechWithoutText, from, cut));
        }
        else if (adopt >= 0)
        {
            // Der Rand bleibt unsicher: sichtbar machen, den Text nicht ändern (T78, Regel 5 der unsicheren Ränder)
            Emit(new LiveNotice(LiveNoticeKind.LengthCutUncertain, cut, SpeechRegions.Max(cut, limit)));
        }

        utterance.Cut(
            units.Take(adopt + 1).SelectMany(unit => unit.Words),
            cut,
            [.. units.Skip(adopt + 1).SelectMany(unit => unit.Words)]);
    }

    /// <summary>
    /// Gibt Audio vor dem Kürzungspunkt der offenen Äusserung frei; es wird nie mehr decodiert (T45). Aufgerundet: Das
    /// Sample, in dem <c>K</c> liegt, beginnt vor <c>K</c> und gehört darum zu keinem weiteren Auftrag (U01).
    /// </summary>
    private void KeepFromCutPoint(LiveUtterance utterance)
    {
        if (utterance.Boundary is null)
        {
            SetBufferStart(PcmAudio.ToSampleAfter(utterance.CutPoint));
        }
    }

    /// <summary>Längenschnitt: Die Äusserung endet am Kürzungspunkt, die nächste beginnt dort (Regel 3).</summary>
    private void FinishAtCut(LiveUtterance utterance)
    {
        var cut = utterance.CutPoint;
        var boundary = utterance.Boundary;
        var ending = utterance.Ending;
        var audio = utterance.Audio;
        Complete(utterance, cut, UtteranceEnd.LengthCut);
        if (boundary is { } end)
        {
            // Die Grenze dahinter steht schon fest: der Rest wird eine eigene, schon abgeschlossene Äusserung
            if (cut < end && FirstSpeech(cut, end) is { } start)
            {
                var next = new LiveUtterance(start, _options.MaxUtterance) { Audio = audio };
                next.Close(end, ending);
                _utterances.Insert(0, next);
            }

            return;
        }

        _lastBoundary = SpeechRegions.Max(_lastBoundary, cut);
        SetBufferStart(PcmAudio.ToSampleAfter(cut));
        Reevaluate();
    }

    private void Finish(LiveUtterance utterance, IReadOnlyList<RecognizedWord> rest)
    {
        utterance.AddRest(rest);
        Complete(utterance, utterance.Boundary!.Value, utterance.Ending);
    }

    /// <summary>
    /// Schliesst eine Äusserung ab, auch ohne Wörter, damit Grenze, Kennung und Aktivierung enden (Regel 2, T51).
    /// Die Filter aus Kapitel 5.5 gelten für die übernommenen Wörter.
    /// </summary>
    private List<RecognizedWord> Complete(LiveUtterance utterance, TimeSpan end, UtteranceEnd ending)
    {
        var words = _guard.Final(utterance.Stable, Speech(utterance));
        var start = words.Count > 0 && words[0].Start < utterance.Start ? words[0].Start : utterance.Start;
        _utterances.Remove(utterance);
        if (_stepFor == utterance)
        {
            _stepFor = null;
        }

        Emit(new UtteranceCompleted(new Utterance(utterance.Id, words, start, end, ending)));

        // Bereiche vor dem Ende dieser Äusserung braucht nur noch, wer davor beginnt
        var keepFrom = SpeechRegions.Min(end, BufferStart);
        _speech.Prune(_utterances.Count > 0 ? SpeechRegions.Min(_utterances[0].Start, keepFrom) : keepFrom);
        return words;
    }

    private void ShowPreview(LiveUtterance utterance)
    {
        var shown = _guard.InSpeech([.. utterance.Stable, .. utterance.Tail], Speech(utterance));
        var text = TextJoin.Join(shown.Select(word => word.Text));
        if (text != utterance.Preview)
        {
            utterance.Preview = text;
            Emit(new ProvisionalText(utterance.Id, text));
        }
    }

    // ---- Fehler --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Endgültiger Decoderfehler (Kapitel 5.4): Abschluss mit den übernommenen Wörtern (seit E1 keine), Meldung des nicht
    /// erkannten Bereichs vom Ende des letzten solchen Worts, sonst vom Äusserungsbeginn, bis zum Ende des Auftrags, dann
    /// Ende mit Fehler (T75).
    /// </summary>
    private void DecoderFailed(DecodeJob job, Exception error)
    {
        var lossStart = CompleteAfterFailure(job.Utterance, UtteranceEnd.DecoderFailed);
        Emit(new LiveNotice(LiveNoticeKind.DecoderFailed, lossStart, SpeechRegions.Max(lossStart, job.End)));
        _utterances.Clear();
        Fail(new InvalidOperationException("Die Erkennung ist ausgefallen; der gemeldete Bereich fehlt (Kapitel 5.4).", error));
    }

    /// <summary>
    /// Detektorfehler (Kapitel 5.4, T68): ohne Wiederholung; Abschluss der ältesten offenen Arbeit mit ihren übernommenen
    /// Wörtern (seit E1 keine), genau eine Meldung bis <see cref="AcceptedEnd"/>, danach weder Decoder noch Detektor noch Annahme.
    /// </summary>
    private void DetectorFailed(Exception error)
    {
        var lossStart = _utterances.Count > 0 ? CompleteAfterFailure(_utterances[0], UtteranceEnd.DetectorFailed) : BufferStart;
        Emit(new LiveNotice(LiveNoticeKind.DetectorFailed, lossStart, SpeechRegions.Max(lossStart, Accepted)));
        _utterances.Clear();
        Fail(new InvalidOperationException("Die Sprachbereichserkennung ist ausgefallen (Kapitel 5.4).", error));
    }

    /// <summary>Schliesst mit den übernommenen Wörtern ab (seit E1 keine) und liefert den Beginn der Verlustspanne.</summary>
    private TimeSpan CompleteAfterFailure(LiveUtterance utterance, UtteranceEnd ending)
    {
        var kept = _guard.Final(utterance.Stable, Speech(utterance));
        var lossStart = kept.Count > 0 ? kept[^1].End : utterance.Start;
        Complete(utterance, lossStart, ending);
        return lossStart;
    }

    private void Fail(Exception exception)
    {
        _failure ??= ExceptionDispatchInfo.Capture(exception);
        _closed = true;
        _workSignal.Set();
        _acceptSignal.Set();
    }

    private void StopIfClosed()
    {
        bool closed;
        lock (_gate)
        {
            closed = _closed;
        }

        if (closed)
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
    }

    private void Emit(TranscriptEvent transcriptEvent)
    {
        if (!_closed)
        {
            _events.Writer.TryWrite(transcriptEvent);
        }
    }

    private enum JobKind
    {
        /// <summary>Live-Schritt: nur Vorschau, keine Übernahme (E1 a, Kapitel 5.2).</summary>
        Step,

        /// <summary>Voller Live-Auftrag: erzwungener Schnitt am Auftragsende, die Äusserung bleibt offen (Regel 4).</summary>
        FullWindow,

        /// <summary>Auftrag bis zur Schnittzeit: erzwungener Schnitt, die Äusserung endet dort (Regel 3).</summary>
        LimitCut,

        /// <summary>Rest bis zur Grenze: Abschluss der Äusserung.</summary>
        Final,
    }

    private sealed class DecodeJob(LiveUtterance utterance, JobKind kind, TimeSpan start, TimeSpan end, float[] audio)
    {
        public LiveUtterance Utterance { get; } = utterance;

        public JobKind Kind { get; } = kind;

        public TimeSpan Start { get; } = start;

        public TimeSpan End { get; } = end;

        /// <summary>Eigene Kopie; bleibt bis zum Ende des Aufrufs gültig und unverändert (Vertrag 1).</summary>
        public ReadOnlyMemory<float> Audio { get; } = audio;
    }
}
