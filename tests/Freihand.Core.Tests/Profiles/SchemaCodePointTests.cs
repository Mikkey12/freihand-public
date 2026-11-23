using System.Globalization;
using System.Text.Json.Nodes;
using Freihand.Core.Commands;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Die Schema-Testhilfe wertet Muster mit der Bedeutung des Flags <c>u</c> von ECMA 262 aus: auf Codepunkten, nicht
/// auf UTF-16-Einheiten (Codex-Prüfung PR #25, R01). Je Unterschied, der in den Mustern unter <c>schemas/</c> vorkommt,
/// ein eigener Test: <c>\p{L}</c>, <c>.</c>, <c>\S</c>, verneinte Zeichenklassen, <c>\d</c>/<c>\w</c>, einzelne Ersatzzeichen
/// und <c>maxLength</c>.
/// </summary>
public class SchemaCodePointTests
{
    private static string Cp(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static bool Accepts(string pattern, string text) =>
        new MiniSchemaValidator(new JsonObject { ["type"] = "string", ["pattern"] = pattern }).IsValid(JsonValue.Create(text));

    private static string PhrasePattern => CommandSchema.PhrasePattern;

    public static TheoryData<int> SupplementaryLetters =>
    [
        0x10400, // Lu, Deseret
        0x10428, // Ll, Deseret
        0x1D400, // Lu, mathematisches Alphabet
        0x20000, // Lo, CJK-Zeichen
        0x16F93, // Lm, Miao
    ];

    public static TheoryData<int> SupplementaryNonLetters =>
    [
        0x1F600, // So, Emoji
        0x1D7CE, // Nd, mathematische Ziffer
        0xE0041, // Cf, unsichtbares Tag-Zeichen
        0x10107, // No, ägäische Zahl
    ];

    // \p{L}

    [Theory]
    [MemberData(nameof(SupplementaryLetters))]
    public void BackslashPLMatchesASupplementaryLetterAsOneCharacter(int codePoint)
    {
        Assert.True(Accepts(@"^\p{L}$", Cp(codePoint)));
        Assert.True(Accepts(@"^\p{L}+$", Cp(codePoint) + Cp(codePoint)));
        Assert.True(Accepts(PhrasePattern, "Komma" + Cp(codePoint)));
        Assert.True(Accepts(PhrasePattern, Cp(codePoint) + " " + Cp(codePoint)));
    }

    [Theory]
    [MemberData(nameof(SupplementaryNonLetters))]
    public void BackslashPLDoesNotMatchASupplementaryNonLetter(int codePoint)
    {
        Assert.False(Accepts(@"^\p{L}$", Cp(codePoint)));
        Assert.False(Accepts(PhrasePattern, "Komma" + Cp(codePoint)));
    }

    [Fact]
    public void BackslashPNdMatchesASupplementaryDigitAsOneCharacter()
    {
        Assert.True(Accepts(@"^\p{Nd}$", Cp(0x1D7CE)));
        Assert.False(Accepts(@"^\p{Nd}$", Cp(0x1F600)));
        Assert.False(Accepts(@"^\P{Nd}$", Cp(0x1D7CE)));
        Assert.True(Accepts(@"^\P{Nd}$", Cp(0x1F600)));
    }

    // Der Punkt, \S und verneinte Klassen nehmen genau einen Codepunkt

    [Fact]
    public void TheDotTakesOneCodePointNotTwoUtf16Units()
    {
        Assert.True(Accepts("^.$", Cp(0x1F600)));
        Assert.False(Accepts("^..$", Cp(0x1F600)));
        Assert.True(Accepts("^..$", Cp(0x1F600) + Cp(0x1F600)));
        Assert.True(Accepts("^a.b$", "a" + Cp(0x1F600) + "b"));
        Assert.True(Accepts("^.{2}$", Cp(0x1F600) + "a"));
        Assert.False(Accepts("^.{3}$", Cp(0x1F600) + "a"));
    }

    [Fact]
    public void TheDotStillExcludesLineTerminatorsNextToASupplementaryCharacter()
    {
        Assert.False(Accepts("^.{2}$", Cp(0x1F600) + "\n"));
        Assert.False(Accepts("^.{2}$", "\r" + Cp(0x1F600)));
    }

    [Fact]
    public void BackslashSTakesOneCodePoint()
    {
        Assert.True(Accepts(@"^\S$", Cp(0x1F600)));
        Assert.False(Accepts(@"^\S\S$", Cp(0x1F600)));
        Assert.True(Accepts(@"^\S\S$", Cp(0x1F600) + Cp(0x1D400)));
        Assert.False(Accepts(@"^\s$", Cp(0x1F600)));
    }

    [Fact]
    public void ANegatedClassTakesOneCodePoint()
    {
        Assert.True(Accepts("^[^a]$", Cp(0x1F600)));
        Assert.False(Accepts("^[^a][^a]$", Cp(0x1F600)));
        Assert.True(Accepts(@"^[^?#@\s]{1}$", Cp(0x1F600)));
        Assert.True(Accepts(
            RepositoryFiles.ReadSchema("profiles.v2.schema.json")["$defs"]!["endpoint"]!["pattern"]!.GetValue<string>(),
            "https://stt.example.com/v1/" + Cp(0x1F600)));
    }

    [Fact]
    public void TheTextPatternAcceptsASupplementaryCharacterAtBothEnds()
    {
        var pattern = RepositoryFiles.ReadSchema("profiles.v2.schema.json")["$defs"]!["text"]!["pattern"]!.GetValue<string>();

        Assert.True(Accepts(pattern, Cp(0x1F600)));
        Assert.True(Accepts(pattern, Cp(0x1F600) + "a" + Cp(0x1F600)));
        Assert.False(Accepts(pattern, Cp(0x1F600) + " "));
    }

    // \d und \w: ASCII auch bei Zeichen ausserhalb der Basisebene

    [Fact]
    public void BackslashDAndBackslashWAreAsciiOnlyForSupplementaryCharactersToo()
    {
        Assert.False(Accepts(@"^\d$", Cp(0x1D7CE)));
        Assert.True(Accepts(@"^\D$", Cp(0x1D7CE)));
        Assert.False(Accepts(@"^\w$", Cp(0x1D400)));
        Assert.True(Accepts(@"^\W$", Cp(0x1D400)));
        Assert.False(Accepts(@"^\W\W$", Cp(0x1D400)));
    }

    // Einzelne Ersatzzeichen sind je ein Zeichen der Kategorie Cs

    [Fact]
    public void ALoneSurrogateIsOneCharacterAndNoLetter()
    {
        var highAlone = ((char)0xD800).ToString();
        var lowThenHigh = (char)0xDC00 + highAlone;

        Assert.True(Accepts("^.$", highAlone));
        Assert.True(Accepts(@"^\S$", highAlone));
        Assert.False(Accepts(@"^\p{L}$", highAlone));
        Assert.True(Accepts("^..$", lowThenHigh));
        Assert.False(Accepts("^.$", lowThenHigh));
    }

    // maxLength zählt Codepunkte (RFC 8259)

    [Fact]
    public void MaxLengthCountsCodePoints()
    {
        var rule = new JsonObject { ["type"] = "string", ["maxLength"] = 2 };
        var validator = new MiniSchemaValidator(rule);

        Assert.True(validator.IsValid(JsonValue.Create(Cp(0x1F600) + Cp(0x1F601))));
        Assert.False(validator.IsValid(JsonValue.Create(Cp(0x1F600) + Cp(0x1F601) + "a")));
        Assert.True(validator.IsValid(JsonValue.Create(((char)0xD800).ToString() + ((char)0xD800))));
    }

    // Die Stellvertreter

    [Fact]
    public void EveryCategoryOfASupplementaryCodePointHasAProxyOfTheSameCategory()
    {
        var seen = new HashSet<UnicodeCategory>();
        for (var codePoint = 0x10000; codePoint <= 0x10FFFF; codePoint++)
        {
            seen.Add(CharUnicodeInfo.GetUnicodeCategory(codePoint));
        }

        Assert.All(seen, category => Assert.Contains(category, MiniSchemaValidator.CategoryProxies.Keys));
        foreach (var (category, proxy) in MiniSchemaValidator.CategoryProxies)
        {
            Assert.Equal(category, CharUnicodeInfo.GetUnicodeCategory(proxy));
            Assert.True(proxy > 127);
            Assert.False(char.IsSurrogate(proxy));
        }
    }

    [Fact]
    public void ProxiesAreNoWhiteSpaceExceptForTheSeparatorCategories()
    {
        var whiteSpace = SchemaPatternTests.EcmaWhiteSpace;
        foreach (var (category, proxy) in MiniSchemaValidator.CategoryProxies)
        {
            var expected = category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
            Assert.Equal(expected, whiteSpace.Contains(proxy));
        }
    }

    [Theory]
    [InlineData(@"^\p{IsGreek}$")]
    [InlineData(@"^\p{Script=Greek}$")]
    [InlineData("^\\" + "u0041$")]
    [InlineData(@"^\x41$")]
    [InlineData(@"^\cA$")]
    [InlineData(@"^(?<name>a)\k<name>$")]
    public void PatternsThatTheProxyCannotCarryFailLoudly(string pattern) =>
        Assert.Throws<NotSupportedException>(() => MiniSchemaValidator.EcmaPattern(pattern));

    [Fact]
    public void PatternsWithNonAsciiCharactersFailLoudly() =>
        Assert.Throws<NotSupportedException>(() => MiniSchemaValidator.EcmaPattern("^" + (char)0x00E9 + "$"));
}
