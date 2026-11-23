using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Freihand.Benchmark;
using Whisper.net;
using Whisper.net.LibraryLoader;

// Messwerkzeug für den Machbarkeitstest
Console.OutputEncoding = System.Text.Encoding.UTF8;
var arguments = Arguments.Parse(args);
try
{
    switch (arguments.Command)
    {
        case "devices":
            Recorder.ListDevices();
            return 0;
        case "record":
            Recorder.Record(ReferencePath(arguments.Positional ?? Fail<string>("Dateiname fehlt, z. B. record R1.wav")), arguments.Int("device", 0));
            return 0;
        case "run":
            await RunAsync(arguments);
            return 0;
        case "chain":
            await Chain.RunAsync(arguments, ReferencePath, DataDirectory("Messung"));
            return 0;
        case "compare-chain":
            await DirectMetrics.CompareReportsAsync(arguments);
            return 0;
        case "direct-metrics":
            await DirectMetrics.RunAsync(arguments, ReferencePath, DataDirectory("Messung"));
            return 0;
        case "chain-devices":
            Chain.ListDevices();
            return 0;
        case "pruefung":
            await Pruefung.RunAsync(arguments, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Freihand"));
            return 0;
        default:
            PrintHelp();
            return arguments.Command is null or "help" ? 0 : 1;
    }
}
catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidDataException or IOException)
{
    Console.Error.WriteLine($"Fehler: {ex.Message}");
    return 1;
}

static async Task RunAsync(Arguments arguments)
{
    var modelPath = arguments.String("model") ?? Fail<string>("--model fehlt");
    var audioPath = ReferencePath(arguments.String("audio") ?? Fail<string>("--audio fehlt"));
    var settings = RunSettings.FromArguments(arguments);
    var (engine, mode, threads) = (settings.Engine, settings.Mode, settings.Threads);

    if (arguments.String("affinity") is { } mask)
    {
        // Nachstellung des Referenznotebook (QM-08)
        Process.GetCurrentProcess().ProcessorAffinity = (nint)long.Parse(mask, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    RuntimeOptions.RuntimeLibraryOrder = settings.RuntimeChoice switch
    {
        "cpu" => [RuntimeLibrary.Cpu],
        "cuda" => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12],
        "vulkan" => [RuntimeLibrary.Vulkan],
        _ => RuntimeOptions.RuntimeLibraryOrder,
    };

    var audio = WavFile.ReadMono16k(audioPath);
    if (settings.RepeatMinutes > 0)
    {
        audio = Repeat(audio, TimeSpan.FromMinutes(settings.RepeatMinutes));
    }

    var load = Stopwatch.StartNew();
    using var factory = engine == "whisper" ? WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = settings.RuntimeChoice != "cpu" }) : null;
    WhisperProcessor? processor = null;
    if (factory is not null)
    {
        var builder = factory.CreateBuilder()
            .WithLanguage(settings.Language!)
            .WithThreads(threads);
        if (settings.AudioContext > 0)
        {
            builder = builder.WithAudioContextSize(settings.AudioContext);
        }

        processor = builder.Build();
    }

    using var parakeet = engine == "parakeet" ? new ParakeetTranscriber(modelPath, threads) : null;
    using var processorScope = processor;
    load.Stop();

    var vadPath = settings.Vad is null ? null : arguments.String("vad");
    using var vadFactory = vadPath is null ? null : WhisperVadFactory.FromPath(vadPath);
    // Silero trennt schon bei kurzen Pausen und teilt lange Abschnitte selbst an der günstigsten Stelle; so schneidet
    // die Höchstlänge in SentenceSimulation nicht mitten im Wort. Bereiche mit kürzerem Abstand als --pause fasst
    // SentenceSimulation wieder zu einem Satz zusammen.
    using var vad = vadFactory?.CreateBuilder()
        .WithMinSilenceDuration(TimeSpan.FromSeconds(settings.Vad!.MinSilenceSeconds))
        .WithMaxSpeechDuration(TimeSpan.FromSeconds(settings.Vad.MaxSpeechSeconds))
        .WithMinSpeechDuration(TimeSpan.FromSeconds(settings.Vad.MinSpeechSeconds))
        .WithThreads(threads)
        .Build();

    // Silero läuft über whisper.cpp, auch neben Parakeet; seine Laufzeit steht darum getrennt im Ergebnis
    var whisperRuntime = RuntimeOptions.LoadedLibrary?.ToString();
    settings = settings with
    {
        AudioSeconds = audio.Length / (double)WavFile.SampleRate,
        Runtime = engine == "parakeet" ? "sherpa-onnx (cpu)" : whisperRuntime,
        VadRuntime = vad is null ? null : whisperRuntime,
    };
    var runtimeName = settings.Runtime;
    Console.WriteLine($"Laufzeit: {runtimeName}, Modus {mode}, Modell geladen in {load.Elapsed.TotalSeconds:F1} s, Audio {settings.AudioSeconds:F1} s");
    string? segmenter = null;
    SimulationResult result;
    if (mode == "satz")
    {
        ITranscriber transcriber = parakeet ?? (ITranscriber)new WhisperTranscriber(processor!);
        var sentenceOptions = settings.Sentence!;
        ISpeechSegmenter segmentation = vad is null ? new EnergySegmenter(sentenceOptions.PauseSeconds) : new SileroSegmenter(vad);
        var sentences = new SentenceSimulation(transcriber, sentenceOptions, segmentation);
        segmenter = sentences.Segmenter;
        result = await sentences.RunAsync(audio);
    }
    else
    {
        result = await new LiveSimulation(processor!, settings.Options).RunAsync(audio);
    }

    var report = Report.Create(result, SpeechStart(audio));
    // Prüfsummen erst nach der Messung, damit das Lesen der Modelle die Zeiten nicht beeinflusst
    var modelFiles = File.Exists(modelPath) ? [modelPath] : ParakeetTranscriber.ModelFiles(modelPath);
    var output = new
    {
        Tool = "Freihand.Benchmark",
        ToolVersion = typeof(Report).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        Timestamp = DateTimeOffset.Now,
        Machine = Environment.MachineName,
        Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
        LogicalProcessors = Environment.ProcessorCount,
        Affinity = arguments.String("affinity"),
        PowerOnBattery = PowerStatus.IsOnBattery(),
        Engine = engine,
        Runtime = runtimeName,
        RuntimeInfo = engine == "whisper" ? WhisperFactory.GetRuntimeInfo() : null,
        Model = Path.GetFileName(Path.TrimEndingDirectorySeparator(modelPath)),
        ModelBytes = modelFiles.Sum(f => new FileInfo(f).Length),
        ModelFiles = modelFiles.Select(Checksum).ToArray(),
        Audio = Path.GetFileName(audioPath),
        Mode = mode,
        Segmenter = segmenter,
        Settings = settings,
        VadModel = vadPath is null ? null : Checksum(vadPath),
        ModelLoadSeconds = load.Elapsed.TotalSeconds,
        PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
        Metrics = report,
        Transcript = string.Join(" ", result.Fixed.Select(s => s.Text)),
        result.Fixed,
        result.Steps,
    };

    var outPath = arguments.String("out") ?? Path.Combine(
        DataDirectory("Messung"),
        $"{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileNameWithoutExtension(audioPath)}_{Path.GetFileNameWithoutExtension(modelPath)}.json");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    // Umlaute lesbar lassen; die Datei bleibt lokal und wird nie in HTML eingebettet
    var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(output, json));

    Console.WriteLine(report.Summary());
    Console.WriteLine($"Ergebnis: {Path.GetFullPath(outPath)}");
}

// Sprachbeginn über die Lautstärke in 20-ms-Fenstern: Schwelle 12 dB über dem Grundrauschen der Aufnahme
// (10. Perzentil), mindestens -40 dBFS, und 100 ms am Stück darüber, damit ein Klick nicht zählt. Für die
// Machbarkeit genügt das; die Sprachaktivitätserkennung (ER-16) kommt mit dem Erkennungsprozess.
static double? SpeechStart(float[] audio)
{
    const int frame = WavFile.SampleRate / 50;
    const int sustained = 5;
    var levels = new double[audio.Length / frame];
    for (var f = 0; f < levels.Length; f++)
    {
        double sum = 0;
        for (var i = f * frame; i < (f + 1) * frame; i++)
        {
            sum += audio[i] * audio[i];
        }

        levels[f] = Math.Sqrt(sum / frame);
    }

    if (levels.Length == 0)
    {
        return null;
    }

    var floor = levels.Order().ElementAt(levels.Length / 10);
    var threshold = Math.Max(floor * 4, 0.01);
    var run = 0;
    for (var f = 0; f < levels.Length; f++)
    {
        run = levels[f] > threshold ? run + 1 : 0;
        if (run == sustained)
        {
            return (f - sustained + 1) * frame / (double)WavFile.SampleRate;
        }
    }

    return null;
}

// Hängt die Aufnahme mit 2 s Pause aneinander, bis die gewünschte Dauer erreicht ist (Dauerlauf)
static float[] Repeat(float[] audio, TimeSpan duration)
{
    var target = (int)(duration.TotalSeconds * WavFile.SampleRate);
    var gap = new float[2 * WavFile.SampleRate];
    var result = new List<float>(target + audio.Length);
    while (result.Count < target)
    {
        result.AddRange(audio);
        result.AddRange(gap);
    }

    return [.. result];
}

// Dateiname, Grösse und SHA-256, damit sich jeder Lauf einem Modell eindeutig zuordnen lässt
static object Checksum(string path)
{
    using var stream = File.OpenRead(path);
    return new { File = Path.GetFileName(path), Bytes = stream.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)) };
}

static string ReferencePath(string path) =>
    Path.IsPathRooted(path) || path.Contains(Path.DirectorySeparatorChar) || path.Contains(Path.AltDirectorySeparatorChar)
        ? path
        : Path.Combine(DataDirectory("Referenz"), path);

static string DataDirectory(string name) =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Freihand", name);

static T Fail<T>(string message) => throw new ArgumentException(message);

static void PrintHelp() => Console.WriteLine("""
    Freihand.Benchmark – Machbarkeitstest

      devices                         Mikrofone auflisten
      record <Datei.wav> [--device N] Referenzaufnahme, Enter beendet sie
      run --model <Modell> --audio <Datei.wav> [Optionen]
          --threads N       Rechenthreads (Standard: 4)
          --runtime R       auto, cpu, cuda, vulkan (Standard: auto)
          --affinity HEX    Prozessoren begrenzen, z. B. FF
          --language L      Sprache (Standard: de)
          --repeat MIN      Aufnahme zu einem Diktat von MIN Minuten aneinanderhängen
          --step S          Sekunden Audio je Durchlauf (Standard: 1.0)
          --window S        höchste Länge des nicht festen Audios (Standard: 25)
          --guard S         Abstand zum Audioende ohne Festschreiben (Standard: 1.0)
          --audio-ctx N     Rechenfenster von whisper.cpp, 50 je Sekunde (Standard: 1500 = 30 s)
          --engine E        whisper (Standard) oder parakeet; parakeet: --model ist der Modellordner, nur --mode satz
          --mode M          live (Standard) oder satz: jeder Satz einmal nach der Pause (ER-19)
          --pause S         Pause, nach der ein Satz endet, im Modus satz (Standard: 0.6)
          --vad PFAD        Silero-VAD-Modell für die Satzgrenzen (sonst Lautstärke)
          --vad-silence S   kürzeste Pause zwischen zwei Abschnitten für Silero (Standard: 0.3)
          --vad-max S       längster Abschnitt, den Silero selbst teilt (Standard: --window minus 2)
                            alle Zeiten in Sekunden, endlich und über 0; --guard und --repeat auch 0
          --out PFAD        Ergebnisdatei (Standard: %LOCALAPPDATA%\Freihand\Messung\…)
      chain --model <Modell> --vad <Silero> --audio <Datei.wav|mic> [Optionen]
                            Freihand-Kette: P3 mit dem P8-Erkenner im eigenen Prozess (P8-Konzept, Kapitel 7.3)
          --mode M          live (Standard) oder satz
          --runtime LISTE   Reihenfolge der Laufzeiten, z. B. cuda,cpu (Standard) oder vulkan,cpu
          --threads N       Rechenthreads (Standard: 4)
          --audio-context N Rechenfenster, 50 je Sekunde (Standard: 1500; small: 850)
          --language L      Profilsprache (Standard: de-CH)
          --hints A,B       Vokabularhinweise als Prompt
          --repeat-minutes M  WAV zu M Minuten aneinanderhängen (RM07)
          --device N|ID     bei mic: Gerät aus chain-devices (Standard: Standardgerät)
          --speech-start S  markierter Sprachbeginn in Sekunden für die Ersttext-Latenz
          --max-utterance S Höchstlänge einer Äusserung, 1 bis 20 (Standard: 15, ER-01); live Fenster ebenso lang
          --vad-threshold T Silero-Schwelle, zwischen 0 und 1 (Standard: 0.5; nur für Messungen, ER-16)
          --lead S          Vorlauf vor dem Sprachbeginn in Sekunden (Standard: 0; App: 0.5, P11 S3)
          --seconds S       bei mic: Dauer (Standard: bis Enter)
          --out PFAD        Ergebnisdatei
      compare-chain --live JSON --sentence JSON [--reference UTF8] [--out JSON]
                            RM03: normalisierter Wortabstand; optional WER gegen lokale Referenz
      direct-metrics --model M --vad V --audio WAV --measurement offset|split
          --start-sample N  RM02: Beginn des unveränderten Originalausschnitts (Standard 0)
          --end-sample N    RM02: exklusives Endsample (Standard Dateiende); höchstens 480000 Samples
          --sentences JSON  RM03: alle Satzintervalle [[Startsample,Endsample],…], extern markiert
                            Modell-/Laufzeitoptionen wie chain; RM02: Decoderaufrufe 0,600,0
      chain-devices         Aufnahmegeräte für chain mit Kennung (WASAPI)
      pruefung --model <Modell> [--ordner PFAD] [--ab NAME] [--wav PFAD] [--runtime cpu] [--out JSONL]
                            P11 S2: Prüfaufnahmen dieses Geräts entschlüsseln und je Abschnitt neu erkennen
                            (abschnitte, vorlauf, zusammenhang, ganz); --ab ab diesem Dateinamen, --wav Ton als WAV
                            für chain. Ergebnis enthält Text: nur lokal, nach der Auswertung löschen

    Dateinamen ohne Pfad liegen unter %LOCALAPPDATA%\Freihand\Referenz\.
    """);
