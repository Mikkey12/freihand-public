using Xunit;
namespace Freihand.Core.Tests.Review;
public sealed class SeventhGuardRecommendationTests
{
    [Fact]
    public void ProjectImportAttributeOrderDoesNotChangeDetection()
    {
        Assert.Single(JsonInputUnicodeMatrixTests.JsonReaderInventory("x.csproj",
            "<Using Include=\"System.Text.Json.JsonSerializer\" Static=\"true\" />"));
        Assert.Single(JsonInputUnicodeMatrixTests.JsonReaderInventory("x.csproj",
            "<Using Static=\"true\" Include=\"System.Text.Json.JsonSerializer\" />"));
    }
    [Fact]
    public void InterpolationFormatTextIsNotExecutableCode()
    {
        Assert.Single(JsonInputUnicodeMatrixTests.JsonReaderInventory("x.cs",
            "var text = $\"{JsonDocument.Parse(json)}\";"));
        Assert.Empty(JsonInputUnicodeMatrixTests.JsonReaderInventory("x.cs",
            "var text = $\"{value:JsonDocument.Parse(json)}\";"));
    }
}
