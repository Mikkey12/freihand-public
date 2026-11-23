using Freihand.Adapters.Audio;
using Freihand.Core.Platform;
using Freihand.Core.Tests.Audio.Kit;

namespace Freihand.Core.Tests.Audio;

/// <summary>
/// Anzeige „Mikrofon stumm“ (Bedienregel vom 19.11.2026): Die Aufnahme meldet den Stummschalter von Windows
/// schon beim Öffnen und jede Änderung; das ist ein Hinweis, nie ein Stopp.
/// </summary>
public sealed class MicrophoneMuteTests
{
    [Fact]
    public async Task AMutedDeviceIsReportedFromTheStartAndEveryChangeFollows()
    {
        await using var rig = CaptureRig.Open(configure: factory => factory.EndpointMuted = true);
        var backend = await rig.StartedAsync();
        Assert.True(rig.Session.Status.EndpointMuted);

        rig.FeedFor(TimeSpan.FromSeconds(0.3));
        var recording = await rig.WaitForAsync(status => status.Phase == CapturePhase.Recording, "Recording");
        Assert.True(recording.EndpointMuted);

        backend.SetMuted(false);
        await rig.WaitForAsync(status => !status.EndpointMuted && status.Phase == CapturePhase.Recording, "freigeschaltet");
        backend.SetMuted(true);
        await rig.WaitForAsync(status => status.EndpointMuted && status.Phase == CapturePhase.Recording, "wieder stumm");

        Assert.True(backend.IsRunning);
        Assert.Equal(0, backend.StopCalls);
    }

    // Eigene Vorprüfung: Ein Wechsel zwischen Lesen und Abonnieren darf nicht verloren gehen
    [Fact]
    public async Task AChangeWhileReadingTheSwitchIsNotLost()
    {
        var backend = new RacingBackend();

        await using var session = new CaptureSession(new AudioDevice("mikro", "Mikro"), _ => backend, CaptureOptions.Default, TimeProvider.System);

        Assert.False(session.Status.EndpointMuted);
    }

    /// <summary>Meldet beim Lesen „stumm“ und schaltet im selben Moment frei.</summary>
    private sealed class RacingBackend : ICaptureBackend
    {
        public CaptureFormat Format => new(16000, 1, CaptureSampleFormat.Pcm16);

        public event EventHandler<CaptureData>? DataAvailable { add { } remove { } }

        public event EventHandler<CaptureStopReason?>? Stopped { add { } remove { } }

        public event EventHandler<bool>? MuteChanged;

        public bool? EndpointMuted
        {
            get
            {
                MuteChanged?.Invoke(this, false);
                return true;
            }
        }

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task WithoutWindowsInformationTheDeviceCountsAsNotMuted()
    {
        await using var rig = CaptureRig.Open();
        await rig.StartedAsync();

        Assert.False(rig.Session.Status.EndpointMuted);
    }

    [Fact]
    public async Task AChangeAfterTheStopIsIgnored()
    {
        await using var rig = CaptureRig.Open();
        var backend = await rig.StartedAsync();
        rig.FeedFor(TimeSpan.FromSeconds(0.3));
        await rig.WaitForAsync(status => status.Phase == CapturePhase.Recording, "Recording");

        rig.Session.Stop();
        await rig.StoppedAsync();
        backend.SetMuted(true);

        Assert.False(rig.Session.Status.EndpointMuted);
    }
}
