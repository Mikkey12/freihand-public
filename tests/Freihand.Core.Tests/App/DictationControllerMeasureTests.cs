using System.Diagnostics;
using Freihand.Adapters.Dictation;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Profiles;
using Freihand.Adapters.Recognition;
using Freihand.Core.Segments;
using Freihand.Core.Platform;
using Freihand.Core.Recognition;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Messwerte für die Abnahmen RM10 (Start: Tastendruck bis Recording) und RM11 (Fehlerstopp: letzter Audioblock bis
/// Stopp), P8 Kapitel 10. Nur Zahlen, nie Text (SI-01).
/// </summary>
public sealed class DictationControllerMeasureTests
{
    private const string TextMarker = "GEHEIM-4713";

    private static long Ago(TimeSpan span) => Stopwatch.GetTimestamp() - (long)(span.TotalSeconds * Stopwatch.Frequency);

    private static ControllerRig NewRig()
    {
        var rig = new ControllerRig();
        rig.Controller.RegisterHotkeys();
        return rig;
    }

    private static async Task PressAndRecordAsync(ControllerRig rig, TimeSpan ago, int number = 1)
    {
        rig.Hotkeys.Press(HotkeyAction.Record, Ago(ago));
        await rig.WaitUntilOpenAsync(number);
        rig.Audio.Sessions[number - 1].SetRecording();
        await rig.WaitForPhase(ControllerPhase.Recording);
    }

    // RM10: Die Spanne läuft vom Zeitstempel des Tastendrucks (Stopwatch) bis Recording.
    [Fact]
    public async Task M01_StartMsRunsFromTheKeyTimestampToRecording()
    {
        await using var rig = NewRig();
        await PressAndRecordAsync(rig, TimeSpan.FromMilliseconds(250));

        var start = rig.Controller.Current.StartMs;
        Assert.NotNull(start);
        Assert.InRange(start.Value, 250, 30_000);
        Assert.EndsWith($"start_ms={start} stopp_ms=-", rig.Controller.Current.LogLine(), StringComparison.Ordinal);
        Assert.Contains(rig.LogLines, line => line.Contains($"start_ms={start} ", StringComparison.Ordinal));
        Assert.All(rig.Updates.Where(update => update.Phase == ControllerPhase.Starting), update => Assert.Null(update.StartMs));
    }

    // RM10: Der zweite Tastendruck beendet die Aufnahme und erzeugt oder ändert keinen Startwert.
    [Fact]
    public async Task M02_SecondKeyPressCreatesNoStartValue()
    {
        await using var rig = NewRig();
        await PressAndRecordAsync(rig, TimeSpan.FromMilliseconds(300));
        var first = rig.Controller.Current.StartMs;
        Assert.NotNull(first);

        rig.Hotkeys.Press(HotkeyAction.Record, Ago(TimeSpan.FromSeconds(20)));
        await rig.WaitForPhase(ControllerPhase.Stopping);
        rig.Factory.LastRun!.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.All(
            rig.Updates.Where(update => update.Phase != ControllerPhase.Starting),
            update => Assert.Equal(first, update.StartMs));
        Assert.Equal(first, rig.Controller.Current.StartMs);
    }

    // RM10: Ohne Tastenereignis (Aufruf von RecordAsync) gibt es keinen Startwert; ein neues Diktat löscht den alten.
    [Fact]
    public async Task M03_StartWithoutKeyEventHasNoValueAndNewDictationClearsTheOldOne()
    {
        await using var rig = NewRig();
        await PressAndRecordAsync(rig, TimeSpan.FromMilliseconds(200));
        Assert.NotNull(rig.Controller.Current.StartMs);
        rig.Hotkeys.Press(HotkeyAction.Record);
        await rig.WaitForPhase(ControllerPhase.Stopping);
        rig.Factory.LastRun!.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.WaitUntilOpenAsync(2);
        Assert.Null(rig.Controller.Current.StartMs);
        rig.Audio.Sessions[1].SetRecording();
        await rig.WaitForPhase(ControllerPhase.Recording);

        Assert.Null(rig.Controller.Current.StartMs);
        Assert.Contains("start_ms=- ", rig.Controller.Current.LogLine(), StringComparison.Ordinal);
    }

    // RM11: Fehlerstopp mit Grund: Zeit vom letzten Block bis zum Stopp, auf ganze Millisekunden gerundet.
    [Theory]
    [InlineData(CaptureStopReason.DeviceRemoved, 120.0, 120)]
    [InlineData(CaptureStopReason.NoSignal, 1000.0, 1000)]
    [InlineData(CaptureStopReason.AccessDenied, 119.6, 120)]
    public async Task M04_FailureStopCarriesTheTimeSinceTheLastBlock(CaptureStopReason reason, double milliseconds, int expected)
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();

        rig.Audio.Session!.Fail(reason, TimeSpan.FromMilliseconds(milliseconds));
        await rig.WaitForPhase(ControllerPhase.Stopping);

        Assert.Contains(rig.Updates, update => update.StopMs == expected);
        Assert.Contains(rig.LogLines, line => line.EndsWith($" stopp_ms={expected}", StringComparison.Ordinal));
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
    }

    // RM11: Ein regulärer Stopp ist kein Fehlerstopp und hat keinen Stoppwert.
    [Fact]
    public async Task M05_RequestedStopHasNoStopValue()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();

        await rig.EndAsync(run);

        Assert.All(rig.Updates, update => Assert.Null(update.StopMs));
        // Nur Zustandszeilen tragen Messwerte; Ereigniszeilen (Beta B4/B6) stehen davor
        var states = rig.LogLines.Where(line => line.StartsWith("phase=", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(states);
        Assert.All(states, line => Assert.EndsWith("stopp_ms=-", line, StringComparison.Ordinal));
        Assert.All(rig.LogLines.Except(states), line => Assert.StartsWith("ereignis ", line, StringComparison.Ordinal));
    }

    // RM11: Kam nie ein Block, gibt es keinen Wert ("-"); das nächste Diktat beginnt wieder ohne Stoppwert.
    [Fact]
    public async Task M06_NoBlockMeansNoValueAndTheNextDictationStartsClean()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        rig.Audio.Session!.Fail(CaptureStopReason.DeviceRemoved, TimeSpan.FromMilliseconds(80));
        await rig.WaitForPhase(ControllerPhase.Stopping);
        Assert.Equal(80, rig.Controller.Current.StopMs);
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.WaitUntilOpenAsync(2);
        Assert.Null(rig.Controller.Current.StopMs);
        rig.Audio.Sessions[1].Fail(CaptureStopReason.AccessDenied);
        await rig.WaitUntil(
            () => rig.Controller.Current.Phase == ControllerPhase.Stopping
                && rig.Controller.Current.Reason == nameof(CaptureStopReason.AccessDenied),
            "zweiter Stopp");

        Assert.Null(rig.Controller.Current.StopMs);
        Assert.EndsWith(" stopp_ms=-", rig.Controller.Current.LogLine(), StringComparison.Ordinal);
    }

    // LogLine: Format der beiden Felder, Zahlen oder "-", nie Text.
    [Fact]
    public void M07_LogLineFormatsBothValuesAndCarriesNoText()
    {
        var empty = Update(null, null);
        var full = Update(212, 1_034);

        Assert.EndsWith("erkennerfehler=- start_ms=- stopp_ms=-", empty.LogLine(), StringComparison.Ordinal);
        Assert.EndsWith("erkennerfehler=- start_ms=212 stopp_ms=1034", full.LogLine(), StringComparison.Ordinal);
        Assert.DoesNotContain(TextMarker, full.LogLine(), StringComparison.Ordinal);
    }

    private static ControllerUpdate Update(int? start, int? stop) => new(
        ControllerPhase.Recording,
        RecognizerAvailability.Ready,
        TextMarker,
        new ModeDecision(LiveMode.Sentence, RecognitionMode.Live, ModeReason.NotApproved),
        $"{TextMarker} vorläufig",
        [new OutputEntry(Guid.NewGuid(), EntryStatus.Written, OutputReason.None, $"{TextMarker} geschrieben", "")],
        ControllerMessage.None,
        null,
        StartMs: start,
        StopMs: stop);
}
