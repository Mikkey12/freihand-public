namespace Freihand.Benchmark;

/// <summary>
/// Wirksame Einstellungen eines Laufs: aus der Befehlszeile abgeleitet und geprüft, bevor ein Modell lädt, und so ins
/// Ergebnis geschrieben.
/// </summary>
/// <param name="RuntimeChoice">Gewählte Laufzeit: auto, cpu, cuda oder vulkan.</param>
/// <param name="Language">Sprache der Erkennung; leer bei Parakeet, das sich nicht auf eine Sprache festlegen lässt.</param>
/// <param name="AudioContext">Rechenfenster von whisper.cpp, 0 = Standard (30 s).</param>
/// <param name="RepeatMinutes">Länge des aneinandergehängten Diktats; 0 = Aufnahme einmal.</param>
/// <param name="Sentence">Nur satzweise.</param>
/// <param name="Vad">Nur satzweise mit Silero VAD.</param>
/// <param name="AudioSeconds">Tatsächlich verarbeitete Audiodauer, nach dem Aneinanderhängen.</param>
/// <param name="Runtime">Geladene Laufzeit der Erkennung.</param>
/// <param name="VadRuntime">Geladene Laufzeit von Silero VAD (whisper.cpp), auch neben Parakeet; leer ohne VAD.</param>
internal sealed record RunSettings(
    string Engine,
    string Mode,
    string RuntimeChoice,
    string? Language,
    int Threads,
    int AudioContext,
    double RepeatMinutes,
    SimulationOptions Options,
    SentenceOptions? Sentence,
    VadOptions? Vad,
    double AudioSeconds = 0,
    string? Runtime = null,
    string? VadRuntime = null)
{
    public static RunSettings FromArguments(Arguments arguments)
    {
        var engine = arguments.String("engine") ?? "whisper";
        if (engine is not ("whisper" or "parakeet"))
        {
            throw new ArgumentException($"--engine {engine} unbekannt; erlaubt: whisper, parakeet");
        }

        var mode = arguments.String("mode") ?? "live";
        if (mode is not ("live" or "satz"))
        {
            throw new ArgumentException($"--mode {mode} unbekannt; erlaubt: live, satz");
        }

        if (engine == "parakeet" && mode != "satz")
        {
            throw new ArgumentException("--engine parakeet geht nur mit --mode satz.");
        }

        var runtime = arguments.String("runtime") ?? "auto";
        if (runtime is not ("auto" or "cpu" or "cuda" or "vulkan"))
        {
            throw new ArgumentException($"--runtime {runtime} unbekannt; erlaubt: auto, cpu, cuda, vulkan");
        }

        // Alle Zahlen vor dem Modellstart prüfen: 0 oder negativ führt sonst zu nicht fortschreitenden Schritten
        var options = new SimulationOptions(
            arguments.Positive("step", 1.0),
            arguments.Positive("window", 25.0),
            arguments.Positive("guard", 1.0, allowZero: true));

        // whisper.cpp rechnet sonst immer ein 30-s-Fenster (1500 Positionen, 50 je Sekunde); kleiner ist schneller,
        // aber das noch nicht feste Audio darf dann nicht länger sein als das Fenster
        var audioContext = arguments.Int("audio-ctx", 0, minimum: 0);
        if (audioContext > 0 && options.WindowSeconds + options.StepSeconds > audioContext / 50.0)
        {
            throw new ArgumentException($"--audio-ctx {audioContext} deckt nur {audioContext / 50.0:F1} s; --window plus --step muss darunter liegen.");
        }

        var satz = mode == "satz";
        return new RunSettings(
            engine,
            mode,
            runtime,
            Language: engine == "whisper" ? arguments.String("language") ?? "de" : null,
            Threads: arguments.Int("threads", Math.Min(4, Environment.ProcessorCount), minimum: 1),
            AudioContext: audioContext,
            RepeatMinutes: arguments.Positive("repeat", 0, allowZero: true),
            Options: options,
            Sentence: satz ? new SentenceOptions(arguments.Positive("pause", 0.6), options.WindowSeconds) : null,
            Vad: satz && arguments.String("vad") is not null
                ? new VadOptions(
                    arguments.Positive("vad-silence", 0.3),
                    arguments.Positive("vad-max", Math.Max(1, options.WindowSeconds - 2)),
                    MinSpeechSeconds: 0.4)
                : null);
    }
}
