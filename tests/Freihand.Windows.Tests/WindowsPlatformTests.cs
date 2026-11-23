namespace Freihand.Windows.Tests;

public class WindowsPlatformTests
{
    [Fact]
    public void RunsOnSupportedWindows()
    {
        Assert.True(WindowsPlatform.IsSupported);
    }
}
