using System.Diagnostics;
using Freihand.Adapters.Recognition;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Freihand.Recognizer;

/// <summary>
/// Lädt Whisper und Silero mit der ersten ladbaren Laufzeit aus <see cref="RecognizerConfiguration.RuntimeOrder"/>
/// (Kapitel 5.4, 5.5 und 10.5). Die Prüfsummen hat <see cref="RecognizerService"/> schon über gegen Schreiben gesperrte
/// Handles geprüft, die bis nach dem Laden offen bleiben; geladen wird darum über den Pfad. Kein Logger ist registriert,
/// damit whisper.cpp nichts protokolliert.
/// </summary>
public static class WhisperNetLoader
{
    public static (IWhisperBackend Whisper, IVadBackend Vad, RecognizerInfo Info) Load(RecognizerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Die Reihenfolge gilt nur vor dem ersten nativen Laden; der Erkennungsprozess lädt genau einmal
        RuntimeOptions.RuntimeLibraryOrder = [.. configuration.RuntimeOrder.SelectMany(Libraries)];
        var watch = Stopwatch.StartNew();
        WhisperFactory factory;
        try
        {
            factory = WhisperFactory.FromPath(configuration.ModelPath);
        }
        catch (Exception exception) when (exception is not RecognizerException)
        {
            throw new RecognizerException(RecognizerErrorCode.RuntimeUnavailable);
        }

        var runtime = RuntimeOptions.LoadedLibrary switch
        {
            RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 => RecognizerRuntime.Cuda,
            RuntimeLibrary.Vulkan => RecognizerRuntime.Vulkan,
            RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx => RecognizerRuntime.Cpu,
            _ => throw new RecognizerException(RecognizerErrorCode.RuntimeUnavailable),
        };

        var vadFactory = WhisperVadFactory.FromPath(configuration.VadModelPath, new WhisperFactoryOptions { UseGpu = false });
        var vad = vadFactory.CreateBuilder()
            .WithThreads(1)
            .WithUseGpu(false)
            .WithThreshold(configuration.Vad.Threshold)
            .WithMinSpeechDuration(configuration.Vad.MinSpeech)
            .WithMinSilenceDuration(configuration.Vad.MinSilence)
            .WithSpeechPadding(configuration.Vad.SpeechPad)
            .Build();
        watch.Stop();

        var info = new RecognizerInfo(
            Path.GetFileName(configuration.ModelPath),
            configuration.ModelSha256,
            runtime,
            RuntimeFallback: runtime != configuration.RuntimeOrder[0],
            watch.Elapsed,
            typeof(WhisperFactory).Assembly.GetName().Version?.ToString(3) ?? "unbekannt");
        return (new WhisperNetBackend(factory, configuration), new SileroBackend(vadFactory, vad), info);
    }

    private static IEnumerable<RuntimeLibrary> Libraries(RecognizerRuntime runtime) => runtime switch
    {
        RecognizerRuntime.Cuda => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12],
        RecognizerRuntime.Vulkan => [RuntimeLibrary.Vulkan],
        RecognizerRuntime.Cpu => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
        _ => [],
    };

    /// <summary>Whisper mit fester Sprache, ohne Übersetzung und Kontext, mit Token-Zeiten (Kapitel 5.5 und 10.5).</summary>
    internal sealed class WhisperNetBackend(WhisperFactory factory, RecognizerConfiguration configuration) : IWhisperBackend
    {
        private readonly Dictionary<(string Language, string? Prompt, bool FullContext), WhisperProcessor> _processors = [];

        public async Task<IReadOnlyList<WhisperSegmentData>> TranscribeAsync(
            ReadOnlyMemory<float> audio, string language, string? prompt, bool fullContext, CancellationToken cancellationToken)
        {
            var processor = Processor(language, prompt, fullContext);
            var segments = new List<WhisperSegmentData>();
            await foreach (var segment in processor.ProcessAsync(audio, cancellationToken).ConfigureAwait(false))
            {
                var tokens = segment.Tokens
                    .Select(token => new WhisperTokenData(token.Text ?? string.Empty, token.Start, token.End, token.Probability, IsSpecial(token.Text)))
                    .ToList();
                segments.Add(new WhisperSegmentData(segment.Text ?? string.Empty, tokens));
            }

            return segments;
        }

        public void Dispose()
        {
            foreach (var processor in _processors.Values)
            {
                processor.Dispose();
            }

            factory.Dispose();
        }

        /// <summary>
        /// Sondertokens an der Textform, weil Whisper.net die Grenze whisper_token_eot nicht liefert (Kapitel 10.5):
        /// <c>[_BEG_]</c>, Zeitstempel wie <c>[_TT_263]</c> (Messbefund RM01, Kapitel 10.6) und <c>&lt;|…|&gt;</c>.
        /// </summary>
        internal static bool IsSpecial(string? text) =>
            text is { Length: >= 3 } && ((text.StartsWith("[_", StringComparison.Ordinal) && text.EndsWith(']'))
                || (text.StartsWith("<|", StringComparison.Ordinal) && text.EndsWith("|>", StringComparison.Ordinal)));

        private WhisperProcessor Processor(string language, string? prompt, bool fullContext)
        {
            var key = (language, prompt, fullContext);
            if (_processors.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var builder = factory.CreateBuilder()
                .WithLanguage(language)
                .WithNoContext()
                .WithTokenTimestamps()
                .WithThreads(configuration.Threads);
            if (!fullContext)
            {
                builder = builder.WithAudioContextSize(configuration.AudioContext);
            }

            if (prompt is not null)
            {
                builder = builder.WithPrompt(prompt);
            }

            var processor = builder.Build();
            _processors[key] = processor;
            return processor;
        }
    }

    /// <summary>Silero mit Rücksetzung des Zustands je Aufruf (<c>DetectSpeech</c>, Kapitel 5.6 und 10.5).</summary>
    private sealed class SileroBackend(WhisperVadFactory factory, WhisperVadProcessor processor) : IVadBackend
    {
        public IReadOnlyList<(TimeSpan Start, TimeSpan End)> DetectFresh(ReadOnlyMemory<float> audio) =>
            [.. processor.DetectSpeech(audio.Span).Select(segment => (segment.Start, segment.End))];

        public void Dispose()
        {
            processor.Dispose();
            factory.Dispose();
        }
    }
}
