namespace Freihand.Benchmark.Tests;

/// <summary>Kennzahlen eines Laufs; M02, M04 und die Gegenprobe B04 stammen aus dem Codex-Review.</summary>
public sealed class ReportTests
{
    [Fact]
    public void B04_ResultEntirelyBeforeSpeechStartIsNoFirstText()
    {
        // Das Decode-Fenster reicht über den Sprachbeginn, das einzige Ergebnis liegt aber ganz davor
        var result = Result([Step(audio: 3.0, finish: 3.2, lastSegmentEnd: 1.5)]);

        Assert.Null(Report.Create(result, speechStart: 2.0).FirstPartialSeconds);
    }

    [Fact]
    public void M04_FirstResultReachingIntoSpeechCounts()
    {
        var result = Result([
            Step(audio: 3.0, finish: 3.2, lastSegmentEnd: 1.5),
            Step(audio: 4.0, finish: 4.3, lastSegmentEnd: 3.8),
        ]);

        Assert.Equal(2.3, Report.Create(result, speechStart: 2.0).FirstPartialSeconds!.Value, 6);
    }

    [Fact]
    public void StepWithoutResultIsNoFirstText()
    {
        var result = Result([Step(audio: 3.0, finish: 3.2, lastSegmentEnd: null)]);

        Assert.Null(Report.Create(result, speechStart: 2.0).FirstPartialSeconds);
    }

    [Fact]
    public void M02_SegmentationTimeIsReportedSeparately()
    {
        // Ohne Sprachbereich keine Decode-Schritte; die geprüfte Aufnahme kostet trotzdem Rechenzeit
        var result = new SimulationResult(10, [], [], null, SegmentationSeconds: 0.5);

        var report = Report.Create(result, speechStart: null);

        Assert.Equal(0, report.RealTimeFactor);
        Assert.Equal(0.05, report.RealTimeFactorWithSegmentation, 6);
    }

    [Fact]
    public void M01_SuspendedTimeIsSummedAndFlagged()
    {
        var result = new SimulationResult(
            10,
            [Step(audio: 2, finish: 3, lastSegmentEnd: 1.8, suspended: 4.0)],
            [],
            null,
            SegmentationSuspendedSeconds: 1.0);

        var report = Report.Create(result, speechStart: null);

        Assert.Equal(5.0, report.SuspendedSeconds, 6);
        Assert.Contains("Standby", report.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void M01_UnmeasurableStandbyIsUnknownNotAbsent()
    {
        // Liefert Windows die Interrupt-Zeit nicht, ist „kein Standby“ nicht belegt (Codex-Nachprüfung, Timing.cs)
        var result = new SimulationResult(10, [Step(audio: 2, finish: 3, lastSegmentEnd: 1.8, suspended: null)], [], null);

        var report = Report.Create(result, speechStart: null);

        Assert.True(report.StandbyUnknown);
        Assert.Contains("unbekannt", report.Summary(), StringComparison.Ordinal);
        Assert.DoesNotContain("kein Standby", report.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void LengthCutsAreCounted()
    {
        var result = new SimulationResult(
            10,
            [],
            [new FixedSegment("a", 0, 5, 6, AtEnd: false, LengthCut: true), new FixedSegment("b", 5, 9, 10, AtEnd: false)],
            null);

        Assert.Equal(1, Report.Create(result, speechStart: null).LengthCuts);
    }

    private static SimulationResult Result(IReadOnlyList<StepRecord> steps) => new(10, steps, [], null);

    private static StepRecord Step(double audio, double finish, double? lastSegmentEnd, double? suspended = 0) =>
        new(audio, audio, finish, finish - audio, lastSegmentEnd is null ? 0 : 1, lastSegmentEnd, suspended);
}
