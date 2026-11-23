using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Freihand.Core.Commands;
using Freihand.Core.Profiles;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Die Schema-Testhilfe wertet die Muster der Schemas wie JSON Schema aus, also nach ECMA 262, nicht nach .NET
/// (Codex-Prüfung PR #25, V02 und N01). Je Unterschied zwischen den beiden Dialekten, der in den Mustern unter
/// <c>schemas/</c> vorkommt, steht hier ein eigener Test; dazu der Abgleich, dass der Import abweist, was das
/// Schema abweist.
/// </summary>
public class SchemaPatternTests
{
    /// <summary>ECMA 262, WhiteSpace und LineTerminator, einzeln aufgezählt (ohne Bereiche).</summary>
    public static readonly char[] EcmaWhiteSpace =
    [
        '\t', '\n', '\v', '\f', '\r', ' ', '\u00A0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004',
        '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200A', '\u2028', '\u2029', '\u202F', '\u205F', '\u3000', '\uFEFF',
    ];

    /// <summary>Zeichen, die .NET als Leerraum kennt (<c>\s</c>), ECMA 262 aber nicht.</summary>
    public static readonly char[] DotNetOnlyWhiteSpace = ['\u0085'];

    /// <summary>Zeichen, die weder .NET noch ECMA 262 als Leerraum kennen, obwohl sie unsichtbar sind.</summary>
    public static readonly char[] NeverWhiteSpace = ['\u001C', '\u001F', '\u180E', '\u200B', '\u200C', '\u2060'];

    public static TheoryData<char> EcmaWhiteSpaceCases => [.. EcmaWhiteSpace];

    public static TheoryData<char> NotEcmaWhiteSpaceCases => [.. DotNetOnlyWhiteSpace, .. NeverWhiteSpace];

    private static MiniSchemaValidator For(string pattern) =>
        new(new JsonObject { ["type"] = "string", ["pattern"] = pattern });

    private static bool Accepts(string pattern, string text) => For(pattern).IsValid(JsonValue.Create(text));

    private static string TextPattern =>
        RepositoryFiles.ReadSchema("profiles.v2.schema.json")["$defs"]!["text"]!["pattern"]!.GetValue<string>();

    private static string EndpointPattern =>
        RepositoryFiles.ReadSchema("profiles.v2.schema.json")["$defs"]!["endpoint"]!["pattern"]!.GetValue<string>();

    // \s und \S: Leerraum nach ECMA 262 (U+FEFF ja, U+0085 nein)

    [Theory]
    [MemberData(nameof(EcmaWhiteSpaceCases))]
    public void BackslashSMatchesEveryEcmaWhiteSpaceCharacter(char c)
    {
        Assert.True(Accepts(@"^\s$", c.ToString()));
        Assert.False(Accepts(@"^\S$", c.ToString()));
    }

    [Theory]
    [MemberData(nameof(NotEcmaWhiteSpaceCases))]
    public void BackslashSDoesNotMatchWhatEcmaDoesNotCallWhiteSpace(char c)
    {
        Assert.False(Accepts(@"^\s$", c.ToString()));
        Assert.True(Accepts(@"^\S$", c.ToString()));
    }

    [Theory]
    [MemberData(nameof(EcmaWhiteSpaceCases))]
    public void TheTextPatternRejectsEcmaWhiteSpaceAtBothEnds(char c)
    {
        Assert.False(Accepts(TextPattern, c + "Alpha"));
        Assert.False(Accepts(TextPattern, "Alpha" + c));
    }

    [Theory]
    [MemberData(nameof(NotEcmaWhiteSpaceCases))]
    public void TheTextPatternAcceptsWhatEcmaDoesNotCallWhiteSpaceAtBothEnds(char c)
    {
        Assert.True(Accepts(TextPattern, c + "Alpha"));
        Assert.True(Accepts(TextPattern, "Alpha" + c));
    }

    [Theory]
    [MemberData(nameof(EcmaWhiteSpaceCases))]
    public void BackslashSInsideAClassIsEcmaWhiteSpaceToo(char c)
    {
        // Das Muster der Endpunkte: [^?#@\s]+
        Assert.False(Accepts(EndpointPattern, "https://stt.example.com/v1/a" + c + "b"));
    }

    [Theory]
    [MemberData(nameof(NotEcmaWhiteSpaceCases))]
    public void BackslashSInsideAClassKeepsWhatEcmaDoesNotCallWhiteSpace(char c)
    {
        Assert.True(Accepts(EndpointPattern, "https://stt.example.com/v1/a" + c + "b"));
    }

    // Der Punkt: kein Zeilenumbruch nach ECMA 262 (\n, \r, U+2028, U+2029), alles andere ja

    [Theory]
    [InlineData('\n')]
    [InlineData('\r')]
    [InlineData('\u2028')]
    [InlineData('\u2029')]
    public void TheDotDoesNotMatchEcmaLineTerminators(char c) => Assert.False(Accepts(@"^a.b$", "a" + c + "b"));

    [Theory]
    [InlineData('\u0085')]
    [InlineData('\u000B')]
    [InlineData('\u000C')]
    [InlineData('\u00A0')]
    [InlineData('\uFEFF')]
    public void TheDotMatchesAllOtherCharacters(char c) => Assert.True(Accepts(@"^a.b$", "a" + c + "b"));

    // Die Anker: $ nur am Ende, ^ nur am Anfang, ohne Mehrzeilenmodus

    [Theory]
    [InlineData("abc\n")]
    [InlineData("abc\r\n")]
    [InlineData("\nabc")]
    public void AnchorsNeverMatchAtLineBreaks(string text)
    {
        Assert.False(Accepts("^[a-z]+$", text));
        Assert.False(Accepts("^abc$", "x" + text));
    }

    // \d und \w: ASCII nach ECMA 262, Unicode in .NET

    [Theory]
    [InlineData("\u0663")] // arabisch-indische Ziffer drei
    [InlineData("\uFF13")] // vollbreite Ziffer drei
    public void BackslashDIsAsciiOnly(string digit)
    {
        Assert.False(Accepts(@"^\d$", digit));
        Assert.True(Accepts(@"^\D$", digit));
        Assert.False(Accepts(@"^[\d]$", digit));
        Assert.True(Accepts(@"^\d$", "3"));
    }

    [Theory]
    [InlineData("\u00E9")] // \u00E9
    [InlineData("\u0663")]
    public void BackslashWIsAsciiOnly(string letter)
    {
        Assert.False(Accepts(@"^\w$", letter));
        Assert.True(Accepts(@"^\W$", letter));
        Assert.False(Accepts(@"^[\w]$", letter));
        Assert.True(Accepts(@"^\w$", "_"));
    }

    [Theory]
    [InlineData(@"[\S]")]
    [InlineData(@"[\D]")]
    [InlineData(@"[\W]")]
    [InlineData(@"\b")]
    public void UntranslatedEscapesFailLoudlyInsteadOfBeingCheckedWrong(string pattern) =>
        Assert.Throws<NotSupportedException>(() => MiniSchemaValidator.EcmaPattern(pattern));

    [Fact]
    public void TranslationKeepsEscapedCharactersClassesAndUnicodeCategories()
    {
        Assert.Equal(@"^[a-z.$]+\.exe\z", MiniSchemaValidator.EcmaPattern(@"^[a-z.$]+\.exe$"));
        Assert.Equal(@"^\p{L}+([ '\-]\p{L}+)*\z", MiniSchemaValidator.EcmaPattern(@"^\p{L}+([ '\-]\p{L}+)*$"));
        Assert.True(Accepts(@"^\p{L}+$", "Ärger"));
        Assert.False(Accepts(@"^\p{L}+$", "Ärger2"));
    }

    // RegexOptions.ECMAScript von .NET hilft nicht: es bringt neue Abweichungen

    [Fact]
    public void DotNetsEcmaScriptOptionGivesTheWrongWhiteSpace()
    {
        // ECMA 262: \s passt auf U+00A0 und U+FEFF. Mit RegexOptions.ECMAScript passt es nur auf ASCII-Leerraum.
        Assert.False(Regex.IsMatch("\u00A0", @"^\s$", RegexOptions.ECMAScript));
        Assert.False(Regex.IsMatch("\uFEFF", @"^\s$", RegexOptions.ECMAScript));
        var withoutOption = Regex.IsMatch("\u00A0", @"^\s$");
        Assert.True(withoutOption);
    }

    [Fact]
    public void DotNetsEcmaScriptOptionKeepsTheDollarAndDotDifferences()
    {
        // Es behebt nur \d und \w (ASCII). „$“ passt weiter vor einem Zeilenvorschub am Schluss, und der Punkt
        // passt weiter auf \r, U+2028 und U+2029, anders als in ECMA 262.
        var dollar = Regex.IsMatch("a\n", "^a$", RegexOptions.ECMAScript);
        var dotCarriageReturn = Regex.IsMatch("\r", "^.$", RegexOptions.ECMAScript);
        var dotLineSeparator = Regex.IsMatch(char.ConvertFromUtf32(0x2028), "^.$", RegexOptions.ECMAScript);
        Assert.True(dollar);
        Assert.True(dotCarriageReturn);
        Assert.True(dotLineSeparator);
    }

    // Import und Schema: Was das Schema abweist, weist der Import ab

    private static IEnumerable<char> RelevantCharacters() =>
        Enumerable.Range(0, 0x10000)
            .Select(code => (char)code)
            .Where(c => !char.IsSurrogate(c))
            .Where(c => char.IsWhiteSpace(c) || EcmaWhiteSpace.Contains(c) || IsInvisibleCategory(CharUnicodeInfo.GetUnicodeCategory(c)));

    // Die Dialekte unterscheiden sich nur bei Leerraum, Zeilenumbruch und unsichtbaren Zeichen; Buchstaben,
    // Ziffern, Satzzeichen und Symbole sind in beiden gleich. Private Verwendung und unbelegte Stellen ebenfalls.
    private static bool IsInvisibleCategory(UnicodeCategory category) =>
        category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.SpaceSeparator
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private static IEnumerable<string> Placements(string baseText, bool startToo, char c)
    {
        yield return baseText + c;
        yield return baseText[..(baseText.Length / 2)] + c + baseText[(baseText.Length / 2)..];
        if (startToo)
        {
            yield return c + baseText;
        }
    }

    [Fact]
    public void WhatTheProfileSchemaRejectsTheImportRejectsToo()
    {
        var schema = new MiniSchemaValidator(RepositoryFiles.ReadSchema("profiles.v2.schema.json"));
        var fields = new (string Name, string Base, bool StartToo, Func<string, Action<JsonObject>> Change)[]
        {
            ("name", "Mail", true, v => p => p["name"] = v),
            ("description", "Mail", true, v => p => p["description"] = v),
            ("id", "abc", true, v => p => p["id"] = v),
            ("formatRules", "ch-standard", true, v => p => p["formatRules"] = v),
            ("language", "de-CH", true, v => p => p["language"] = v),
            ("vocabulary", "Gerber", true, v => p => p["vocabulary"] = new JsonArray(v)),
            ("programs", "olk.exe", true, v => p => p["programs"] = new JsonArray(v)),
            ("endpoint", "https://stt.example.com/v1/listen", false, v => p => p["recognition"]!["endpoint"] = v),
            ("deviceId", "device", true, v => p => p["audioSource"]!["deviceId"] = v),
            ("displayName", "Headset", true, v => p => p["audioSource"]!["displayName"] = v),
        };

        var checkedCases = 0;
        var inconsistent = new List<string>();
        foreach (var c in RelevantCharacters())
        {
            foreach (var (name, baseText, startToo, change) in fields)
            {
                foreach (var value in Placements(baseText, startToo, c))
                {
                    var json = WithProfileV2(change(value));
                    if (schema.IsValid(JsonNode.Parse(json)))
                    {
                        continue;
                    }

                    checkedCases++;
                    if (ProfileSerializer.Import(json, AllowList).Succeeded)
                    {
                        inconsistent.Add($"{name}: U+{(int)c:X4} in „{value.Replace("\n", "\\n", StringComparison.Ordinal)}“");
                    }
                }
            }
        }

        Assert.True(checkedCases > 100, "Der Abgleich hat kaum Fälle geprüft: " + checkedCases);
        Assert.Empty(inconsistent);
    }

    [Fact]
    public void WhatTheCommandSchemaRejectsTheImportRejectsToo()
    {
        var schema = new MiniSchemaValidator(RepositoryFiles.ReadSchema("commands.v1.schema.json"));
        var checkedCases = 0;
        var inconsistent = new List<string>();
        foreach (var c in RelevantCharacters())
        {
            foreach (var (name, baseText) in new[] { ("id", "abc"), ("phrase", "Komma"), ("description", "Beschreibung") })
            {
                foreach (var value in Placements(baseText, startToo: true, c))
                {
                    var root = JsonNode.Parse(BuiltInCommands.Json)!;
                    var command = root["commands"]![0]!;
                    switch (name)
                    {
                        case "id": command["id"] = value; break;
                        case "phrase": command["phrases"] = new JsonArray(value); break;
                        default: command["description"] = value; break;
                    }

                    if (schema.IsValid(root))
                    {
                        continue;
                    }

                    checkedCases++;
                    if (CommandSetSerializer.Import(root.ToJsonString()).Succeeded)
                    {
                        inconsistent.Add($"{name}: U+{(int)c:X4}");
                    }
                }
            }
        }

        Assert.True(checkedCases > 100, "Der Abgleich hat kaum Fälle geprüft: " + checkedCases);
        Assert.Empty(inconsistent);
    }
}
