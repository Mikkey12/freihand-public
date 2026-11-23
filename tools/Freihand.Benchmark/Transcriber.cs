using SherpaOnnx;
using Whisper.net;

namespace Freihand.Benchmark;

/// <summary>Erkennt einen Abschnitt Audio am Stück; Grundlage des satzweisen Modus (ER-19).</summary>
internal interface ITranscriber
{
    Task<IReadOnlyList<string>> TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken cancellationToken = default);
}

internal sealed class WhisperTranscriber(WhisperProcessor processor) : ITranscriber
{
    public async Task<IReadOnlyList<string>> TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken cancellationToken = default)
    {
        var texts = new List<string>();
        await foreach (var segment in processor.ProcessAsync(audio, cancellationToken))
        {
            if (segment.Text.Trim() is { Length: > 0 } text)
            {
                texts.Add(text);
            }
        }

        return texts;
    }
}

/// <summary>
/// Parakeet TDT über sherpa-onnx (Vergleich im Machbarkeitstest, Technikentscheid „Weitere Modelle“). Erwartet den
/// entpackten Modellordner mit <c>encoder</c>, <c>decoder</c>, <c>joiner</c> und <c>tokens.txt</c>.
/// </summary>
internal sealed class ParakeetTranscriber : ITranscriber, IDisposable
{
    private readonly OfflineRecognizer _recognizer;

    private static readonly string[] Parts = ["encoder", "decoder", "joiner"];

    public ParakeetTranscriber(string modelDirectory, int threads)
    {
        string Find(string prefix) => Directory.GetFiles(modelDirectory, prefix + "*.onnx") switch
        {
            [var single] => single,
            [] => throw new ArgumentException($"{modelDirectory}: {prefix}*.onnx fehlt."),
            // z. B. int8 und volle Genauigkeit nebeneinander: nie still die erste Datei nehmen
            var several => throw new ArgumentException(
                $"{modelDirectory}: {prefix}*.onnx ist mehrdeutig ({string.Join(", ", several.Select(Path.GetFileName))})."),
        };

        // Dateien vor dem Laden der nativen Bibliothek prüfen
        var (encoder, decoder, joiner) = (Find("encoder"), Find("decoder"), Find("joiner"));
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = WavFile.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = encoder;
        config.ModelConfig.Transducer.Decoder = decoder;
        config.ModelConfig.Transducer.Joiner = joiner;
        config.ModelConfig.Tokens = Path.Combine(modelDirectory, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        _recognizer = new OfflineRecognizer(config);
    }

    public Task<IReadOnlyList<string>> TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(WavFile.SampleRate, audio.ToArray());
        _recognizer.Decode(stream);
        var text = stream.Result.Text.Trim();
        return Task.FromResult<IReadOnlyList<string>>(text.Length > 0 ? [text] : []);
    }

    public void Dispose() => _recognizer.Dispose();

    /// <summary>Die verwendeten Modelldateien eines Ordners, für Grösse und Prüfsumme im Ergebnis.</summary>
    public static string[] ModelFiles(string modelDirectory) =>
        [.. Parts.SelectMany(p => Directory.GetFiles(modelDirectory, p + "*.onnx")), Path.Combine(modelDirectory, "tokens.txt")];
}
