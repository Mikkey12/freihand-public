using System.Text.Json;
using Freihand.Core.Quality;

namespace Freihand.Core.Tests.Quality;

public class QualityReportTests
{
    private static readonly MeasurementInfo Measurement = new(
        "whisper.cpp",
        "large-v3-turbo q5",
        "Notebook-Profil",
        "de-CH",
        TextStage.Raw,
        ReferenceSet: "referenzsatz-v1",
        MeasuredAt: new DateTimeOffset(2026, 10, 27, 14, 0, 0, TimeSpan.FromHours(1)),
        Notes: "Office- und Teams-Last");

    [Fact]
    public void EvaluatesAllMetrics()
    {
        var report = QualityEvaluator.Evaluate(SampleRun());

        Assert.Equal(QualityReport.CurrentSchema, report.Schema);
        Assert.Equal(0.55, report.RealTimeFactor.Overall!.Value, 10);
        Assert.Equal(0.8, report.RealTimeFactor.WindowP95!.Value, 10);
        Assert.Equal(11, report.RealTimeFactor.Windows.Count);

        Assert.Equal(TimeSpan.FromSeconds(1), report.Latency.FirstPartial.P95);
        Assert.Equal(TimeSpan.FromSeconds(2), report.Latency.Fixed.Max);
        Assert.Equal(1, report.Latency.Fixed.Missing);
        Assert.Equal(TimeSpan.FromSeconds(2), report.Latency.FixedWindowP95Max);

        // s1: „Rechnung“ fehlt (1 von 6 Wörtern), s2: fehlerfrei (5 Wörter, „1'250“ = „1250“)
        Assert.Equal(new WordErrorCounts(11, 0, 1, 0), report.WordErrors.Total);
        Assert.Equal(1.0 / 11, report.WordErrors.Total.Rate, 10);
        Assert.Equal(["s1", "s2"], report.WordErrors.Sentences.Select(s => s.Id));

        Assert.Equal(new CategoryErrors(TermCategory.Name, 1, 0), report.Terms.Single(t => t.Category == TermCategory.Name));
        Assert.Equal(new CategoryErrors(TermCategory.Number, 2, 1), report.Terms.Single(t => t.Category == TermCategory.Number));

        // s1: Punkt erwartet und da; s2: Komma fehlt, Punkt da
        Assert.Equal(3, report.DictationMarks.Expected);
        Assert.Equal(1, report.DictationMarks.Errors);

        Assert.Equal(1, report.Commands.FalseTriggered);
        Assert.Equal(1, report.Commands.Correct);
        Assert.Equal(3.0, report.Commands.FalseTriggeredPerHour);
    }

    [Fact]
    public void ExportsDocumentedJsonFormat()
    {
        var json = QualityReportJson.Serialize(QualityEvaluator.Evaluate(SampleRun()));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("freihand.quality/1", root.GetProperty("schema").GetString());
        Assert.Equal("raw", root.GetProperty("measurement").GetProperty("stage").GetString());
        Assert.Equal(1_200_000, root.GetProperty("audioDuration").GetDouble());
        Assert.Equal(500, root.GetProperty("settings").GetProperty("pauseThreshold").GetDouble());
        Assert.Equal(600_000, root.GetProperty("settings").GetProperty("window").GetDouble());
        Assert.Equal(1000, root.GetProperty("latency").GetProperty("firstPartial").GetProperty("p95").GetDouble());
        Assert.Equal(1.0 / 11, root.GetProperty("wordErrors").GetProperty("total").GetProperty("rate").GetDouble(), 10);
        Assert.Equal("name", root.GetProperty("terms")[0].GetProperty("category").GetString());
        Assert.Equal(0, root.GetProperty("terms")[0].GetProperty("rate").GetDouble());
        Assert.Equal(1500, root.GetProperty("latency").GetProperty("fixed").GetProperty("mean").GetDouble());
        Assert.Equal("comma", root.GetProperty("dictationMarks").GetProperty("kinds")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public void JsonContainsNoTextFromSentences()
    {
        var json = QualityReportJson.Serialize(QualityEvaluator.Evaluate(SampleRun()));

        Assert.DoesNotContain("Rechnung", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Gisler", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Franken", json, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonRoundTrips()
    {
        var json = QualityReportJson.Serialize(QualityEvaluator.Evaluate(SampleRun()));

        var restored = QualityReportJson.Deserialize(json);

        Assert.Equal(json, QualityReportJson.Serialize(restored));
        Assert.Equal(Measurement, restored.Measurement);
    }

    [Fact]
    public void ErrorsOfDifferentSentencesDoNotCancelOutInReport()
    {
        // Codex-Review R6: Fehler je Satz zählen, auch wenn sich Soll und Ist über alle Sätze ausgleichen
        var run = new QualityRun(Measurement, TimeSpan.FromMinutes(1), new QualitySettings(TimeSpan.FromMilliseconds(500)))
        {
            Sentences =
            [
                new("a", "Ja, Herr Meier.", "Ja Herr.", [new(TermCategory.Name, "Meier")]),
                new("b", "Nein danke.", "Nein, danke Meier.", []),
            ],
            Commands =
            [
                new("c1", "rueckgaengig", null),
                new("c2", null, "rueckgaengig"),
            ],
        };

        var report = QualityEvaluator.Evaluate(run);
        var restored = QualityReportJson.Deserialize(QualityReportJson.Serialize(report));

        Assert.Equal(2, report.DictationMarks.Kinds.Single(k => k.Kind == DictationMarkKind.Comma).Errors);
        Assert.Equal(2, restored.DictationMarks.Kinds.Single(k => k.Kind == DictationMarkKind.Comma).Errors);
        Assert.Equal(2, restored.DictationMarks.Errors);
        Assert.Equal(1, report.Terms.Single(t => t.Category == TermCategory.Name).Errors);
        Assert.Equal(1, report.Commands.MissedCommands);
        Assert.Equal(1, report.Commands.FalseCommands);
    }

    [Fact]
    public void RejectsUnknownSchema()
    {
        var json = QualityReportJson.Serialize(QualityEvaluator.Evaluate(SampleRun()))
            .Replace(QualityReport.CurrentSchema, "freihand.quality/99", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => QualityReportJson.Deserialize(json));
    }

    [Fact]
    public void EmptyRunHasNoValues()
    {
        var report = QualityEvaluator.Evaluate(new QualityRun(Measurement, TimeSpan.Zero, new QualitySettings(TimeSpan.Zero)));

        Assert.Null(report.RealTimeFactor.Overall);
        Assert.Null(report.Latency.FirstPartial.P95);
        Assert.Equal(WordErrorCounts.Zero, report.WordErrors.Total);
        Assert.Null(report.Commands.FalseTriggeredPerHour);
        using var document = JsonDocument.Parse(QualityReportJson.Serialize(report));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("realTimeFactor").GetProperty("overall").ValueKind);
    }

    /// <summary>20 Minuten: erste 10 Minuten Echtzeitfaktor 0,3, danach 0,8 (wie <see cref="RealTimeFactorTests"/>).</summary>
    private static QualityRun SampleRun() => new(
        Measurement,
        TimeSpan.FromMinutes(20),
        new QualitySettings(TimeSpan.FromMilliseconds(500)))
    {
        Compute =
        [
            .. Enumerable.Range(0, 20)
                .Select(i => new ComputeSample(TimeSpan.FromMinutes(i), TimeSpan.FromSeconds(i < 10 ? 18 : 48))),
        ],
        Utterances =
        [
            new("u1", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(14), TimeSpan.FromSeconds(10.8), TimeSpan.FromSeconds(15.5)),
            new("u2", TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(5.5)),
            new("u3", TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(2), null, null),
        ],
        Sentences =
        [
            new("s1", "Bitte die Rechnung an Frau Gisler.", "Bitte die an Frau Gisler.", [new(TermCategory.Name, "Gisler")]),
            new(
                "s2",
                "Total 1'250, also 3,5 Franken.",
                "Total 1250 also 3,5 Franken.",
                [new(TermCategory.Number, "1'250"), new(TermCategory.Number, "3,5")]),
        ],
        Commands =
        [
            new("c1", "rueckgaengig", "rueckgaengig"),
            new("c2", null, "abbrechen"),
        ],
    };
}
