using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Freihand.Adapters.Recognition;

/// <summary>Whisper im Erkennungsprozess (Freihand.Recognizer: Whisper.net; Stufe A: Attrappe).</summary>
public interface IWhisperBackend : IDisposable
{
    /// <param name="audio">16 kHz mono, höchstens 30 s.</param>
    /// <param name="language">Whisper-Code, fest eingestellt.</param>
    /// <param name="prompt">Anfangstext aus den Vokabularhinweisen oder null.</param>
    /// <param name="fullContext">Auftrag länger als <c>AudioContext / 50</c> Sekunden: mit vollem Fenster (30 s) rechnen.</param>
    /// <param name="cancellationToken">Abbruch durch <c>cancel</c>.</param>
    Task<IReadOnlyList<WhisperSegmentData>> TranscribeAsync(
        ReadOnlyMemory<float> audio, string language, string? prompt, bool fullContext, CancellationToken cancellationToken);
}

/// <summary>Silero VAD im Erkennungsprozess (Freihand.Recognizer: über Whisper.net; Stufe A: Attrappe).</summary>
public interface IVadBackend : IDisposable
{
    /// <summary>Sprachbereiche relativ zum Anfang des Audios; der Zustand ist vor dem Aufruf zurückgesetzt.</summary>
    IReadOnlyList<(TimeSpan Start, TimeSpan End)> DetectFresh(ReadOnlyMemory<float> audio);
}

/// <summary>
/// Der Dienst im Erkennungsprozess; Protokoll Version 1 nach Kapitel 4.7 und 10.4. Je Kanal werden Anfragen in ihrer
/// Reihenfolge beantwortet; Meldungen tragen nur Codes, nie Text oder Audio (SI-01).
/// </summary>
public sealed class RecognizerService
{
    /// <summary>Höchstens so viele Samples je Anfrage (30 s).</summary>
    public const int MaxSamples = 480_000;

    private const long TicksPerSample = 625;

    private readonly Func<RecognizerConfiguration, (IWhisperBackend Whisper, IVadBackend Vad, RecognizerInfo Info)> _load;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IWhisperBackend? _whisper;
    private IVadBackend? _vad;
    private int _audioContext;

    /// <param name="load">Lädt nach geprüfter Prüfsumme; wirft <see cref="RecognizerException"/> (z. B. RuntimeUnavailable).</param>
    public RecognizerService(Func<RecognizerConfiguration, (IWhisperBackend Whisper, IVadBackend Vad, RecognizerInfo Info)> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        _load = load;
    }

    /// <summary>Endet, sobald <c>ready</c> gesendet ist; danach verbindet sich der Erkenner auf <c>detect</c>.</summary>
    /// <remarks>Ergänzung zu Kapitel 4.6 für den Ablauf im Erkennungsprozess.</remarks>
    public Task Ready => _ready.Task;

    /// <summary>Sendet <c>hello</c>, erwartet <c>configure</c>, prüft und lädt, beantwortet dann <c>decode</c>.</summary>
    public async Task RunDecodeChannelAsync(Stream connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var write = new SemaphoreSlim(1, 1);
        try
        {
            await PipeFraming.WriteAsync(connection, write, Hello("decode"), default, cancellationToken).ConfigureAwait(false);

            // Bis ready liest der Dienst nur configure; alles andere ist ein Rahmenfehler (Regel 4, Kapitel 10.4)
            var configure = await PipeFraming.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
            if (configure is null || configure.Type != "configure")
            {
                return;
            }

            var loaded = Configure(configure);
            if (loaded.Failure is { } code)
            {
                await PipeFraming.WriteAsync(connection, write, PipeFraming.Failed(null, code), default, cancellationToken).ConfigureAwait(false);
                return;
            }

            var ready = new JsonObject { ["type"] = "ready", ["info"] = JsonSerializer.SerializeToNode(loaded.Info, PipeFraming.Json) };
            await PipeFraming.WriteAsync(connection, write, ready, default, cancellationToken).ConfigureAwait(false);
            _ready.TrySetResult();

            await ServeDecodeAsync(connection, write, cancellationToken).ConfigureAwait(false);
        }
        catch (FrameException)
        {
            // Regel 1: Verbindung zu, keine Antwort
        }
        catch (IOException)
        {
            // Verbindung getrennt
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Erst nach <see cref="Ready"/>: sendet <c>hello</c> und beantwortet <c>detect</c>.</summary>
    public async Task RunDetectChannelAsync(Stream connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var write = new SemaphoreSlim(1, 1);
        try
        {
            await Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            await PipeFraming.WriteAsync(connection, write, Hello("detect"), default, cancellationToken).ConfigureAwait(false);
            while (await PipeFraming.ReadAsync(connection, cancellationToken).ConfigureAwait(false) is { } frame)
            {
                if (frame.Type != "detect")
                {
                    return;
                }

                var answer = Detect(frame);
                await PipeFraming.WriteAsync(connection, write, answer, default, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (FrameException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static JsonObject Hello(string channel) =>
        new() { ["type"] = "hello", ["protocol"] = PipeFraming.Protocol, ["channel"] = channel };

    /// <summary>Prüft beide Prüfsummen über gegen Schreiben gesperrte Handles und lädt, solange sie offen sind (Kapitel 5.4).</summary>
    private (RecognizerInfo? Info, RecognizerErrorCode? Failure) Configure(PipeFrame frame)
    {
        var configuration = PipeFraming.Read<RecognizerConfiguration>(frame.Header["config"]);

        if (configuration is null || configuration.RuntimeOrder is not { Count: > 0 } || configuration.ModelPath is null
            || configuration.VadModelPath is null || configuration.AudioContext <= 0)
        {
            return (null, RecognizerErrorCode.InvalidConfiguration);
        }

        FileStream? model = null;
        FileStream? vad = null;
        try
        {
            model = OpenChecked(configuration.ModelPath, configuration.ModelSha256, out var modelFailure);
            if (modelFailure is not null)
            {
                return (null, modelFailure);
            }

            vad = OpenChecked(configuration.VadModelPath, configuration.VadSha256, out var vadFailure);
            if (vadFailure is not null)
            {
                return (null, vadFailure);
            }

            var (whisper, detector, info) = _load(configuration);
            _whisper = whisper;
            _vad = detector;
            _audioContext = configuration.AudioContext;
            return (info, null);
        }
        catch (RecognizerException exception)
        {
            return (null, exception.Code);
        }
        catch (Exception)
        {
            return (null, RecognizerErrorCode.RuntimeUnavailable);
        }
        finally
        {
            vad?.Dispose();
            model?.Dispose();
        }
    }

    /// <summary>Öffnet nur zum Lesen, sperrt gegen Schreiben und prüft die SHA-256; der Handle bleibt bis nach dem Laden offen.</summary>
    private static FileStream? OpenChecked(string path, string? sha256, out RecognizerErrorCode? failure)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            failure = RecognizerErrorCode.ModelMissing;
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = RecognizerErrorCode.ModelChecksum;
            return null;
        }

        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
        {
            stream.Dispose();
            failure = RecognizerErrorCode.ModelChecksum;
            return null;
        }

        stream.Position = 0;
        failure = null;
        return stream;
    }

    /// <summary>Liest weiter, während ein Decode rechnet, damit <c>cancel</c> ankommt; beantwortet in Reihenfolge.</summary>
    private async Task ServeDecodeAsync(Stream connection, SemaphoreSlim write, CancellationToken cancellationToken)
    {
        var queue = Channel.CreateUnbounded<DecodeJob>(new() { SingleReader = true, SingleWriter = true });
        var jobs = new Dictionary<long, DecodeJob>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var worker = Task.Run(() => WorkAsync(queue.Reader, jobs, connection, write, stop.Token), CancellationToken.None);
        _ = StopReadingWhenWorkerEndsAsync(worker, stop);
        try
        {
            while (await PipeFraming.ReadAsync(connection, stop.Token).ConfigureAwait(false) is { } frame)
            {
                switch (frame.Type)
                {
                    case "decode":
                        var id = frame.Long("id") ?? throw new FrameException();
                        var job = new DecodeJob(id, frame);
                        lock (jobs)
                        {
                            jobs[id] = job;
                        }

                        queue.Writer.TryWrite(job);
                        break;
                    case "cancel":
                        // Kein Antwort; ein cancel für eine beantwortete oder unbekannte id bleibt ohne Wirkung (Regel 3)
                        if (frame.Long("id") is { } cancelId)
                        {
                            lock (jobs)
                            {
                                if (jobs.TryGetValue(cancelId, out var running))
                                {
                                    CancelSafely(running.Cancel);
                                }
                            }
                        }

                        break;
                    case "shutdown":
                        return;
                    default:
                        return;
                }
            }
        }
        finally
        {
            queue.Writer.TryComplete();
            CancelSafely(stop);
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // SI-01, P8 4.7: Backendfehler beim Beenden dürfen keinen Diagnosetext nach aussen tragen.
            }
            finally
            {
                lock (jobs)
                {
                    foreach (var job in jobs.Values) job.Cancel.Dispose();
                    jobs.Clear();
                }
            }
        }
    }

    private static async Task StopReadingWhenWorkerEndsAsync(Task worker, CancellationTokenSource stop)
    {
        try { await worker.ConfigureAwait(false); }
        catch (Exception) { }
        finally { CancelSafely(stop); }
    }

    // SI-01, P8 4.7/5.5: Fremde Backend-Callbacks dürfen keine Ausnahmetexte nach aussen tragen.
    private static void CancelSafely(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (Exception) { }
    }

    private async Task WorkAsync(
        ChannelReader<DecodeJob> queue, Dictionary<long, DecodeJob> jobs, Stream connection, SemaphoreSlim write, CancellationToken stop)
    {
        await foreach (var job in queue.ReadAllAsync(stop).ConfigureAwait(false))
        {
            var answer = await DecodeAsync(job, stop).ConfigureAwait(false);
            lock (jobs)
            {
                jobs.Remove(job.Id);
            }

            job.Cancel.Dispose();
            await PipeFraming.WriteAsync(connection, write, answer, default, stop).ConfigureAwait(false);
        }
    }

    private async Task<JsonObject> DecodeAsync(DecodeJob job, CancellationToken stop)
    {
        var frame = job.Frame;
        if (Validate(frame, out var offsetTicks, out var sampleCount) is { } invalid)
        {
            return PipeFraming.Failed(job.Id, invalid);
        }

        if (job.Cancel.IsCancellationRequested)
        {
            return Cancelled(job.Id);
        }

        if (sampleCount == 0)
        {
            return Hypothesis(job.Id, offsetTicks, offsetTicks, []);
        }

        var audio = PipeFraming.Floats(frame.Payload);
        var prompt = frame.String("prompt") is { Length: > 0 } text ? text : null;
        var language = frame.String("language") ?? string.Empty;
        var fullContext = sampleCount > (long)_audioContext * 320;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, job.Cancel.Token);
        try
        {
            var segments = await _whisper!.TranscribeAsync(audio, language, prompt, fullContext, linked.Token).ConfigureAwait(false);
            if (job.Cancel.IsCancellationRequested)
            {
                return Cancelled(job.Id);
            }

            var hypothesis = WhisperWordBuilder.Build(segments, TimeSpan.FromTicks(offsetTicks), (int)sampleCount, WordBuilderOptions.Default);
            var words = new JsonArray();
            foreach (var word in hypothesis.Words)
            {
                words.Add(new JsonObject
                {
                    ["text"] = word.Text,
                    ["startTicks"] = word.Start.Ticks,
                    ["endTicks"] = word.End.Ticks,
                    ["probability"] = word.Probability,
                });
            }

            return Hypothesis(job.Id, hypothesis.AudioStart.Ticks, hypothesis.AudioEnd.Ticks, words);
        }
        catch (OperationCanceledException) when (job.Cancel.IsCancellationRequested)
        {
            return Cancelled(job.Id);
        }
        catch (TokenAlignmentException)
        {
            return PipeFraming.Failed(job.Id, RecognizerErrorCode.TokenAlignment);
        }
        catch (Exception)
        {
            // Nur der Code, nie die Meldung der Ausnahme (SI-01)
            return job.Cancel.IsCancellationRequested ? Cancelled(job.Id) : PipeFraming.Failed(job.Id, RecognizerErrorCode.DecoderError);
        }
    }

    private JsonObject Detect(PipeFrame frame)
    {
        var id = frame.Long("id") ?? throw new FrameException();
        if (Validate(frame, out var offsetTicks, out var sampleCount) is { } invalid)
        {
            return PipeFraming.Failed(id, invalid);
        }

        var audioEnd = offsetTicks + (sampleCount * TicksPerSample);
        var regions = new JsonArray();
        if (sampleCount > 0)
        {
            IReadOnlyList<(TimeSpan Start, TimeSpan End)> found;
            try
            {
                found = _vad!.DetectFresh(PipeFraming.Floats(frame.Payload));
            }
            catch (Exception)
            {
                return PipeFraming.Failed(id, RecognizerErrorCode.VadError);
            }

            // Relativ → Sitzungszeit, auf das übergebene Audio begrenzt; Bereiche ohne Dauer entfallen (Kapitel 5.6)
            foreach (var (start, end) in found)
            {
                var from = Math.Clamp(offsetTicks + start.Ticks, offsetTicks, audioEnd);
                var to = Math.Clamp(offsetTicks + end.Ticks, offsetTicks, audioEnd);
                if (to > from)
                {
                    regions.Add(new JsonObject { ["startTicks"] = from, ["endTicks"] = to });
                }
            }
        }

        return new JsonObject { ["type"] = "regions", ["id"] = id, ["regions"] = regions };
    }

    /// <summary>Regel 2 und 5: Inhaltsfehler ergeben einen Code, die Verbindung bleibt.</summary>
    private static RecognizerErrorCode? Validate(PipeFrame frame, out long offsetTicks, out long sampleCount)
    {
        offsetTicks = frame.Long("offsetTicks") ?? -1;
        sampleCount = frame.Long("sampleCount") ?? -1;
        if (offsetTicks < 0 || offsetTicks % TicksPerSample != 0 || sampleCount < 0 || frame.Payload.LongLength != sampleCount * 4)
        {
            return RecognizerErrorCode.InvalidRequest;
        }

        return sampleCount > MaxSamples ? RecognizerErrorCode.AudioTooLong : null;
    }

    private static JsonObject Hypothesis(long id, long audioStart, long audioEnd, JsonArray words) => new()
    {
        ["type"] = "hypothesis",
        ["id"] = id,
        ["audioStartTicks"] = audioStart,
        ["audioEndTicks"] = audioEnd,
        ["words"] = words,
    };

    private static JsonObject Cancelled(long id) => new() { ["type"] = "cancelled", ["id"] = id };

    private sealed record DecodeJob(long Id, PipeFrame Frame)
    {
        public CancellationTokenSource Cancel { get; } = new();
    }
}
