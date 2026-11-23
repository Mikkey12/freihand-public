using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Freihand.Core.Tests.Profiles;

public sealed class SecondProfileFollowupTests
{
    // OF-01: Das reale Befehlsmuster enthält Unicode-Buchstaben, auch ausserhalb der Basic Multilingual Plane.
    [Theory]
    [InlineData(0x10400)]
    [InlineData(0x1D400)]
    public void R01_CommandPatternAcceptsSupplementaryUnicodeLetters(int codePoint)
    {
        var schema = RepositoryFiles.ReadSchema("commands.v1.schema.json");
        var pattern = schema["$defs"]!["command"]!["properties"]!["phrases"]!["items"]!["pattern"]!.GetValue<string>();
        var rule = new JsonObject { ["type"] = "string", ["pattern"] = pattern };
        var value = char.ConvertFromUtf32(codePoint);
        Assert.Equal(UnicodeCategory.UppercaseLetter, Rune.GetUnicodeCategory(new Rune(codePoint)));
        Assert.True(new MiniSchemaValidator(rule).IsValid(JsonValue.Create(value)));
    }
}
