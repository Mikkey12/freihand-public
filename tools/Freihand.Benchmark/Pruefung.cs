using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Freihand.Adapters.Dictation;
using Freihand.Windows.Recovery;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Freihand.Benchmark;

/// <summary>
/// Auswertung S2: entschlüsselt die Prüfaufnahmen auf diesem Gerät und
/// erkennt jede auf mehrere Arten neu, damit sich je Fehler die Stufe zeigt. Ergebnis nur lokal unter
/// %LOCALAPPDATA%\Freihand\Messung\S2; es enthält Text und gehört nach der Auswertung gelöscht.
/// <list type="bullet">
/// <item><c>app</c>: was Freihand im Alltag erkannt und geschrieben hat (aus der Aufnahme).</item>
/// <item><c>abschnitte</c>: jeder Abschnitt einzeln ab seinem Beginn, ohne Zusammenhang (nachgestellt wie heute).</item>
/// <item><c>vorlauf</c>: wie <c>abschnitte</c>, aber mit 0,5 s Ton vor dem Abschnitt.</item>
/// <item><c>zusammenhang</c>: wie <c>vorlauf</c>, dazu der bisherige Text als Hinweis an Whisper.</item>
/// <item><c>ganz</c>: die ganze Aufnahme in einem Durchgang mit Zusammenhang.</item>
/// </list>
/// </summary>
internal static class Pruefung
{
    private const string Entropy = "Freihand.Pruefaufnahmen.v1";
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(500);

    public static async Task RunAsync(Arguments arguments, string dataRoot)
    {
        var model = arguments.String("model") ?? throw new ArgumentException("--model fehlt");
        var folder = arguments.String("ordner") ?? Path.Combine(dataRoot, "Pruefaufnahmen");
        var output = arguments.String("out") ?? Path.Combine(dataRoot, "Messung", "S2",
            "ergebnis-" + Path.GetFileNameWithoutExtension(model) + ".jsonl");
        var since = arguments.String("ab");
        var wav = arguments.String("wav");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        RuntimeOptions.RuntimeLibraryOrder = arguments.String("runtime") == "cpu"
            ? [RuntimeLibrary.Cpu]
            : [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];
        using var factory = WhisperFactory.FromPath(model);
        var protector = new WindowsDataProtector(Entropy);
        var files = Directory.GetFiles(folder, "*" + ReviewRecorder.Extension).Order(StringComparer.Ordinal)
            .Where(file => since is null || string.CompareOrdinal(Path.GetFileName(file), since) >= 0).ToList();
        Console.WriteLine($"{files.Count} Prüfaufnahmen, Modell {Path.GetFileName(model)}, Laufzeit {RuntimeOptions.LoadedLibrary}");

        await using var writer = new StreamWriter(output, append: false, new UTF8Encoding(false));
        foreach (var file in files)
        {
            var (header, pcm) = Read(File.ReadAllBytes(file), protector);
            var audio = ToFloat(pcm.Span);
            if (wav is not null)
            {
                // Für die Nachmessung mit der ganzen Kette (chain); entschlüsselter Ton, nach der Auswertung löschen
                Directory.CreateDirectory(wav);
                WriteWav(Path.Combine(wav, Path.GetFileNameWithoutExtension(file) + ".wav"), pcm.Span, header.SampleRate);
            }

            var sections = header.Sections;
            var result = new Dictionary<string, object?>
            {
                ["datei"] = Path.GetFileName(file),
                ["nummer"] = header.Number,
                ["start"] = header.Start,
                ["sekunden"] = Math.Round(audio.Length / 16000.0, 2),
                ["app"] = string.Join(" ", sections.Select(section => section.Text.Trim())),
                ["geschrieben"] = string.Join(" ", sections.Select(section => section.Written.Trim())),
                ["ergebnisse"] = sections.Select(section => section.Result).ToArray(),
                ["abschnitte_ms"] = sections.Select(section => new[] { section.StartMs, section.EndMs }).ToArray(),
            };

            var each = new List<string>();
            var lead = new List<string>();
            var context = new List<string>();
            foreach (var section in sections)
            {
                var start = Slice(audio, header, section.StartMs, section.EndMs);
                var early = Slice(audio, header, section.StartMs - (long)Lead.TotalMilliseconds, section.EndMs);
                each.Add(await DecodeAsync(factory, start, prompt: null, wholeContext: false));
                lead.Add(await DecodeAsync(factory, early, prompt: null, wholeContext: false));
                var before = string.Join(" ", context);
                context.Add(await DecodeAsync(factory, early, before.Length == 0 ? null : Tail(before, 400), wholeContext: false));
            }

            var watch = Stopwatch.StartNew();
            result["abschnitte"] = string.Join(" ", each);
            result["vorlauf"] = string.Join(" ", lead);
            result["zusammenhang"] = string.Join(" ", context);
            result["ganz"] = await DecodeAsync(factory, audio, prompt: null, wholeContext: true);
            result["ganz_ms"] = watch.ElapsedMilliseconds;
            await writer.WriteLineAsync(JsonSerializer.Serialize(result));
            Console.Write('.');
        }

        Console.WriteLine();
        Console.WriteLine("Ergebnis: " + output);
    }

    private static (ReviewHeader Header, ReadOnlyMemory<byte> Pcm) Read(byte[] protectedData, WindowsDataProtector protector)
    {
        var plain = protector.Unprotect(protectedData);
        var length = BinaryPrimitives.ReadInt32LittleEndian(plain);
        var header = JsonSerializer.Deserialize<ReviewHeader>(plain.AsSpan(4, length))
            ?? throw new InvalidDataException("Kopf der Prüfaufnahme fehlt.");
        return (header, plain.AsMemory(4 + length));
    }

    private static void WriteWav(string path, ReadOnlySpan<byte> pcm, int sampleRate)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
    }

    private static float[] ToFloat(ReadOnlySpan<byte> pcm)
    {
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(2 * i)..]) / 32768f;
        }

        return samples;
    }

    /// <summary>Ton zwischen zwei Sitzungszeiten; die Blöcke sagen, wo eine Sitzungszeit im Ton liegt.</summary>
    private static float[] Slice(float[] audio, ReviewHeader header, long fromMs, long toMs)
    {
        int Index(long ms)
        {
            var chunk = header.Chunks.LastOrDefault(item => item.OffsetMs <= ms) ?? header.Chunks.FirstOrDefault();
            if (chunk is null)
            {
                return 0;
            }

            // Auf den Block begrenzt: Liegt die Zeit in einer Lücke nach dem Block, gilt sein Ende (Codex zu PR #65)
            var within = Math.Clamp((ms - chunk.OffsetMs) * 16, 0L, chunk.Bytes / 2);
            var sample = chunk.Position / 2 + within;
            return (int)Math.Clamp(sample, 0, audio.Length);
        }

        var from = Index(Math.Max(0, fromMs));
        var to = Math.Max(from, Index(toMs));
        return audio[from..to];
    }

    private static async Task<string> DecodeAsync(WhisperFactory factory, float[] audio, string? prompt, bool wholeContext)
    {
        if (audio.Length < 1600)
        {
            return string.Empty;
        }

        var builder = factory.CreateBuilder().WithLanguage("de");
        if (!wholeContext)
        {
            builder = builder.WithNoContext();
        }

        if (prompt is not null)
        {
            builder = builder.WithPrompt(prompt);
        }

        using var processor = builder.Build();
        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(audio))
        {
            text.Append(segment.Text);
        }

        return text.ToString().Trim();
    }

    private static string Tail(string text, int max) => text.Length <= max ? text : text[^max..];
}
