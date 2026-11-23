using Freihand.Adapters.Dictation;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Recovery;

namespace Freihand.Core.Tests.App;

public sealed class Pr60DeltaConsentCounterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Freihand-Pr60-Delta-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DisableMustStayEffectiveAfterAnUnreadableConsentFileIsUnlocked()
    {
        if (!OperatingSystem.IsWindows()) return;
        var time = new ManualTimeProvider();
        var protector = new FakeDataProtector();
        using var recorder = new ReviewRecorder(_root, protector, time);
        Assert.True(recorder.Enable());
        var oldRecording = recorder.Begin(1)!;
        oldRecording.Audio(new AudioChunk(new byte[640], 16000, TimeSpan.Zero));

        using (var locked = new FileStream(Path.Combine(_root, ReviewRecorder.StateFile), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            recorder.Disable();
        }

        // Kein Neueinschalten: Freigabe und alte Aufnahme muessen nach dem Entsperren widerrufen bleiben.
        var oldName = oldRecording.Complete();
        using var restarted = new ReviewRecorder(_root, protector, time);
        restarted.Cleanup();
        var nextRecording = restarted.Begin(2);
        Assert.Null(oldName);
        Assert.Null(recorder.ActiveUntil);
        Assert.Null(nextRecording);
    }

    public void Dispose()
    {
        var absolute = Path.GetFullPath(_root);
        if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(absolute).StartsWith("Freihand-Pr60-Delta-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
