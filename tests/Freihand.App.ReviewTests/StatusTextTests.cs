using Freihand.Adapters.Dictation;

namespace Freihand.App;

/// <summary>Meldungszeile des Statusfensters (RM11, Entscheid vom 09.11.2026).</summary>
public sealed class StatusTextTests
{
    [Fact]
    public void NoSignalAsksWhetherTheDeviceWasDisconnected() =>
        Assert.Equal("Mikrofon beendet: kein Ton vom Mikrofon – Gerät getrennt?",
            StatusText.Message(ControllerMessage.CaptureStopped, "NoSignal"));

    [Theory]
    [InlineData("DeviceRemoved")]
    [InlineData("AccessDenied")]
    public void OtherStopReasonsKeepTheirCode(string reason) =>
        Assert.Equal($"Mikrofon beendet: {reason}", StatusText.Message(ControllerMessage.CaptureStopped, reason));

    [Fact]
    public void NoticesAndNoMessageStayEmpty()
    {
        Assert.Equal(string.Empty, StatusText.Message(ControllerMessage.None, null));
        Assert.Equal(string.Empty, StatusText.Message(ControllerMessage.Notice, "AudioGap"));
    }

    [Fact]
    public void CommandsAreReportedInPlainWords()
    {
        Assert.Equal("Befehl noch nicht unterstützt: undo", StatusText.Message(ControllerMessage.CommandNotSupported, "undo"));
        Assert.Equal("Kein gültiger Befehl erkannt", StatusText.Message(ControllerMessage.CommandHint, null));
    }

    [Fact]
    public void OtherMessagesAreUnchanged() =>
        Assert.Equal("Aufnahme beendet: PasswordField", StatusText.Message(ControllerMessage.GuardStopped, "PasswordField"));
}
