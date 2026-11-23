using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Freihand.Core.Recognition;

namespace Freihand.Adapters.Recognition;

/// <summary>Warum der Client den Erkenner nicht mehr nutzen kann (für den Supervisor, Kapitel 5.7).</summary>
internal enum ClientFault
{
    /// <summary>Verbindung getrennt oder Rahmenfehler.</summary>
    Disconnected,

    /// <summary>Zeitgrenze eines Decodes oder Detects.</summary>
    Timeout,

    /// <summary><c>failed</c> auf <c>detect</c>.</summary>
    DetectorFailed,
}

/// <summary>
/// Client auf beiden Kanälen, ein Exemplar je Erkennerprozess (Kapitel 10.4): die <c>id</c> läuft je Verbindung über alle
/// Sitzungen, späte Antworten werden über die <c>id</c> verworfen. Je Kanal läuft höchstens eine Anfrage; weitere warten.
/// </summary>
public sealed class RecognizerClient : IHypothesisDecoder, ISpeechDetector, IAsyncDisposable
{
    private const long TicksPerSample = 625;

    private readonly Lane _decode;
    private readonly Lane _detect;
    private readonly ClientTimeouts _timeouts;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private int _faulted;
    private readonly object _disposeGate = new();
    private Task? _disposing;

    public RecognizerClient(Stream decodeChannel, Stream detectChannel, ClientTimeouts timeouts, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(decodeChannel);
        ArgumentNullException.ThrowIfNull(detectChannel);
        ArgumentNullException.ThrowIfNull(timeouts);
        ArgumentNullException.ThrowIfNull(time);

        _timeouts = timeouts;
        _time = time;
        _decode = new Lane(decodeChannel);
        _detect = new Lane(detectChannel);
        _ = ListenAsync(_decode);
        _ = ListenAsync(_detect);
    }

    /// <summary>Wird genau einmal ausgelöst, bevor wartende Aufrufe mit <see cref="RecognizerUnavailableException"/> enden.</summary>
    internal event Action<ClientFault>? Faulted;

    /// <summary>Verbindung getrennt, Zeitgrenze abgelaufen oder Detektor gescheitert; der Client nimmt nichts mehr an.</summary>
    internal bool IsBroken => Volatile.Read(ref _faulted) == 1;

    public async Task<RecognitionHypothesis> DecodeAsync(
        ReadOnlyMemory<float> audio, TimeSpan offset, RecognitionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Sprache und Prompt bei jedem Aufruf neu; die Sitzung merkt sich nichts (Kapitel 5.5, P3 T43)
        var language = LanguageMapping.ToWhisper(options.Language);
        var (prompt, _) = PromptBuilder.Build(options.VocabularyHints, PromptBuilder.DefaultMaxChars);

        // Den Speicher nur während des Aufrufs lesen: sofort in die Nachricht kopieren (P3 Vertrag 1)
        var payload = PipeFraming.FloatBytes(audio.Span);
        var sampleCount = audio.Length;
        var limit = _timeouts.DecodeBase + TimeSpan.FromTicks(5 * sampleCount * TicksPerSample);

        await _decode.Turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfBroken();
            var (id, answer) = _decode.Register();
            var header = new JsonObject
            {
                ["type"] = "decode",
                ["id"] = id,
                ["offsetTicks"] = offset.Ticks,
                ["sampleCount"] = sampleCount,
                ["language"] = language,
            };
            if (prompt is not null)
            {
                header["prompt"] = prompt;
            }

            PipeFrame frame;
            try
            {
                frame = await ExchangeAsync(_decode, header, payload, answer, limit, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Die spätere Antwort mit dieser id wird verworfen (Kapitel 5.7)
                _decode.Forget(id);
                _ = TrySendAsync(_decode, new JsonObject { ["type"] = "cancel", ["id"] = id });
                throw;
            }
            catch (TimeoutException)
            {
                _decode.Forget(id);
                Fault(ClientFault.Timeout);
                throw new RecognizerUnavailableException();
            }

            return frame.Type switch
            {
                "hypothesis" => Parse(frame, ToHypothesis),
                "failed" => throw new RecognizerException(PipeFraming.Code(frame) ?? RecognizerErrorCode.ProtocolViolation),
                "cancelled" => throw new OperationCanceledException(),
                _ => throw BreakProtocol(),
            };
        }
        finally
        {
            _decode.Turn.Release();
        }
    }

    public IReadOnlyList<SpeechRegion> Detect(ReadOnlyMemory<float> audio, TimeSpan offset)
    {
        var payload = PipeFraming.FloatBytes(audio.Span);
        _detect.Turn.Wait();
        try
        {
            ThrowIfBroken();
            var (id, answer) = _detect.Register();
            var header = new JsonObject
            {
                ["type"] = "detect",
                ["id"] = id,
                ["offsetTicks"] = offset.Ticks,
                ["sampleCount"] = audio.Length,
            };

            PipeFrame frame;
            try
            {
                frame = ExchangeAsync(_detect, header, payload, answer, _timeouts.Detect, default).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                _detect.Forget(id);
                Fault(ClientFault.Timeout);
                throw new RecognizerUnavailableException();
            }

            if (frame.Type != "regions")
            {
                // Nie eine leere Liste oder ein Bereich über alles: der Detektor wirft (Kapitel 5.7, Detektorfehler)
                Fault(ClientFault.DetectorFailed);
                throw new RecognizerUnavailableException();
            }

            return Parse(frame, regions => (IReadOnlyList<SpeechRegion>)[.. (regions.Header["regions"] as JsonArray ?? []).Select(region => new SpeechRegion(
                TimeSpan.FromTicks((long)region!["startTicks"]!),
                TimeSpan.FromTicks((long)region["endTicks"]!)))]);
        }
        finally
        {
            _detect.Turn.Release();
        }
    }

    /// <summary>Gemeinsamer Abschluss aller Freigaben (P8 5.4/5.7); ein fremdes Dispose ohne Ende bleibt abwartbar.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_disposeGate)
        {
            if (_disposing is not null) return new ValueTask(_disposing);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposing = completion.Task;
        }

        _ = ReleaseAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task ReleaseAsync(TaskCompletionSource completion)
    {
        try
        {
            Fault(null);
            // P8 5.4: Beide Besitzer unabhängig freigeben, auch bei synchron blockierendem Fremdcode.
            var decode = Task.Run(async () => await _decode.Stream.DisposeAsync().ConfigureAwait(false));
            var detect = Task.Run(async () => await _detect.Stream.DisposeAsync().ConfigureAwait(false));
            var cancel = Task.Run(async () =>
            {
                try { await _stop.CancelAsync().ConfigureAwait(false); }
                catch (Exception) { /* SI-01: Fremde Abbruch-Callbacks verhindern keine Freigabe. */ }
            });
            await Task.WhenAll(decode, detect, cancel).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static RecognitionHypothesis ToHypothesis(PipeFrame frame)
    {
        var words = (frame.Header["words"] as JsonArray ?? []).Select(word => new RecognizedWord(
            (string)word!["text"]!,
            TimeSpan.FromTicks((long)word["startTicks"]!),
            TimeSpan.FromTicks((long)word["endTicks"]!),
            (float)word["probability"]!)).ToList();
        return new RecognitionHypothesis(
            TimeSpan.FromTicks(frame.Long("audioStartTicks") ?? throw Violation()),
            TimeSpan.FromTicks(frame.Long("audioEndTicks") ?? throw Violation()),
            words);
    }

    private static RecognizerException Violation() => new(RecognizerErrorCode.ProtocolViolation);

    /// <summary>Eine Antwort, die nicht dem Protokoll entspricht, trennt den Erkenner wie ein Rahmenfehler.</summary>
    private T Parse<T>(PipeFrame frame, Func<PipeFrame, T> parse)
    {
        try
        {
            return parse(frame);
        }
        catch (Exception exception) when (exception is not RecognizerUnavailableException)
        {
            Fault(ClientFault.Disconnected);
            throw new RecognizerUnavailableException();
        }
    }

    private void ThrowIfBroken()
    {
        if (IsBroken)
        {
            throw new RecognizerUnavailableException();
        }
    }

    private RecognizerUnavailableException BreakProtocol()
    {
        Fault(ClientFault.Disconnected);
        return new RecognizerUnavailableException();
    }

    // P8 5.7 (BE-18): Eine Frist umfasst Schreiben und Antwort. WaitAsync begrenzt auch
    // Streams ohne Abbruchunterstützung; ihr späterer Abschluss oder Fehler bleibt beobachtet.
    private async Task<PipeFrame> ExchangeAsync(Lane lane, JsonObject header, byte[] payload,
        Task<PipeFrame> answer, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(limit, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken, _stop.Token);
        Task? sending = null;
        try
        {
            sending = PipeFraming.WriteAsync(lane.Stream, lane.Write, header, payload, linked.Token);
            await sending.WaitAsync(linked.Token).ConfigureAwait(false);
            return await answer.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // P8 4.7/5.7: Ein unterbrochener Rahmen trennt; ein vollständiger Auftrag kann normal abbrechen.
            if (sending is not { IsCompletedSuccessfully: true }) Fault(ClientFault.Disconnected);
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Fault(ClientFault.Timeout);
            throw new TimeoutException();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Fault(ClientFault.Disconnected);
            throw new RecognizerUnavailableException();
        }
        finally
        {
            if (sending is not null) _ = ObserveAsync(sending);
            _ = ObserveAsync(answer);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* P8 5.7: Fremde Taskfehler wurden bereits am Aufruf in neutrale Fehler umgesetzt. */ }
    }

    private async Task TrySendAsync(Lane lane, JsonObject header)
    {
        using var deadline = new CancellationTokenSource(_timeouts.Detect, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, _stop.Token);
        try
        {
            await PipeFraming.WriteAsync(lane.Stream, lane.Write, header, default, linked.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Fault(ClientFault.Disconnected);
        }
    }

    private async Task ListenAsync(Lane lane)
    {
        try
        {
            while (await PipeFraming.ReadAsync(lane.Stream, _stop.Token).ConfigureAwait(false) is { } frame)
            {
                if ((ReferenceEquals(lane, _decode) && frame.Type is not ("hypothesis" or "failed" or "cancelled"))
                    || (ReferenceEquals(lane, _detect) && frame.Type is not ("regions" or "failed")))
                {
                    Fault(ClientFault.Disconnected);
                    return;
                }

                if (frame.Long("id") is { } id)
                {
                    lane.Answer(id, frame);
                }
            }
        }
        catch (Exception)
        {
            // Rahmenfehler, getrennte Verbindung oder Ende: jeder wartende Aufruf endet sichtbar, nie ein Hänger
        }

        Fault(ClientFault.Disconnected);
    }

    /// <summary>Erst der Supervisor, dann die wartenden Aufrufe: so sieht ein Folgeaufruf schon den Neustart.</summary>
    private void Fault(ClientFault? fault)
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 1)
        {
            return;
        }

        if (fault is { } reason)
        {
            Faulted?.Invoke(reason);
        }

        _decode.FailAll();
        _detect.FailAll();
    }

    /// <summary>Ein Kanal: Schreibsperre, Reihenfolge der Anfragen und offene Antworten nach <c>id</c>.</summary>
    private sealed class Lane(Stream stream)
    {
        private readonly ConcurrentDictionary<long, TaskCompletionSource<PipeFrame>> _pending = new();
        private long _nextId;

        public Stream Stream { get; } = stream;

        public SemaphoreSlim Write { get; } = new(1, 1);

        public SemaphoreSlim Turn { get; } = new(1, 1);

        public (long Id, Task<PipeFrame> Answer) Register()
        {
            var id = Interlocked.Increment(ref _nextId);
            var answer = new TaskCompletionSource<PipeFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = answer;
            return (id, answer.Task);
        }

        public void Forget(long id)
        {
            if (_pending.TryRemove(id, out var answer)) answer.TrySetCanceled();
        }

        public void Answer(long id, PipeFrame frame)
        {
            if (_pending.TryRemove(id, out var answer))
            {
                answer.TrySetResult(frame);
            }
        }

        public void FailAll()
        {
            foreach (var id in _pending.Keys)
            {
                if (_pending.TryRemove(id, out var answer))
                {
                    answer.TrySetException(new RecognizerUnavailableException());
                }
            }
        }
    }
}
