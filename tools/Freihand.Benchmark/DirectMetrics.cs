using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using Freihand.Adapters.Recognition;
using Freihand.Core.Recognition;
using Freihand.Core.Quality;
using Freihand.Windows.Recognition;

namespace Freihand.Benchmark;

// RM02/RM03: Direkter Decoder ohne P3-Filter, mit unverändertem Audio und exakten Samplezeiten.
internal static class DirectMetrics
{
    internal static void CheckLength(int samples)
    {
        if (samples > RecognizerService.MaxSamples)
            throw new InvalidDataException("RM02/RM03 unmessbar: vollständiges Intervall überschreitet die 30-s-Grenze des Erkenners; keine Kürzung.");
    }
    internal const long SampleTicks = 625;
    internal sealed record OffsetResult(int Words, bool ShiftExact, bool RepeatExact);
    internal sealed record Distance(int ReferenceWords, int ComparedWords, int Edits, int EqualWords, double? WordErrorRate, double? EqualShare);
    internal sealed record SplitResult(int StartSample, int EndSample, int SplitSample, Distance Words);

    // RM02: Benannter Originalausschnitt; keine Rundung, Umrechnung oder Änderung der Samples.
    internal static (ReadOnlyMemory<float> Audio, int Start, int End) SelectOffsetAudio(float[] audio, Arguments arguments)
    {
        static int Sample(Arguments args, string name, int fallback) => args.String(name) is not { } text ? fallback :
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value :
            throw new ArgumentException($"--{name} verlangt eine nichtnegative ganze Samplezahl.");
        var start = Sample(arguments, "start-sample", 0);
        var end = Sample(arguments, "end-sample", audio.Length);
        if (start < 0 || end > audio.Length || end <= start)
            throw new ArgumentException("RM02: ungültiger oder leerer Originalausschnitt.");
        CheckLength(end - start);
        return (audio.AsMemory(start, end - start), start, end);
    }

    // RM03: Auch syntaktisch gültiges JSON muss vollständige Satzmarkierungen enthalten.
    internal static int[][] ParseIntervals(string json, int audioSamples)
    {
        var intervals = JsonSerializer.Deserialize<int[]?[]>(json);
        if (intervals is null || intervals.Length == 0 || intervals.Any(x =>
            x is null || x.Length != 2 || x[0] < 0 || x[1] <= x[0] || x[1] > audioSamples))
            throw new ArgumentException("Satzmarkierungen fehlen oder sind ungültig.");
        return intervals.Select(x => x!).ToArray();
    }

    internal static bool Equivalent(RecognitionHypothesis first, RecognitionHypothesis next, TimeSpan shift) =>
        next.AudioStart == first.AudioStart + shift && next.AudioEnd == first.AudioEnd + shift &&
        first.Words.Count == next.Words.Count && first.Words.Zip(next.Words).All(pair =>
            pair.First.Text == pair.Second.Text && pair.First.Probability == pair.Second.Probability &&
            pair.Second.Start == pair.First.Start + shift && pair.Second.End == pair.First.End + shift);

    internal static async Task<OffsetResult> OffsetAsync(IHypothesisDecoder decoder, ReadOnlyMemory<float> audio, RecognitionOptions options)
    {
        CheckLength(audio.Length);
        var first = await decoder.DecodeAsync(audio, TimeSpan.Zero, options);
        var shifted = await decoder.DecodeAsync(audio, TimeSpan.FromSeconds(600), options);
        var repeated = await decoder.DecodeAsync(audio, TimeSpan.Zero, options);
        return new(first.Words.Count, Equivalent(first, shifted, TimeSpan.FromSeconds(600)), Equivalent(first, repeated, TimeSpan.Zero));
    }

    // RM03: Levenshtein-Abstand; bei gleichem Abstand maximale Zahl unveränderter Wörter.
    internal static Distance Compare(IReadOnlyList<string> reference, IReadOnlyList<string> compared)
    {
        var matrix = new (int Edits, int Equal)[reference.Count + 1, compared.Count + 1];
        for (var i = 0; i <= reference.Count; i++) matrix[i, 0] = (i, 0);
        for (var j = 0; j <= compared.Count; j++) matrix[0, j] = (j, 0);
        for (var i = 1; i <= reference.Count; i++)
        for (var j = 1; j <= compared.Count; j++)
        {
            var same = reference[i - 1] == compared[j - 1];
            var diagonal = matrix[i - 1, j - 1];
            var deletion = matrix[i - 1, j];
            var insertion = matrix[i, j - 1];
            matrix[i, j] = new[] { (diagonal.Edits + (same ? 0 : 1), diagonal.Equal + (same ? 1 : 0)),
                (deletion.Edits + 1, deletion.Equal), (insertion.Edits + 1, insertion.Equal) }
                .OrderBy(x => x.Item1).ThenByDescending(x => x.Item2).First();
        }
        var result = matrix[reference.Count, compared.Count];
        return new(reference.Count, compared.Count, result.Edits, result.Equal,
            reference.Count == 0 ? null : result.Edits / (double)reference.Count,
            reference.Count == 0 ? null : result.Equal / (double)reference.Count);
    }

    internal static async Task<SplitResult> SplitAsync(IHypothesisDecoder decoder, ReadOnlyMemory<float> audio,
        int start, int end, RecognitionOptions options)
    {
        if (start < 0 || end > audio.Length || end <= start) throw new ArgumentException("Ungültiges Satzintervall.");
        CheckLength(end - start);
        var offset = TimeSpan.FromTicks(start * SampleTicks);
        var whole = await decoder.DecodeAsync(audio[start..end], offset, options);
        var candidates = whole.Words.Select(word => word.End.Ticks).Where(ticks =>
            ticks % SampleTicks == 0 && ticks > start * SampleTicks && ticks < end * SampleTicks).ToArray();
        if (candidates.Length == 0) throw new InvalidDataException("RM03: kein inneres samplegenaues Wortende; Messung unvollständig.");
        var split = checked((int)(candidates[candidates.Length / 2] / SampleTicks));
        var left = await decoder.DecodeAsync(audio[start..split], offset, options);
        var right = await decoder.DecodeAsync(audio[split..end], TimeSpan.FromTicks(split * SampleTicks), options);
        return new(start, end, split, Compare(whole.Words.Select(w => w.Text).ToArray(),
            left.Words.Concat(right.Words).Select(w => w.Text).ToArray()));
    }

    public static async Task RunAsync(Arguments arguments, Func<string, string> referencePath, string outputDirectory)
    {
        var settings = ChainSettings.FromArguments(arguments);
        if (settings.Audio == "mic" || settings.RepeatMinutes != 0) throw new ArgumentException("Direktmessung verlangt eine unveränderte WAV-Datei.");
        var audioPath = referencePath(settings.Audio);
        var originalAudioSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(audioPath)));
        var audio = WavFile.ReadMono16k(audioPath);
        var measurement = arguments.String("measurement") ?? "offset";
        if (measurement is not ("offset" or "split")) throw new ArgumentException("--measurement: offset oder split");
        if (measurement == "split" && (arguments.String("start-sample") is not null || arguments.String("end-sample") is not null))
            throw new ArgumentException("--start-sample/--end-sample gelten nur für RM02 offset.");
        (ReadOnlyMemory<float> Audio, int Start, int End) selected = measurement == "offset"
            ? SelectOffsetAudio(audio, arguments) : (audio, 0, audio.Length);
        // RM03: Satzintervalle werden extern markiert, nicht aus der Erkennung abgeleitet.
        var intervals = measurement == "split" ? ParseIntervals(await File.ReadAllTextAsync(
            arguments.String("sentences") ?? throw new ArgumentException("--sentences fehlt: JSON [[Startsample,Endsample],…]")), audio.Length) : [];
        foreach (var interval in intervals) CheckLength(interval[1] - interval[0]);
        string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var configuration = new RecognizerConfiguration(settings.Model, Hash(settings.Model), settings.Runtimes,
            settings.Threads, settings.AudioContext, settings.Vad, Hash(settings.Vad), VadSettings.Default);
        await using var supervisor = new RecognizerSupervisor(RecognizerLauncher.ForExecutable(Path.Combine(AppContext.BaseDirectory,
            "Freihand.Recognizer.exe")), configuration, ClientTimeouts.Default, TimeProvider.System);
        supervisor.Start();
        var options = new RecognitionOptions(settings.Language, settings.Hints);
        await using var session = await supervisor.StartSessionAsync(options, settings.LiveOptions());
        object metrics;
        if (measurement == "offset") metrics = await OffsetAsync(session.Decoder, selected.Audio, options);
        else
        {
            var splits = new List<SplitResult>();
            foreach (var interval in intervals) splits.Add(await SplitAsync(session.Decoder, audio, interval[0], interval[1], options));
            metrics = splits;
        }
        var output = arguments.String("out") ?? Path.Combine(outputDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}_direct-{measurement}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Measurement = measurement, Timestamp = DateTimeOffset.Now,
            session.Info, configuration.ModelSha256, configuration.VadSha256, settings.Threads, settings.AudioContext,
            OriginalAudioSha256 = originalAudioSha256, OriginalSamples = audio.Length,
            StartSample = selected.Start, EndSample = selected.End,
            AudioSamples = selected.Audio.Length, SampleRate = WavFile.SampleRate, Metrics = metrics }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(metrics));
        Console.WriteLine($"Ergebnis: {Path.GetFullPath(output)}");
    }

    // RM03/T21: Lokale Kettenberichte auswerten; Text verlässt weder Datei noch Prozess.
    internal static void ValidateReports(JsonElement live, JsonElement sentence)
    {
        try { ValidateReportPair(live, sentence); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Kettenbericht: ungültiger Feldtyp oder Wert.");
        }
    }

    private static void ValidateReportPair(JsonElement live, JsonElement sentence)
    {
        static JsonElement Required(JsonElement report, string name) => report.TryGetProperty(name, out var value)
            ? value : throw new InvalidDataException($"Kettenbericht: Pflichtfeld {name} fehlt.");
        static void CheckRun(JsonElement report, int mode)
        {
            static void Text(JsonElement owner, string field)
            {
                if (string.IsNullOrWhiteSpace(Required(owner, field).GetString()))
                    throw new InvalidDataException($"Kettenbericht: {field} ist keine bekannte Identität.");
            }
            static void Hash(JsonElement owner, string field)
            {
                var hash = Required(owner, field).GetString();
                if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                    throw new InvalidDataException($"Kettenbericht: {field} ist kein SHA-256.");
            }
            Text(report, "Machine");
            Text(report, "Audio");
            if (Required(report, "Transcript").ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Kettenbericht: Transcript muss eine Zeichenfolge sein.");
            var recognizer = Required(report, "Recognizer");
            Text(recognizer, "ModelFile");
            Hash(recognizer, "ModelSha256");
            if (Required(recognizer, "Runtime").GetInt32() is < 0 or > 2)
                throw new InvalidDataException("Kettenbericht: unbekannte Laufzeit.");
            var settings = Required(report, "Settings");
            Text(settings, "Language");
            if (Required(settings, "Threads").GetInt32() < 1 || Required(settings, "AudioContext").GetInt32() < 1)
                throw new InvalidDataException("Kettenbericht: ungültige Rechenoptionen.");
            var repeat = Required(settings, "RepeatMinutes").GetDouble();
            if (!double.IsFinite(repeat) || repeat < 0)
                throw new InvalidDataException("Kettenbericht: ungültige Wiederholungsdauer.");
            var hints = Required(settings, "Hints");
            if (hints.ValueKind != JsonValueKind.Array || hints.EnumerateArray().Any(hint =>
                hint.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(hint.GetString())))
                throw new InvalidDataException("Kettenbericht: ungültige Hinweise.");
            if (Required(report, "Tool").GetString() != "Freihand.Benchmark chain" ||
                Required(Required(report, "Settings"), "Mode").GetInt32() != mode)
                throw new InvalidDataException("Kettenbericht: falsches Werkzeug oder Modus (live/satz erwartet).");
            foreach (var name in new[] { "AudioSha256", "VadSha256" })
                Hash(report, name);
            if (Required(report, "VadSettings").ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Kettenbericht: VadSettings fehlen.");
            var errors = new List<string>();
            if (Required(report, "Incomplete").GetBoolean()) errors.Add("Incomplete");
            if (Required(report, "Failure").ValueKind != JsonValueKind.Null) errors.Add("Failure");
            var invalid = Required(report, "P3InvalidHypotheses").GetInt64();
            if (invalid != 0) errors.Add($"P3InvalidHypotheses={invalid}");
            foreach (var name in new[] { "TokenAlignmentFailures", "AudioTooLongFailures", "DecoderFailures", "DecoderCancelled" })
            {
                var count = Required(report, name).GetInt32();
                if (count != 0) errors.Add($"{name}={count}");
            }
            var detect = Required(report, "Detect");
            if (Required(detect, "Failures").GetInt32() != 0) errors.Add("Detect.Failures");
            if (errors.Count != 0) throw new InvalidDataException("Kettenbericht nicht abnahmefähig: " + string.Join(", ", errors));
            if (Required(report, "Audio").GetString() is null or "mic" ||
                Required(report, "PlannedAudioSeconds").GetDouble() != Required(report, "AcceptedAudioSeconds").GetDouble())
                throw new InvalidDataException("Kettenbericht: vollständiger Dateilauf erforderlich.");
        }
        static void Same(JsonElement first, JsonElement second, string name)
        {
            if (!JsonElement.DeepEquals(Required(first, name), Required(second, name)))
                throw new InvalidDataException($"Kettenberichte passen nicht zusammen: {name}.");
        }
        CheckRun(live, 0);
        CheckRun(sentence, 1);
        foreach (var name in new[] { "Audio", "AudioSha256", "VadSha256", "VadSettings", "Machine", "PlannedAudioSeconds", "AcceptedAudioSeconds" }) Same(live, sentence, name);
        var liveRecognizer = Required(live, "Recognizer");
        var sentenceRecognizer = Required(sentence, "Recognizer");
        foreach (var name in new[] { "ModelFile", "ModelSha256", "Runtime" }) Same(liveRecognizer, sentenceRecognizer, name);
        foreach (var name in new[] { "Language", "Hints", "Threads", "AudioContext", "RepeatMinutes" })
            Same(Required(live, "Settings"), Required(sentence, "Settings"), name);
    }

    public static async Task CompareReportsAsync(Arguments arguments)
    {
        using var live = JsonDocument.Parse(await File.ReadAllTextAsync(arguments.String("live") ?? throw new ArgumentException("--live fehlt")));
        using var sentence = JsonDocument.Parse(await File.ReadAllTextAsync(arguments.String("sentence") ?? throw new ArgumentException("--sentence fehlt")));
        ValidateReports(live.RootElement, sentence.RootElement);
        var liveText = live.RootElement.GetProperty("Transcript").GetString()!;
        var sentenceText = sentence.RootElement.GetProperty("Transcript").GetString()!;
        var reference = arguments.String("reference") is { } path ? await File.ReadAllTextAsync(path) : null;
        var metrics = new { SentenceToLiveDistance = WordErrorRate.Compute(sentenceText, liveText),
            LiveReferenceWer = reference is null ? null : WordErrorRate.Compute(reference, liveText),
            SentenceReferenceWer = reference is null ? null : WordErrorRate.Compute(reference, sentenceText) };
        Console.WriteLine(JsonSerializer.Serialize(metrics));
        if (arguments.String("out") is { } output)
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
    }
}
