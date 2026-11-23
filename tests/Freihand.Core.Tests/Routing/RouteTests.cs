using Freihand.Core.Routing;

namespace Freihand.Core.Tests.Routing;

public class RouteTests
{
    [Fact]
    public void OnlyDeviceRecognitionStaysOnDevice()
    {
        Assert.False(RecognitionRoute.Device.LeavesDevice());
        Assert.True(RecognitionRoute.Online.LeavesDevice());
        Assert.True(RecognitionRoute.Home.LeavesDevice());
        Assert.True(RecognitionRoute.CompanyService.LeavesDevice());
    }

    [Fact]
    public void HomeIsNotLocalForTransform()
    {
        Assert.False(TransformRoute.None.LeavesDevice());
        Assert.False(TransformRoute.Local.LeavesDevice());
        Assert.True(TransformRoute.Home.LeavesDevice());
        Assert.True(TransformRoute.PrivateProvider.LeavesDevice());
        Assert.True(TransformRoute.CompanyService.LeavesDevice());
    }

    [Fact]
    public void HighestDataClassWins()
    {
        Assert.Equal(DataClass.Confidential, DataClass.Highest(DataClass.Public, DataClass.Confidential));
        Assert.Equal(DataClass.Confidential, DataClass.Highest(DataClass.Confidential, DataClass.Internal));
        Assert.Equal(DataClass.Internal, DataClass.Highest(DataClass.Internal, DataClass.Internal));
    }

    [Fact]
    public void CompanyDefinedDataClassesCompareByRank()
    {
        var restricted = new DataClass("streng-vertraulich", 3);

        Assert.Equal(restricted, DataClass.Highest(DataClass.Confidential, restricted));
        Assert.True(restricted.CompareTo(DataClass.Confidential) > 0);
    }
}
