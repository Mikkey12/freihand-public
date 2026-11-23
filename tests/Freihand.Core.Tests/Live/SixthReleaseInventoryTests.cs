using Xunit;
namespace Freihand.Core.Tests.Live;
public sealed class SixthReleaseInventoryTests
{
    [Fact]
    public void AudioReleaseFailureHasOnlyOneIdenticalEngineScenario()
    {
        var rows = ReleaseRuleMatrixTests.EngineCases()
            .Where(row => (ReleaseRuleMatrixTests.EngineEnd)row[0] ==
                ReleaseRuleMatrixTests.EngineEnd.AudioReleaseFailsWhileEngineReleaseRuns).ToArray();
        Assert.NotEmpty(rows);
        // Beide Zeilen konfigurieren momentan Audio-Dispose fehlerhaft und Engine-Dispose erfolgreich.
        Assert.Single(rows);
    }
}
