using System.Text.Json;
using System.Text.Json.Nodes;

namespace Freihand.Benchmark.Tests;

public sealed class ChainComparisonTests
{
    // RM03/RM04: Erst vollständige, fehlerfreie und vergleichbare Läufe auswerten.
    [Fact]
    public void MatchingCompleteRunsAreAccepted() => Validate(Report(0), Report(1));

    [Theory]
    [InlineData("Recognizer.ModelSha256")]
    [InlineData("Recognizer.ModelFile")]
    [InlineData("Recognizer.Runtime")]
    [InlineData("Machine")]
    [InlineData("Settings.Language")]
    [InlineData("Settings.Hints")]
    [InlineData("Settings.Threads")]
    [InlineData("Settings.AudioContext")]
    [InlineData("Settings.RepeatMinutes")]
    [InlineData("Transcript")]
    public void UnknownOrWronglyTypedValuesOnBothSidesAreRejected(string path)
    {
        foreach (var value in new JsonNode?[] { null, JsonValue.Create(true), new JsonObject() })
        {
            var live = Report(0);
            var sentence = Report(1);
            Set(live, path, value?.DeepClone());
            Set(sentence, path, value?.DeepClone());
            Assert.Throws<InvalidDataException>(() => Validate(live, sentence));
        }
    }

    [Theory]
    [InlineData("AudioSha256")]
    [InlineData("VadSha256")]
    [InlineData("Recognizer.ModelSha256")]
    public void MalformedHashesCannotBeEqualEvidence(string path)
    {
        foreach (var hash in new[] { "", "abc", new string('z', 64), new string('a', 63), new string('a', 65) })
        {
            var live = Report(0);
            var sentence = Report(1);
            Set(live, path, JsonValue.Create(hash));
            Set(sentence, path, JsonValue.Create(hash));
            Assert.Throws<InvalidDataException>(() => Validate(live, sentence));
        }
    }

    [Theory]
    [InlineData("AudioSha256")]
    [InlineData("VadSha256")]
    [InlineData("VadSettings")]
    public void TwoMissingIdentitiesDoNotCountAsEqual(string field)
    {
        var live = Report(0);
        var sentence = Report(1);
        live[field] = null;
        sentence[field] = null;
        Assert.Throws<InvalidDataException>(() => Validate(live, sentence));
    }

    [Theory]
    [InlineData("Audio")]
    [InlineData("Machine")]
    [InlineData("AudioSha256")]
    [InlineData("VadSha256")]
    [InlineData("VadSettings")]
    [InlineData("Recognizer.ModelFile")]
    [InlineData("Recognizer.ModelSha256")]
    [InlineData("Recognizer.Runtime")]
    [InlineData("Settings.Mode")]
    [InlineData("Settings.Language")]
    [InlineData("Settings.Threads")]
    [InlineData("Settings.AudioContext")]
    [InlineData("Settings.RepeatMinutes")]
    [InlineData("PlannedAudioSeconds")]
    [InlineData("AcceptedAudioSeconds")]
    public void DifferentIdentityOrModeIsRejected(string field)
    {
        var sentence = Report(1);
        Set(sentence, field, field is "Recognizer.Runtime" or "Settings.Mode" or "Settings.Threads" or "Settings.AudioContext" or
            "Settings.RepeatMinutes" or "PlannedAudioSeconds" or "AcceptedAudioSeconds" ? JsonValue.Create(7)! : JsonValue.Create("different")!);
        Assert.Throws<InvalidDataException>(() => Validate(Report(0), sentence));
    }

    [Theory]
    [InlineData("TokenAlignmentFailures")]
    [InlineData("AudioTooLongFailures")]
    [InlineData("DecoderFailures")]
    [InlineData("DecoderCancelled")]
    [InlineData("Detect.Failures")]
    [InlineData("P3InvalidHypotheses")]
    public void AnyErrorCountRejectsEitherRun(string field)
    {
        foreach (var liveSide in new[] { false, true })
        {
            var live = Report(0);
            var sentence = Report(1);
            Set(liveSide ? live : sentence, field, JsonValue.Create(2)!);
            Assert.Throws<InvalidDataException>(() => Validate(live, sentence));
        }
    }

    [Fact]
    public void FailedIncompleteSentenceExposesAllFailureMarkers()
    {
        var sentence = Report(1);
        sentence["Failure"] = "DecoderError";
        sentence["Incomplete"] = true;
        sentence["TokenAlignmentFailures"] = 2;
        var error = Assert.Throws<InvalidDataException>(() => Validate(Report(0), sentence));
        Assert.Contains("Failure", error.Message);
        Assert.Contains("Incomplete", error.Message);
        Assert.Contains("TokenAlignmentFailures=2", error.Message);
    }

    [Theory]
    [InlineData("Failure")]
    [InlineData("Incomplete")]
    [InlineData("TokenAlignmentFailures")]
    [InlineData("Machine")]
    [InlineData("AudioSha256")]
    [InlineData("VadSha256")]
    [InlineData("VadSettings")]
    [InlineData("P3InvalidHypotheses")]
    public void MissingEvidenceIsRejected(string field)
    {
        var live = Report(0);
        live.Remove(field);
        Assert.Throws<InvalidDataException>(() => Validate(live, Report(1)));
    }

    private static JsonObject Report(int mode) => JsonNode.Parse("""
        {"Tool":"Freihand.Benchmark chain","Machine":"synthetic","Audio":"R1.wav",
        "AudioSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","VadSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","VadSettings":{"Threshold":0.5},
        "Transcript":"", "Incomplete":false,"Failure":null,"P3InvalidHypotheses":0,"TokenAlignmentFailures":0,"AudioTooLongFailures":0,
        "DecoderFailures":0,"DecoderCancelled":0,"Detect":{"Failures":0},
        "PlannedAudioSeconds":20,"AcceptedAudioSeconds":20,
        "Recognizer":{"ModelFile":"test.bin","ModelSha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","Runtime":0},
        "Settings":{"Mode":0,"Language":"de-CH","Hints":[],"Threads":4,"AudioContext":1500,"RepeatMinutes":0}}
        """)!.AsObject().ApplyMode(mode);

    private static void Set(JsonObject report, string path, JsonNode? value)
    {
        var names = path.Split('.');
        if (names.Length == 1) report[names[0]] = value;
        else report[names[0]]![names[1]] = value;
    }

    private static void Validate(JsonObject live, JsonObject sentence) => DirectMetrics.ValidateReports(
        JsonSerializer.SerializeToElement(live), JsonSerializer.SerializeToElement(sentence));
}

internal static class SyntheticReportExtensions
{
    internal static JsonObject ApplyMode(this JsonObject report, int mode)
    {
        report["Settings"]!["Mode"] = mode;
        return report;
    }
}
