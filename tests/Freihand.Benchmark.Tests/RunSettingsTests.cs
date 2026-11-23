using System.Text.Json;

namespace Freihand.Benchmark.Tests;

/// <summary>Wirksame Einstellungen im Ergebnis (F09, Vorschlag C05 aus der Codex-Nachprüfung).</summary>
public sealed class RunSettingsTests
{
    [Fact]
    public void C05_LanguageIsReportedAsUsed()
    {
        var german = Json(RunSettings.FromArguments(Arguments.Parse(["run", "--language", "de"])));
        var english = Json(RunSettings.FromArguments(Arguments.Parse(["run", "--language", "en"])));

        Assert.Equal("de", german.GetProperty("Language").GetString());
        Assert.Equal("en", english.GetProperty("Language").GetString());
    }

    [Fact]
    public void C05_ParakeetHasNoFixedLanguage()
    {
        var settings = RunSettings.FromArguments(Arguments.Parse(["run", "--engine", "parakeet", "--mode", "satz", "--language", "de"]));

        Assert.Null(settings.Language);
    }

    [Fact]
    public void C05_RepeatAndProcessedAudioAreReported()
    {
        var settings = RunSettings.FromArguments(Arguments.Parse(["run", "--repeat", "30"])) with { AudioSeconds = 1801.5 };

        var json = Json(settings);
        Assert.Equal(30, json.GetProperty("RepeatMinutes").GetDouble());
        Assert.Equal(1801.5, json.GetProperty("AudioSeconds").GetDouble());
    }

    [Fact]
    public void C05_ParakeetWithSileroReportsBothRuntimes()
    {
        var settings = RunSettings.FromArguments(
            Arguments.Parse(["run", "--engine", "parakeet", "--mode", "satz", "--vad", "silero.bin"]))
            with { Runtime = "sherpa-onnx (cpu)", VadRuntime = "Cpu" };

        var json = Json(settings);
        Assert.Equal("sherpa-onnx (cpu)", json.GetProperty("Runtime").GetString());
        Assert.Equal("Cpu", json.GetProperty("VadRuntime").GetString());
        Assert.Equal(0.3, json.GetProperty("Vad").GetProperty("MinSilenceSeconds").GetDouble());
        Assert.Equal(0.6, json.GetProperty("Sentence").GetProperty("PauseSeconds").GetDouble());
    }

    [Fact]
    public void LiveModeHasNoSentenceOrVadSettings()
    {
        var settings = RunSettings.FromArguments(Arguments.Parse(["run", "--vad", "silero.bin"]));

        Assert.Null(settings.Sentence);
        Assert.Null(settings.Vad);
    }

    [Theory]
    [InlineData("--engine", "vosk")]
    [InlineData("--mode", "stream")]
    [InlineData("--runtime", "metal")]
    public void UnknownChoicesAreRejectedBeforeModelStart(string option, string value)
    {
        Assert.Throws<ArgumentException>(() => RunSettings.FromArguments(Arguments.Parse(["run", option, value])));
    }

    [Fact]
    public void ParakeetOnlyInSentenceMode()
    {
        Assert.Throws<ArgumentException>(() => RunSettings.FromArguments(Arguments.Parse(["run", "--engine", "parakeet"])));
    }

    [Fact]
    public void AudioContextMustCoverWindowAndStep()
    {
        Assert.Throws<ArgumentException>(() =>
            RunSettings.FromArguments(Arguments.Parse(["run", "--audio-ctx", "600", "--window", "12", "--step", "1"])));
    }

    private static JsonElement Json(RunSettings settings) => JsonSerializer.SerializeToElement(settings);
}
