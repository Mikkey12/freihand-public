using Freihand.Core.Recognition;

namespace Freihand.Core.Tests.Live.Fakes;

/// <summary>Ein Decode-Auftrag, wie P3 ihn stellt: Intervall in Sitzungszeit und die Optionen der Sitzung.</summary>
internal sealed record DecodeRequest(int Index, TimeSpan Start, TimeSpan End, int SampleCount, RecognitionOptions Options)
{
    public bool Overlaps(TimeSpan start, TimeSpan end) => Start < end && start < End;

    public bool Contains(TimeSpan start, TimeSpan end) => Start <= start && end <= End;
}

/// <summary>Protokoll eines Aufrufs; <see cref="Words"/> ist leer, solange er läuft oder wenn er scheiterte.</summary>
internal sealed class DecodeCall(DecodeRequest request)
{
    public DecodeRequest Request { get; } = request;

    public IReadOnlyList<RecognizedWord> Words { get; set; } = [];

    public bool Completed { get; set; }

    /// <summary>Die Task des Aufrufs ist abgeschlossen, mit Ergebnis oder Fehler; P3 kann das Ergebnis jetzt verarbeiten.</summary>
    public bool Returned => Volatile.Read(ref _returned) == 1;

    /// <summary>Speicher zu Beginn und am Ende des Aufrufs gleich dem Audio der Sitzung (Vertrag 1).</summary>
    public bool AudioIntact { get; set; } = true;

    private int _returned;

    public void MarkReturned() => Volatile.Write(ref _returned, 1);
}

/// <summary>
/// Decoder-Attrappe nach Kapitel 6: antwortet nach Audiointervall, sofern ein Test nichts anderes verlangt.
/// Protokolliert jeden Auftrag, kann Aufträge blockieren und den Abbruch auf Wunsch übergehen (T35).
/// </summary>
internal sealed class FakeHypothesisDecoder : IHypothesisDecoder
{
    private readonly Func<DecodeRequest, IReadOnlyList<RecognizedWord>> _respond;
    private readonly Lock _gate = new();
    private readonly List<DecodeCall> _calls = [];
    private int _running;
    private int _open;

    public FakeHypothesisDecoder(Func<DecodeRequest, IReadOnlyList<RecognizedWord>> respond) => _respond = respond;

    /// <summary>Wird vor der Antwort abgewartet; so blockiert ein Test einzelne Aufträge.</summary>
    public Func<DecodeRequest, Task>? Hold { get; set; }

    /// <summary>Ersetzt die ganze Hypothese, etwa mit falschem Kopf; sonst bildet die Attrappe ihn korrekt.</summary>
    public Func<DecodeRequest, RecognitionHypothesis>? RawResponse { get; set; }

    /// <summary>Falsch heisst: Der Decoder übergeht den Abbruch und liefert trotzdem (T35).</summary>
    public bool HonorCancellation { get; set; } = true;

    /// <summary>Prüft den übergebenen Speicher; bei sehr langen Läufen abschaltbar (T02).</summary>
    public bool VerifyAudio { get; set; } = true;

    public int MaxConcurrent { get; private set; }

    /// <summary>
    /// Aufrufe, deren Task noch nicht abgeschlossen ist. Null heisst nur, dass gerade kein Aufruf läuft; eingeplante,
    /// noch nicht begonnene Arbeit erfasst der Zähler nicht, er ist darum keine Quittung für einen Schritt (L01).
    /// </summary>
    public int Open => Volatile.Read(ref _open);

    public IReadOnlyList<DecodeCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<DecodeRequest> Requests => [.. Calls.Select(call => call.Request)];

    /// <summary>
    /// Antwort nach Audiointervall: alle gesprochenen Wörter ganz im Intervall; ein Wort, das zu mindestens der Hälfte am
    /// Beginn hineinragt, auf das Intervall gekürzt (der Rest eines Worts ist hörbar); ein Wort über dem Intervallende nur,
    /// wenn <paramref name="partialAtEnd"/> dafür einen Wortlaut liefert, dann bis zum Intervallende.
    /// </summary>
    public static IReadOnlyList<RecognizedWord> Heard(
        IEnumerable<RecognizedWord> spoken, TimeSpan start, TimeSpan end, Func<RecognizedWord, string?>? partialAtEnd = null)
    {
        var heard = new List<RecognizedWord>();
        foreach (var word in spoken.OrderBy(word => word.Start))
        {
            if (word.Start >= start && word.End <= end)
            {
                heard.Add(word);
            }
            else if (word.Start < start && word.End > start && word.End <= end
                && (word.End - start).Ticks * 2 >= (word.End - word.Start).Ticks)
            {
                heard.Add(word with { Start = start });
            }
            else if (word.Start >= start && word.Start < end && word.End > end && partialAtEnd?.Invoke(word) is { } text)
            {
                heard.Add(word with { Text = text, End = end });
            }
        }

        return heard;
    }

    public static FakeHypothesisDecoder ByInterval(
        IEnumerable<RecognizedWord> spoken, Func<RecognizedWord, string?>? partialAtEnd = null)
    {
        var words = spoken.ToList();
        return new FakeHypothesisDecoder(request => Heard(words, request.Start, request.End, partialAtEnd));
    }

    public static FakeHypothesisDecoder Silent() => new(_ => []);

    public Task<RecognitionHypothesis> DecodeAsync(
        ReadOnlyMemory<float> audio, TimeSpan offset, RecognitionOptions options, CancellationToken cancellationToken = default)
    {
        DecodeCall call;
        lock (_gate)
        {
            var request = new DecodeRequest(
                _calls.Count, offset, offset + TimeSpan.FromTicks(audio.Length * TestAudio.TicksPerSample), audio.Length, options);
            call = new DecodeCall(request);
            _calls.Add(call);
            _running++;
            MaxConcurrent = Math.Max(MaxConcurrent, _running);
        }

        Interlocked.Increment(ref _open);
        var task = DecodeCoreAsync(call, audio, offset, cancellationToken);

        // Zählt erst ab, wenn die Task abgeschlossen ist, also wenn P3 das Ergebnis erhalten kann
        _ = task.ContinueWith(
            _ =>
            {
                call.MarkReturned();
                Interlocked.Decrement(ref _open);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private async Task<RecognitionHypothesis> DecodeCoreAsync(
        DecodeCall call, ReadOnlyMemory<float> audio, TimeSpan offset, CancellationToken cancellationToken)
    {
        try
        {
            var intact = !VerifyAudio || TestAudio.Matches(audio.Span, offset);
            if (Hold is { } hold)
            {
                var wait = hold(call.Request);
                await (HonorCancellation ? wait.WaitAsync(cancellationToken) : wait);
            }

            if (HonorCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Der Speicher muss bis zum Ende des Aufrufs gültig und unverändert bleiben (Vertrag 1, T29)
            call.AudioIntact = intact && (!VerifyAudio || TestAudio.Matches(audio.Span, offset));
            var hypothesis = RawResponse?.Invoke(call.Request)
                ?? new RecognitionHypothesis(call.Request.Start, call.Request.End, _respond(call.Request));
            call.Words = hypothesis.Words;
            call.Completed = true;
            return hypothesis;
        }
        finally
        {
            lock (_gate)
            {
                _running--;
            }
        }
    }
}

/// <summary>
/// Sprachbereiche als feste Liste in Sitzungszeit; jeder Aufruf ohne Zustand (Vertrag 3). Auf Wunsch wirft die
/// Attrappe wie P8 bei Ausfall oder Zeitüberschreitung (T68).
/// </summary>
internal sealed class FakeSpeechDetector(params SpeechRegion[] regions) : ISpeechDetector
{
    private int _calls;
    private int _failures;

    public IReadOnlyList<SpeechRegion> Regions { get; } = regions;

    public int CallCount => Volatile.Read(ref _calls);

    /// <summary>Wirft bei jedem Aufruf, dessen Audio über diese Sitzungszeit hinausreicht (T68).</summary>
    public TimeSpan? FailAfter { get; set; }

    /// <summary>Anzahl Aufrufe, die geworfen haben.</summary>
    public int Failures => Volatile.Read(ref _failures);

    public static FakeSpeechDetector Speech(params (double Start, double End)[] regions) =>
        new([.. regions.Select(region => new SpeechRegion(TestAudio.Sec(region.Start), TestAudio.Sec(region.End)))]);

    /// <summary>Liefert die Bereiche, die das übergebene Audio schneiden, auf das Audio begrenzt; Doppelte bleiben doppelt.</summary>
    public IReadOnlyList<SpeechRegion> Detect(ReadOnlyMemory<float> audio, TimeSpan offset)
    {
        Interlocked.Increment(ref _calls);
        var end = offset + TimeSpan.FromTicks(audio.Length * TestAudio.TicksPerSample);
        if (FailAfter is { } limit && end > limit)
        {
            Interlocked.Increment(ref _failures);
            throw new TimeoutException("Künstlicher Detektorfehler");
        }

        return
        [
            .. Regions
                .Where(region => region.Start < end && offset < region.End)
                .Select(region => new SpeechRegion(
                    region.Start > offset ? region.Start : offset, region.End < end ? region.End : end)),
        ];
    }
}

/// <summary>Ein offenes Tor für blockierte Aufrufe.</summary>
internal sealed class TestGate
{
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Task => _open.Task;

    public void Open() => _open.TrySetResult();
}
