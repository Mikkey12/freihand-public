using System.Text.Json;
using Freihand.Core.Platform;
using Freihand.Core.Routing;
using Freihand.Core.Tests.Platform.Fakes;

namespace Freihand.Core.Tests.Platform;

/// <summary>Wächter und fremde Inhalte aus dem P7-Konzept, Kapitel 4.4, 5.5 und 5.9; Abnahmetests W18 und W19.</summary>
public sealed class GuardTests
{
    private static readonly TimeSpan DesktopPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>Wächter mit Signal-Attrappe und manueller Uhr; <see cref="Stops"/> sammelt jedes <c>StopRequired</c>.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Guard = new RecordingGuard(Signals, new GuardOptions(DesktopPoll), Clock);
            Guard.StopRequired += (_, reason) => Stops.Add(reason);
        }

        public FakeGuardSignals Signals { get; } = new();

        public ManualClock Clock { get; } = new();

        public RecordingGuard Guard { get; }

        public List<GuardReason> Stops { get; } = [];

        public void Dispose() => Guard.Dispose();
    }

    // ---------------------------------------------------------------- W18: Wächter

    [Fact]
    public void W18_SessionLockedAfterArmRequestsExactlyOneStop()
    {
        using var rig = new Rig();
        rig.Guard.Arm();

        rig.Signals.RaiseSession(GuardReason.SessionLocked);

        Assert.Equal([GuardReason.SessionLocked], rig.Stops);
    }

    [Theory]
    [InlineData("Winlogon")]
    [InlineData(null)]
    public void W18_InputDesktopOtherThanDefaultOrUnreadableRequestsStopAtNextPoll(string? desktop)
    {
        using var rig = new Rig();
        rig.Guard.Arm();
        rig.Signals.Desktop = desktop;

        rig.Clock.Advance(DesktopPoll);

        Assert.Equal([GuardReason.SecureDesktop], rig.Stops);
    }

    [Fact]
    public void W18_FocusChangeToPasswordFieldRequestsStop()
    {
        using var rig = new Rig();
        rig.Guard.Arm();

        rig.Signals.RaiseFocus(true);

        Assert.Equal([GuardReason.PasswordField], rig.Stops);
    }

    [Fact]
    public void W18_ArmOnPasswordFieldRequestsStopImmediately()
    {
        using var rig = new Rig();
        rig.Signals.Password = true;

        rig.Guard.Arm();

        Assert.Equal([GuardReason.PasswordField], rig.Stops);
    }

    // Entscheid vom 11.11.2026 (SI-04): Ein nicht lesbares IsPassword beendet die Aufnahme nicht
    [Fact]
    public void W18_UnreadablePasswordPropertyRequestsNothing()
    {
        using var rig = new Rig();
        rig.Signals.Password = null;
        rig.Guard.Arm();

        rig.Signals.RaiseFocus(null);

        Assert.Empty(rig.Stops);
    }

    [Fact]
    public void W18_FocusChangeToNonPasswordRequestsNothing()
    {
        using var rig = new Rig();
        rig.Guard.Arm();

        rig.Signals.RaiseFocus(false);

        Assert.Empty(rig.Stops);
    }

    [Fact]
    public void W18_AfterDisarmNeitherEventsNorQueriesRemain()
    {
        using var rig = new Rig();
        rig.Guard.Arm();
        rig.Guard.Disarm();
        var passwordQueries = rig.Signals.PasswordQueries;
        var desktopQueries = rig.Signals.DesktopQueries;

        rig.Signals.RaiseSession(GuardReason.SessionLocked);
        rig.Signals.RaiseSession(GuardReason.SessionDisconnected);
        rig.Signals.RaiseFocus(true);
        rig.Signals.RaiseFocus(null);
        rig.Signals.Desktop = "Winlogon";
        rig.Clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Empty(rig.Stops);
        Assert.Equal(passwordQueries, rig.Signals.PasswordQueries);
        Assert.Equal(desktopQueries, rig.Signals.DesktopQueries);
    }

    [Fact]
    public void W18_SignalsAreStartedAndStoppedOncePerArmAndDisarm()
    {
        using var rig = new Rig();

        rig.Guard.Arm();

        Assert.Equal(1, rig.Signals.Starts);
        Assert.Equal(0, rig.Signals.Stops);

        rig.Guard.Disarm();

        Assert.Equal(1, rig.Signals.Starts);
        Assert.Equal(1, rig.Signals.Stops);
    }

    // ---------------------------------------------------------------- W19: fremder Text

    private static ForeignText Secret() => new("GEHEIM-4711", ReadPurpose.TargetCheck, DataClass.Confidential);

    [Fact]
    public void W19_ToStringNeverShowsTheContent()
    {
        var text = Secret();

        var shown = text.ToString();

        Assert.DoesNotContain("GEHEIM-4711", shown);
        Assert.Equal("[Fremdtext]", shown);
    }

    [Fact]
    public void W19_InterpolationNeverShowsTheContent()
    {
        var text = Secret();

        var shown = $"Gelesen: {text}";

        Assert.DoesNotContain("GEHEIM-4711", shown);
    }

    [Fact]
    public void W19_JsonNeverContainsTheContent()
    {
        var json = JsonSerializer.Serialize(Secret());

        Assert.DoesNotContain("GEHEIM-4711", json);
    }

    [Fact]
    public void W19_RevealReturnsTheContent()
    {
        Assert.Equal("GEHEIM-4711", Secret().Reveal());
    }
}
