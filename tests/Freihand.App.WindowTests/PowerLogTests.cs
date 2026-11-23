using Microsoft.Win32;

namespace Freihand.App;

/// <summary>Standby und Aufwachen im Protokoll.</summary>
public sealed class PowerLogTests
{
    [Theory]
    [InlineData(PowerModes.Suspend, "ereignis standby")]
    [InlineData(PowerModes.Resume, "ereignis aufgewacht")]
    [InlineData(PowerModes.StatusChange, null)]
    public void OnlySuspendAndResumeAreLogged(PowerModes mode, string? line) => Assert.Equal(line, Shell.PowerLine(mode));
}
