using System.Diagnostics;
using System.Reflection;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests.App;

public sealed class Pr57DelayCounterTests
{
    [Fact]
    public async Task ClockDriftMustNotBecomeAnApparentlyExactZeroDelay()
    {
        await using var rig = new ControllerRig(arrange: r => r.Audio.OnOpen = session =>
            session.Chunks.Add(new AudioChunk(new byte[6400], 16000, TimeSpan.Zero)));
        var run = await rig.StartRecordingAsync();
        await rig.WaitUntil(() => rig.Log.Contains("Capture.Chunk"), "erster Block gelesen");

        // Kontrollierte Nachstellung: Nach einer Stunde liegt die Audiozeit um 360 ms vor der Stoppuhr
        // (100 ppm). Nicht eine Stunde warten und keine echten Aufnahmen benutzen.
        var dictation = typeof(Freihand.Adapters.Dictation.DictationController)
            .GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Controller)!;
        dictation.GetType().GetProperty("AudioZero")!.SetValue(dictation,
            Stopwatch.GetTimestamp() - (long)(3599.64 * Stopwatch.Frequency));
        run.Emit(new TextOutput(new RecognitionUpdate(
            [new TextSegment(Guid.NewGuid(), "Erfundene Probe", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromHours(1))], [])));
        await rig.WaitUntil(() => rig.LogLines.Any(line => line.StartsWith("ereignis abschnitt_geschrieben ", StringComparison.Ordinal)), "Messwert");
        await rig.EndAsync(run);

        Assert.DoesNotContain("ereignis abschnitt_geschrieben verzug_ms=0", rig.LogLines);
    }
}
