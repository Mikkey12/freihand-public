using System.Text.Json.Nodes;
using Freihand.Core.Commands;
using Freihand.Core.Profiles;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Zeichen ausserhalb der Basisebene (Codepunkte ab U+10000, in UTF-16 ein Zeichenpaar) im Import
/// (Codex-Prüfung PR #25, R01, Folgerung für den Produktcode): Länge, Zeichenart und Bedeutung wie im JSON-Schema.
/// Das Schema zählt <c>maxLength</c> in Codepunkten (RFC 8259) und wertet Muster auf Codepunkten aus; der Import
/// darf davon nur abweichen, indem er strenger ist, und nie unsichtbare Zeichen durchlassen.
/// </summary>
public class SupplementaryCharacterTests
{
    private static string Cp(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static string Repeat(int codePoint, int count) => string.Concat(Enumerable.Repeat(Cp(codePoint), count));

    private const int Emoji = 0x1F600;
    private const int DeseretLetter = 0x10400;
    private const int TagA = 0xE0041;
    private const int MusicalBeginBeam = 0x1D173;

    private static readonly MiniSchemaValidator ProfileSchema2 = new(RepositoryFiles.ReadSchema("profiles.v2.schema.json"));

    private static readonly MiniSchemaValidator CommandSchema1 = new(RepositoryFiles.ReadSchema("commands.v1.schema.json"));

    /// <summary>Felder mit Längengrenze: Name, Grenze und die Änderung des Beispiels.</summary>
    public static TheoryData<string> LengthLimitedFields =>
    [
        "name",
        "description",
        "vocabulary",
        "deviceId",
        "displayName",
    ];

    private static (int Limit, Func<string, Action<JsonObject>> Change) Field(string name) => name switch
    {
        "name" => (64, v => p => p["name"] = v),
        "description" => (500, v => p => p["description"] = v),
        "vocabulary" => (100, v => p => p["vocabulary"] = new JsonArray(v)),
        "deviceId" => (256, v => p => p["audioSource"]!["deviceId"] = v),
        "displayName" => (64, v => p => p["audioSource"]!["displayName"] = v),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(LengthLimitedFields))]
    public void TheLimitCountsCodePointsLikeTheSchema(string name)
    {
        var (limit, change) = Field(name);
        var atLimit = WithProfileV2(change(Repeat(Emoji, limit)));
        var overLimit = WithProfileV2(change(Repeat(Emoji, limit + 1)));

        Assert.True(ProfileSchema2.IsValid(JsonNode.Parse(atLimit)));
        Assert.False(ProfileSchema2.IsValid(JsonNode.Parse(overLimit)));
        var accepted = ProfileSerializer.Import(atLimit, AllowList);
        Assert.True(accepted.Succeeded, string.Join("\n", accepted.Errors));
        Assert.False(ProfileSerializer.Import(overLimit, AllowList).Succeeded);
    }

    [Fact]
    public void TheEndpointLimitCountsCodePointsLikeTheSchema()
    {
        const string prefix = "https://stt.example.com/v1/";
        var atLimit = WithProfileV2(p => p["recognition"]!["endpoint"] = prefix + Repeat(Emoji, 2048 - prefix.Length));
        var overLimit = WithProfileV2(p => p["recognition"]!["endpoint"] = prefix + Repeat(Emoji, 2048 - prefix.Length + 1));

        Assert.True(ProfileSchema2.IsValid(JsonNode.Parse(atLimit)));
        Assert.False(ProfileSchema2.IsValid(JsonNode.Parse(overLimit)));
        var accepted = ProfileSerializer.Import(atLimit, AllowList);
        Assert.True(accepted.Succeeded, string.Join("\n", accepted.Errors));
        Assert.False(ProfileSerializer.Import(overLimit, AllowList).Succeeded);
    }

    [Fact]
    public void TheCommandLimitsCountCodePointsLikeTheSchema()
    {
        string Phrases(int count) => Repeat(DeseretLetter, count);
        string WithCommand(Action<JsonNode> change)
        {
            var root = JsonNode.Parse(BuiltInCommands.Json)!;
            change(root["commands"]![0]!);
            return root.ToJsonString();
        }

        var phraseAtLimit = WithCommand(c => c["phrases"] = new JsonArray(Phrases(CommandSchema.MaxPhraseLength)));
        var phraseOver = WithCommand(c => c["phrases"] = new JsonArray(Phrases(CommandSchema.MaxPhraseLength + 1)));
        var descriptionAtLimit = WithCommand(c => c["description"] = Repeat(Emoji, CommandSchema.MaxDescriptionLength));
        var descriptionOver = WithCommand(c => c["description"] = Repeat(Emoji, CommandSchema.MaxDescriptionLength + 1));

        Assert.True(CommandSchema1.IsValid(JsonNode.Parse(phraseAtLimit)));
        Assert.False(CommandSchema1.IsValid(JsonNode.Parse(phraseOver)));
        Assert.True(CommandSchema1.IsValid(JsonNode.Parse(descriptionAtLimit)));
        Assert.False(CommandSchema1.IsValid(JsonNode.Parse(descriptionOver)));

        var accepted = CommandSetSerializer.Import(phraseAtLimit);
        Assert.True(accepted.Succeeded, string.Join("\n", accepted.Errors));
        Assert.False(CommandSetSerializer.Import(phraseOver).Succeeded);
        var describedAtLimit = CommandSetSerializer.Import(descriptionAtLimit);
        Assert.True(describedAtLimit.Succeeded, string.Join("\n", describedAtLimit.Errors));
        Assert.False(CommandSetSerializer.Import(descriptionOver).Succeeded);
    }

    // Unsichtbare Zeichen ausserhalb der Basisebene

    public static TheoryData<int> InvisibleSupplementary => [TagA, MusicalBeginBeam, 0xE0001, 0xE007F, 0x1BCA0];

    [Theory]
    [MemberData(nameof(InvisibleSupplementary))]
    public void InvisibleFormatCharactersOutsideTheBasicPlaneAreRejectedInEveryTextField(int codePoint)
    {
        // Die Tag-Zeichen ab U+E0000 sind unsichtbar und tragen ASCII-Text, den kein Mensch liest, ein Sprachmodell aber schon.
        var invisible = Cp(codePoint);
        var changes = new Dictionary<string, Action<JsonObject>>
        {
            ["name"] = p => p["name"] = "Mail" + invisible,
            ["description"] = p => p["description"] = "Mail" + invisible + "online",
            ["vocabulary"] = p => p["vocabulary"] = new JsonArray("Gerber" + invisible),
            ["deviceId"] = p => p["audioSource"]!["deviceId"] = "dev" + invisible + "ice",
            ["displayName"] = p => p["audioSource"]!["displayName"] = invisible + "Headset",
        };

        foreach (var (field, change) in changes)
        {
            var result = ProfileSerializer.Import(WithProfileV2(change), AllowList);

            Assert.False(result.Succeeded, field);
            Assert.Contains(result.Errors, error => error.Message.Contains("Steuer- oder Richtungszeichen", StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(InvisibleSupplementary))]
    public void InvisibleFormatCharactersOutsideTheBasicPlaneAreRejectedInEndpointsAndCommands(int codePoint)
    {
        var invisible = Cp(codePoint);

        var endpoint = ProfileSerializer.Import(WithProfileV2(p => p["recognition"]!["endpoint"] = "https://stt.example.com/v1/a" + invisible), AllowList);
        Assert.False(endpoint.Succeeded);
        Assert.Contains(endpoint.Errors, error => error.Path == "$.profiles[0].recognition.endpoint");

        var root = JsonNode.Parse(BuiltInCommands.Json)!;
        root["commands"]![0]!["description"] = "Beschreibung" + invisible;
        Assert.False(CommandSetSerializer.Import(root.ToJsonString()).Succeeded);
    }

    [Fact]
    public void VisibleSupplementaryCharactersStayAllowed()
    {
        var json = WithProfileV2(p =>
        {
            p["name"] = "Mail " + Cp(Emoji);
            p["vocabulary"] = new JsonArray(Cp(DeseretLetter) + Cp(DeseretLetter), "Gerber " + Cp(0x20000));
        });

        var result = ProfileSerializer.Import(json, AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var exported = ProfileSerializer.Export(result.Value);
        Assert.Equal(result.Value, ProfileSerializer.Import(exported, AllowList).Value);
    }

    // Wendungen mit Buchstaben ausserhalb der Basisebene

    [Fact]
    public void CommandPhrasesAcceptSupplementaryLettersLikeTheSchema()
    {
        var root = JsonNode.Parse(BuiltInCommands.Json)!;
        root["commands"]![0]!["phrases"] = new JsonArray(Cp(DeseretLetter) + Cp(0x10428), "Komma" + Cp(0x20000) + "-" + Cp(0x1D400));

        Assert.True(CommandSchema1.IsValid(root));
        var result = CommandSetSerializer.Import(root.ToJsonString());
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
    }

    [Theory]
    [InlineData(Emoji)]
    [InlineData(0x1D7CE)]
    public void CommandPhrasesRejectSupplementaryNonLettersLikeTheSchema(int codePoint)
    {
        var root = JsonNode.Parse(BuiltInCommands.Json)!;
        root["commands"]![0]!["phrases"] = new JsonArray("Komma" + Cp(codePoint));

        Assert.False(CommandSchema1.IsValid(root));
        Assert.False(CommandSetSerializer.Import(root.ToJsonString()).Succeeded);
    }

    [Theory]
    [InlineData("Komma", true)]
    [InlineData("neue Zeile", true)]
    [InlineData("rock'n-roll", true)]
    [InlineData("", false)]
    [InlineData(" Komma", false)]
    [InlineData("Komma ", false)]
    [InlineData("neue  Zeile", false)]
    [InlineData("-Komma", false)]
    [InlineData("Komma-", false)]
    [InlineData("Komma2", false)]
    public void IsPhraseFollowsThePattern(string phrase, bool expected) => Assert.Equal(expected, CommandSchema.IsPhrase(phrase));

    // Einzelne Ersatzzeichen

    [Fact]
    public void ALoneSurrogateInTheJsonTextIsAnImportErrorNotAnException()
    {
        var withLoneSurrogate = FullDocumentV2.Replace("Mail online", "Mail" + (char)0xD800, StringComparison.Ordinal);

        var result = ProfileSerializer.Import(withLoneSurrogate, AllowList);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path == "$");
        Assert.False(CommandSetSerializer.Import("{ \"kind\": \"freihand.commands\", \"x\": \"" + (char)0xDC00 + "\" }").Succeeded);
    }

    [Fact]
    public void AnEscapedLoneSurrogateIsAnImportError()
    {
        var escaped = FullDocumentV2.Replace("Mail online", "Mail" + (char)92 + "ud800", StringComparison.Ordinal);

        var result = ProfileSerializer.Import(escaped, AllowList);

        Assert.False(result.Succeeded);
    }
}
