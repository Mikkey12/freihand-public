using Freihand.Core.Tests.Recognition.Kit;
using static Freihand.Core.Tests.Recognition.Kit.RecognitionTestKit;

namespace Freihand.Core.Tests.Recognition;

/// <summary>Rechenzeit der Erkennung je Sitzung: nur gelungene Aufrufe, nur Zahlen.</summary>
public sealed class DecodeStatisticsTests
{
    [Fact]
    public async Task EverySuccessfulDecodeIsCountedWithItsTime()
    {
        await using var harness = await SupervisorHarness.StartedAsync();
        await using var session = await harness.SessionAsync(German());
        Assert.Equal(default, session.Decodes);

        await session.Decoder.DecodeAsync(Samples(16_000), Sec(1), German()).WaitAsync(Guard);
        await session.Decoder.DecodeAsync(Samples(16_000), Sec(2), German()).WaitAsync(Guard);

        var decodes = session.Decodes;
        Assert.Equal(2, decodes.Count);
        Assert.True(decodes.Longest > TimeSpan.Zero);
        Assert.True(decodes.Total >= decodes.Longest);
    }
}
