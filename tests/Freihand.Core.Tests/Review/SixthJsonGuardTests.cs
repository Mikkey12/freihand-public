using Xunit;
namespace Freihand.Core.Tests.Review;
public sealed class SixthJsonGuardTests
{
    [Theory]
    [InlineData("global using static System.Text.Json.JsonSerializer;\nstatic class C { public static object? Read(string json) => Deserialize<object>(json); }")]
    [InlineData("global using J = System.Text.Json.JsonDocument;\nstatic class C { public static object Read(string json) => J.Parse(json); }")]
    [InlineData("static class C { public static object? Read(string json) { var url = \"https://example.invalid\"; return System.Text.Json.JsonSerializer.Deserialize<object>(json); } }")]
    public void GuardMustDetectAReaderOrItsForbiddenImport(string code)
    {
        Assert.Single(JsonInputUnicodeMatrixTests.JsonReaderInventory("control.cs", "var value = JsonSerializer.Deserialize<object>(json);"));
        Assert.NotEmpty(JsonInputUnicodeMatrixTests.JsonReaderInventory("new.cs", code));
    }
}
