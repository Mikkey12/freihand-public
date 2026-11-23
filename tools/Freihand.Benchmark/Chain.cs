using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Freihand.Adapters.Audio;
using Freihand.Adapters.Recognition;
using Freihand.Core.Live;
using Freihand.Core.Platform;
using Freihand.Core.Recognition;
using Freihand.Windows.Audio;
using Freihand.Windows.Recognition;

namespace Freihand.Benchmark;

/// <summary>Einstellungen von <c>chain</c> (P8-Konzept, Kapitel 3.2 und 7.3).</summary>
/// <param name="Audio">WAV-Datei (16 kHz mono) oder <c>mic</c>.</param>
/// <param name="Device">Bei <c>mic</c>: Kennung oder Nummer aus <c>chain-devices</c>; leer = Standardgerät.</param>
/// <param name="Seconds">Bei <c>mic</c>: Aufnahmedauer; 0 = bis Enter.</param>
internal sealed record ChainSettings(
    string Model,
    string Vad,
    string Audio,
    LiveMode Mode,
    string Language,
    IReadOnlyList<string> Hints,
    IReadOnlyList<RecognizerRuntime> Runtimes,
    int Threads,
    int AudioContext,
    double RepeatMinutes,
    string? Device,
    double Seconds)
{
    /// <summary>Markierter Sprachbeginn in der Aufnahme; ohne Angabe keine Ersttext-Latenz behaupten (RM05).</summary>
    public double? SpeechStartSeconds { get; init; }

    /// <summary>
    /// Höchstlänge einer Äusserung (ER-01, Startwert 15 s seit 03.11.2026); höchstens 20 s, die Grenze des Erkenners
    /// (P8 Kapitel 5.5). Live wächst das Fenster mit, sonst lehnt P3 die Optionen ab (E1).
    /// </summary>
    public double MaxUtteranceSeconds { get; init; } = 15;

    /// <summary>Vorlauf vor dem Sprachbeginn in Sekunden (P11 S3, <see cref="LiveOptions.Lead"/>); Standard 0.</summary>
    public double LeadSeconds { get; init; }

    /// <summary>Silero-Schwelle für Messungen (ER-16, kurze Geräusche); Standard wie <see cref="VadSettings.Default"/>.</summary>
    public float VadThreshold { get; init; } = VadSettings.Default.Threshold;

    public static ChainSettings FromArguments(Arguments arguments)
    {
        var mode = (arguments.String("mode") ?? "live") switch
        {
            "live" => LiveMode.Live,
            "satz" => LiveMode.Sentence,
            var other => throw new ArgumentException($"--mode {other} unbekannt; erlaubt: live, satz"),
        };
        var runtimes = (arguments.String("runtime") ?? "cuda,cpu")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name switch
            {
                "cuda" => RecognizerRuntime.Cuda,
                "vulkan" => RecognizerRuntime.Vulkan,
                "cpu" => RecognizerRuntime.Cpu,
                _ => throw new ArgumentException($"--runtime {name} unbekannt; erlaubt: cuda, vulkan, cpu"),
            })
            .ToArray();
        if (runtimes.Length == 0 || runtimes.Distinct().Count() != runtimes.Length)
        {
            throw new ArgumentException("--runtime braucht eine Liste ohne Doppelte, z. B. cuda,cpu");
        }

        var hints = (arguments.String("hints") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var language = arguments.String("language") ?? "de-CH";
        LanguageMapping.ToWhisper(language);

        return new ChainSettings(
            arguments.String("model") ?? throw new ArgumentException("--model fehlt"),
            arguments.String("vad") ?? throw new ArgumentException("--vad fehlt (ggml-silero-v5.1.2.bin)"),
            arguments.String("audio") ?? throw new ArgumentException("--audio fehlt (WAV-Datei oder mic)"),
            mode,
            language,
            hints,
            runtimes,
            arguments.Int("threads", 4, 1),
            arguments.Int("audio-context", 1500, 1),
            arguments.Positive("repeat-minutes", 0, allowZero: true),
            arguments.String("device"),
            arguments.Positive("seconds", 0, allowZero: true))
        {
            SpeechStartSeconds = arguments.String("speech-start") is null ? null : arguments.Positive("speech-start", 0, allowZero: true),
            MaxUtteranceSeconds = MaxUtterance(arguments.Positive("max-utterance", 15)),
            LeadSeconds = arguments.Positive("lead", 0, allowZero: true),
            VadThreshold = CheckedVadThreshold(arguments.Double("vad-threshold", VadSettings.Default.Threshold)),
        };
    }

    private static float CheckedVadThreshold(double value) => value is > 0 and < 1
        ? (float)value
        : throw new ArgumentException("--vad-threshold zwischen 0 und 1 (ausschliesslich)");

    private static double MaxUtterance(double seconds) => seconds is >= 1 and <= 20
        ? seconds
        : throw new ArgumentException("--max-utterance zwischen 1 und 20 s (Grenze des Erkenners)");

    /// <summary>Startwerte von P3 Vertrag 4 im gewählten Modus; die Höchstlänge nach <see cref="MaxUtteranceSeconds"/>.</summary>
    public LiveOptions LiveOptions() => new(
        Mode,
        Step: TimeSpan.FromSeconds(0.5),
        Window: TimeSpan.FromSeconds(Math.Max(10, MaxUtteranceSeconds)),
        PauseThreshold: TimeSpan.FromSeconds(0.6),
        MaxUtterance: TimeSpan.FromSeconds(MaxUtteranceSeconds),
        CutGuard: TimeSpan.FromSeconds(0.3),
        BacklogWarning: TimeSpan.FromSeconds(10),
        BacklogLimit: TimeSpan.FromSeconds(120),
        Filter: new HallucinationFilter([], 0.5f))
    {
        Lead = TimeSpan.FromSeconds(LeadSeconds),
    };
}

/// <summary>
/// <c>chain</c>: die Freihand-Kette aus P3 <see cref="LiveTranscription"/> und dem P8-Erkenner (eigener Prozess über
/// <see cref="RecognizerSupervisor"/>) auf einer WAV-Datei im Echtzeittakt oder am Mikrofon (P8-Konzept, Kapitel 3.2).
/// Ergebnis lokal als JSON; ins Repository kommen nur Kennzahlen, nie Text oder Aufnahmen.
/// </summary>
internal static class Chain
{
    private const int SampleRate = 16_000;
    private const int ChunkSamples = 3_200;

    public static async Task RunAsync(Arguments arguments, Func<string, string> referencePath, string outputDirectory)
    {
        var settings = ChainSettings.FromArguments(arguments);
        var fromMic = settings.Audio == "mic";
        var audioPath = fromMic ? null : referencePath(settings.Audio);
        // RM03/QM-02: Identität vor der Messung bestimmen; gleiche Dateinamen genügen nicht.
        var audioHash = audioPath is null ? null : Sha256(audioPath);
        var recognition = new RecognitionOptions(settings.Language, settings.Hints);

        var configuration = new RecognizerConfiguration(
            settings.Model, Sha256(settings.Model), settings.Runtimes, settings.Threads, settings.AudioContext,
            settings.Vad, Sha256(settings.Vad), VadSettings.Default with { Threshold = settings.VadThreshold });
        var launcher = new ProcessTracker(RecognizerLauncher.ForExecutable(Path.Combine(AppContext.BaseDirectory, "Freihand.Recognizer.exe")));

        var start = Stopwatch.StartNew();
        await using var supervisor = new RecognizerSupervisor(launcher, configuration, ClientTimeouts.Default, TimeProvider.System);
        supervisor.Start();
        await using var session = await supervisor.StartSessionAsync(recognition, settings.LiveOptions());
        var ready = start.Elapsed;

        Console.WriteLine($"Erkenner bereit: {session.Info.ModelFile}, Laufzeit {session.Info.Runtime}"
            + (session.Info.RuntimeFallback ? " (Rückfall)" : string.Empty) + $", nach {ready.TotalSeconds:F1} s");

        // RM05: Dateilesen und Blockvorbereitung gehören nicht zur simulierten Sitzungszeit.
        var clock = new Stopwatch();
        var decoder = new TimedDecoder(session.Decoder, () => clock.Elapsed);
        var detector = new TimedDetector(session.Detector, () => clock.Elapsed);
        var live = new LiveTranscription(decoder, detector, recognition, settings.LiveOptions());

        ICaptureSession? capture = null;
        IAsyncEnumerable<AudioChunk>? chunks = null;
        long? plannedAudioTicks = null;
        if (fromMic)
        {
            var audio = WasapiAudio.Create();
            var device = ChooseDevice(audio, settings.Device);
            clock.Start();
            capture = audio.Open(device);
        }
        else
        {
            var samples = WavFile.ReadMono16k(audioPath!);
            var count = settings.RepeatMinutes > 0
                ? TimeSpan.FromMinutes(settings.RepeatMinutes).Ticks / 625
                : samples.LongLength;
            plannedAudioTicks = checked(count * 625);
            chunks = PacedAsync(PlaybackChunks(samples, count), clock);
            clock.Start();
        }

        // RM07: Dauer der laufenden Kette, ohne Modellladen und Dateivorbereitung.
        await using var memory = new MemorySampler(() => (MemorySampler.ReadProcess(Environment.ProcessId, 0), launcher.CurrentMemory()), TimeProvider.System);
        memory.Start();

        var fixedSegments = new List<FixedSegment>();
        var notices = new Dictionary<LiveNoticeKind, int>();
        double? firstPartial = null;
        string? failure = null;
        async Task CollectAsync()
        {
            // Reader-Erzeugung und Konsolenausgabe sind ebenfalls durch WithCaptureAsync geschützt.
            if (capture is not null)
            {
                chunks = capture.ReadAsync();
                Console.WriteLine($"Aufnahme von „{capture.Device.Name}“; " + (settings.Seconds > 0 ? $"{settings.Seconds} s." : "Enter beendet."));
            }
            await foreach (var item in live.RunAsync(chunks!))
            {
                switch (item)
                {
                    case ProvisionalText provisional:
                        firstPartial = FirstPartial(firstPartial, provisional.Text, clock.Elapsed.TotalSeconds, settings.SpeechStartSeconds);
                        break;
                    case UtteranceCompleted { Utterance: var utterance }:
                        fixedSegments.Add(new FixedSegment(
                            string.Join(" ", utterance.Words.Select(word => word.Text)),
                            utterance.Start.TotalSeconds,
                            utterance.End.TotalSeconds,
                            clock.Elapsed.TotalSeconds,
                            AtEnd: utterance.Ending == UtteranceEnd.StreamEnd,
                            LengthCut: utterance.Ending == UtteranceEnd.LengthCut));
                        break;
                    case LiveNotice notice:
                        notices[notice.Kind] = notices.GetValueOrDefault(notice.Kind) + 1;
                        break;
                }
            }
        }

        try
        {
            if (capture is null) await CollectAsync();
            else await WithCaptureAsync(capture, CollectAsync, token => WaitForStopAsync(settings.Seconds, token));
        }
        catch (Exception exception) when (exception is RecognizerUnavailableException or RecognizerException or InvalidOperationException)
        {
            // Nur der Typ, nie eine Meldung mit Inhalt (SI-01)
            failure = exception.GetType().Name;
        }

        CaptureStatus? captureStatus = null;
        if (capture is not null)
        {
            captureStatus = capture.Status;
        }

        var audioSeconds = live.AcceptedEnd.TotalSeconds;
        var detectSummary = detector.Summary(audioSeconds);
        var result = new SimulationResult(audioSeconds, decoder.Steps, fixedSegments, firstPartial,
            SegmentationSeconds: detectSummary.TotalSeconds, SegmentationSuspendedSeconds: null);
        var report = CreateReport(result, settings.SpeechStartSeconds);
        var memoryReport = await memory.FinishAsync();
        var recognizerProcess = launcher.PeakWorkingSet();
        var output = new
        {
            Tool = "Freihand.Benchmark chain",
            ToolVersion = typeof(Chain).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().SingleOrDefault()?.InformationalVersion,
            Timestamp = DateTimeOffset.Now,
            Machine = Environment.MachineName,
            Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            PowerOnBattery = PowerStatus.IsOnBattery(),
            Settings = settings with { Model = Path.GetFileName(settings.Model), Vad = Path.GetFileName(settings.Vad) },
            Recognizer = session.Info,
            ReadySeconds = ready.TotalSeconds,
            FirstPartialAtSeconds = firstPartial,
            Audio = fromMic ? "mic" : Path.GetFileName(audioPath),
            AudioSha256 = audioHash,
            VadSha256 = configuration.VadSha256,
            VadSettings = configuration.Vad,
            PlannedAudioSeconds = plannedAudioTicks is { } ticks ? (double?)TimeSpan.FromTicks(ticks).TotalSeconds : null,
            AcceptedAudioSeconds = audioSeconds,
            Incomplete = plannedAudioTicks is { } planned && live.AcceptedEnd.Ticks < planned,
            Capture = captureStatus,
            Failure = failure,
            Notices = notices.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
            P3InvalidHypotheses = live.InvalidHypotheses,
            TokenAlignmentFailures = decoder.Count(RecognizerErrorCode.TokenAlignment),
            AudioTooLongFailures = decoder.Count(RecognizerErrorCode.AudioTooLong),
            DecoderFailures = decoder.Failures,
            DecoderCancelled = decoder.Cancelled,
            Detect = detectSummary,
            Memory = memoryReport,
            PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            RecognizerPeakWorkingSetBytes = recognizerProcess,
            Metrics = report,
            Transcript = string.Join(" ", fixedSegments.Select(segment => segment.Text)),
            Fixed = fixedSegments,
            decoder.Steps,
        };

        var name = fromMic ? "mic" : Path.GetFileNameWithoutExtension(audioPath);
        var outPath = arguments.String("out") ?? Path.Combine(
            outputDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}_chain_{name}_{Path.GetFileNameWithoutExtension(settings.Model)}_{settings.Mode}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(output, json));

        Console.WriteLine(report.Summary());
        Console.WriteLine($"Meldungen: {(notices.Count == 0 ? "keine" : string.Join(", ", notices.Select(pair => $"{pair.Key} {pair.Value}")))}"
            + $"; TokenAlignment {output.TokenAlignmentFailures}; Detect p95 {output.Detect.P95Milliseconds:F1} ms");
        if (captureStatus is not null)
        {
            Console.WriteLine($"Aufnahme: Start {captureStatus.StartLatency?.TotalMilliseconds:F0} ms, Stopp {captureStatus.StopReason}, ungelesen {captureStatus.Unread.TotalSeconds:F1} s");
        }

        Console.WriteLine($"Ergebnis: {Path.GetFullPath(outPath)}");
    }

    /// <summary>Listet die Aufnahmegeräte mit Nummer und Kennung.</summary>
    public static void ListDevices()
    {
        var audio = WasapiAudio.Create();
        var devices = audio.Devices;
        for (var i = 0; i < devices.Count; i++)
        {
            var mark = devices[i].Id == audio.DefaultDevice?.Id ? " (Standard)" : string.Empty;
            Console.WriteLine($"{i}: {devices[i].Name}{mark}  {devices[i].Id}");
        }
    }

    /// <summary>16 kHz mono float → Blöcke zu 200 ms PCM16 mit lückenlosen Offsets (P3 Vertrag 1).</summary>
    internal static IReadOnlyList<AudioChunk> ToChunks(float[] samples)
    {
        var chunks = new List<AudioChunk>();
        for (var start = 0; start < samples.Length; start += ChunkSamples)
        {
            var length = Math.Min(ChunkSamples, samples.Length - start);
            var pcm = new byte[length * 2];
            for (var i = 0; i < length; i++)
            {
                var value = Math.Clamp(Math.Round(samples[start + i] * 32_768.0), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)value);
            }

            chunks.Add(new AudioChunk(pcm, SampleRate, TimeSpan.FromTicks(start * 625L)));
        }

        return chunks;
    }

    private static async IAsyncEnumerable<AudioChunk> PacedAsync(
        IEnumerable<AudioChunk> chunks, Stopwatch clock, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in chunks)
        {
            // Wie WASAPI: ein Block steht erst am Ende seiner Audiozeit bereit
            var due = chunk.Offset + TimeSpan.FromTicks(chunk.Pcm16Mono.Length / 2 * 625L);
            var wait = due - clock.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            yield return chunk;
        }
    }

    private static AudioDevice ChooseDevice(IAudioCapture audio, string? choice)
    {
        if (choice is null)
        {
            return audio.DefaultDevice ?? throw new ArgumentException("kein Standardgerät für die Aufnahme");
        }

        var devices = audio.Devices;
        if (int.TryParse(choice, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < devices.Count)
        {
            return devices[index];
        }

        return devices.FirstOrDefault(device => device.Id == choice) ?? throw new ArgumentException($"--device {choice} nicht gefunden");
    }

    private static async Task WaitForStopAsync(double seconds, CancellationToken cancellationToken)
    {
        if (seconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        }
        else
        {
            await Task.Run(Console.ReadLine, CancellationToken.None).WaitAsync(cancellationToken);
        }
    }

    /// <summary>ER-17: Besitz von Aufnahme und Stoppaufgabe, auch bei unerwartetem Fehler des Verbrauchers.</summary>
    internal static async Task WithCaptureAsync(ICaptureSession capture, Func<Task> consume, Func<CancellationToken, Task> stopSignal)
    {
        using var stop = new CancellationTokenSource();
        async Task StopWhenSignalledAsync()
        {
            try { await stopSignal(stop.Token).WaitAsync(stop.Token); }
            finally
            {
                // Auch ein Fehler der Eingabe (z. B. geschlossene Konsole) beendet die Aufnahme.
                if (!stop.IsCancellationRequested) capture.Stop();
            }
        }

        var stopping = StopWhenSignalledAsync();
        try
        {
            await consume();
        }
        finally
        {
            try
            {
                await stop.CancelAsync();
                try { await stopping; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            finally
            {
                try { capture.Stop(); }
                finally { await capture.DisposeAsync(); }
            }
        }
    }

    /// <summary>RM07: Wiederholung samt 2 s Pause blockweise; kein stundenlanges Audio im Arbeitsspeicher.</summary>
    internal static IEnumerable<AudioChunk> PlaybackChunks(float[] audio, long sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        var period = audio.LongLength + 2L * SampleRate;
        for (long start = 0; start < sampleCount; start += ChunkSamples)
        {
            var length = (int)Math.Min(ChunkSamples, sampleCount - start);
            var pcm = new byte[length * 2];
            for (var i = 0; i < length; i++)
            {
                var position = (start + i) % period;
                var sample = position < audio.LongLength ? audio[position] : 0;
                var value = (short)Math.Clamp(Math.Round(sample * 32_768.0), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
            }

            yield return new AudioChunk(pcm, SampleRate, TimeSpan.FromTicks(start * 625));
        }
    }

    /// <summary>RM05: Ersttext ist die tatsächlich sichtbare P3-Vorschau, nicht die erste Decoderantwort.</summary>
    internal static Report CreateReport(SimulationResult result, double? speechStart) =>
        Report.Create(result, speechStart) with
        {
            FirstPartialSeconds = result.FirstPartialAtSeconds is { } first && speechStart is { } start && first >= start
                ? first - start : null,
        };

    /// <summary>ER-16/RM05: Leeren Text und Vorschauen vor dem markierten Sprachbeginn nicht als Ersttext zählen.</summary>
    internal static double? FirstPartial(double? first, string text, double at, double? speechStart) =>
        first ?? (text.Length > 0 && (speechStart is null || at >= speechStart) ? at : null);

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Misst jeden Decode als Durchlauf für den Bericht und zählt Fehlercodes.</summary>
    internal sealed class TimedDecoder(IHypothesisDecoder inner, Func<TimeSpan> elapsed) : IHypothesisDecoder
    {
        private readonly List<StepRecord> _steps = [];
        private readonly Dictionary<RecognizerErrorCode, int> _codes = [];

        public IReadOnlyList<StepRecord> Steps
        {
            get
            {
                lock (_steps)
                {
                    return [.. _steps];
                }
            }
        }

        public int Failures { get; private set; }

        public int Cancelled { get; private set; }

        public int Count(RecognizerErrorCode code)
        {
            lock (_steps)
            {
                return _codes.GetValueOrDefault(code);
            }
        }

        public async Task<RecognitionHypothesis> DecodeAsync(
            ReadOnlyMemory<float> audio, TimeSpan offset, RecognitionOptions options, CancellationToken cancellationToken = default)
        {
            var started = elapsed();
            RecognitionHypothesis? hypothesis = null;
            try
            {
                hypothesis = await inner.DecodeAsync(audio, offset, options, cancellationToken);
                return hypothesis;
            }
            catch (Exception exception)
            {
                lock (_steps)
                {
                    if (exception is OperationCanceledException) Cancelled++;
                    else Failures++;
                    if (exception is RecognizerException coded)
                        _codes[coded.Code] = _codes.GetValueOrDefault(coded.Code) + 1;
                }

                throw;
            }
            finally
            {
                var finished = elapsed();
                lock (_steps)
                {
                    // RM01/RM05: Fehlversuche kosten ebenfalls Zeit, sind aber keine gültigen Ergebnisse.
                    _steps.Add(new StepRecord(
                        (offset + TimeSpan.FromTicks(audio.Length * 625L)).TotalSeconds,
                        started.TotalSeconds, finished.TotalSeconds, (finished - started).TotalSeconds,
                        hypothesis?.Words.Count ?? 0,
                        hypothesis?.Words.Count > 0 ? hypothesis.Words[^1].End.TotalSeconds : null,
                        SuspendedSeconds: null));
                }
            }
        }
    }

    /// <summary>Zeit je Detect für RM09.</summary>
    internal sealed class TimedDetector(ISpeechDetector inner, Func<TimeSpan> elapsed) : ISpeechDetector
    {
        private readonly List<double> _milliseconds = [];
        private int _failures;

        public IReadOnlyList<SpeechRegion> Detect(ReadOnlyMemory<float> audio, TimeSpan offset)
        {
            var started = elapsed();
            try
            {
                return inner.Detect(audio, offset);
            }
            catch
            {
                lock (_milliseconds) { _failures++; }
                throw;
            }
            finally
            {
                lock (_milliseconds) { _milliseconds.Add((elapsed() - started).TotalMilliseconds); }
            }
        }

        public DetectSummary Summary(double audioSeconds)
        {
            lock (_milliseconds)
            {
                var sorted = _milliseconds.Order().ToArray();
                var p95 = sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1)];
                var share = audioSeconds > 0 ? sorted.Sum() / 1000 / audioSeconds : 0;
                return new DetectSummary(sorted.Length, p95, share, sorted.Sum() / 1000, _failures);
            }
        }
    }

    /// <summary>Merkt sich den gestarteten Erkenner, um seinen Speicher auszuweisen (RM07, RM14).</summary>
    private sealed class ProcessTracker(IRecognizerLauncher inner) : IRecognizerLauncher
    {
        private int _processId;
        private int _generation;
        private readonly object _gate = new();

        public MemoryReading CurrentMemory()
        {
            lock (_gate) return MemorySampler.ReadProcess(_processId, _generation);
        }

        public async Task<LaunchedRecognizer> LaunchAsync(CancellationToken cancellationToken = default)
        {
            var launched = await inner.LaunchAsync(cancellationToken);
            lock (_gate)
            {
                _processId = launched.ProcessId;
                _generation++;
            }
            return launched;
        }

        public long? PeakWorkingSet()
        {
            try
            {
                using var process = Process.GetProcessById(_processId);
                return process.PeakWorkingSet64;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}

/// <param name="Calls">Zahl der Aufrufe.</param>
/// <param name="P95Milliseconds">Zeit je Detect, 95. Perzentil.</param>
/// <param name="ShareOfAudio">Summe der Detect-Zeiten geteilt durch Audiozeit.</param>
internal sealed record DetectSummary(int Calls, double P95Milliseconds, double ShareOfAudio, double TotalSeconds, int Failures);
