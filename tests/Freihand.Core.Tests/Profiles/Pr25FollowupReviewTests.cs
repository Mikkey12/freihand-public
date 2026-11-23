using System.Text.Json.Nodes;

namespace Freihand.Core.Tests.Profiles;

public sealed class Pr25FollowupReviewTests
{
    // OF-01: Das tatsächlich verwendete Textmuster muss auch für Unicode-Leerraum ECMA-262 entsprechen.
    [Theory]
    [InlineData("\uFEFFAlpha", false)]
    [InlineData("Alpha\uFEFF", false)]
    [InlineData("\u0085Alpha", true)]
    [InlineData("Alpha\u0085", true)]
    public void N01_TextPatternUsesEcmaWhitespace(string text, bool expected)
    {
        var schema = RepositoryFiles.ReadSchema("profiles.v2.schema.json");
        var textSchema = schema["$defs"]!["text"]!.DeepClone().AsObject();
        var actual = new MiniSchemaValidator(textSchema).IsValid(JsonValue.Create(text));
        Assert.Equal(expected, actual);
    }
}
