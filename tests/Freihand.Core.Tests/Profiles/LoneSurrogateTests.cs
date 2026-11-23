using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Freihand.Core.Commands;
using Freihand.Core.Profiles;
using Freihand.Core.Quality;
using Freihand.Core.Tests.Recovery;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Einzelne Ersatzzeichen in JSON-Text (Codex-Prüfung PR #25, Z01 und Z02): Jeder Eingang für JSON-Text, also Profile,
/// Befehle, Messbericht und Sicherungsdatei, meldet sie als festgelegten Fehler und wirft keine fremde Ausnahme.
/// Profile und Befehle: ein Importfehler am Pfad <c>$</c>. Messbericht: <see cref="JsonException"/>. Sicherungsdatei:
/// beschädigter Entwurf. Gilt für einzelne hohe und tiefe Hälften, ein umgekehrtes Paar, roh und als Escape, in Werten,
/// Schlüsseln, Rahmenfeldern und Listen. Ein gültiges Paar bleibt erlaubt.
/// </summary>
public class LoneSurrogateTests : RecoveryTestBase
{
    private const string Placeholder = "PLACEHOLDERTEXT";

    private static readonly string Backslash = ((char)92).ToString();

    private static string Escaped(string hex) => Backslash + "u" + hex;

    /// <summary>JSON-Text eines Zeichens oder Paars, so wie er im Dokument steht (rohe Zeichen oder Escapes).</summary>
    private static string Form(string name) => name switch
    {
        "high-escaped" => Escaped("d800"),
        "high-escaped-upper" => Escaped("DBFF"),
        "low-escaped" => Escaped("dc00"),
        "low-escaped-upper" => Escaped("DFFF"),
        "reversed-escaped" => Escaped("dc00") + Escaped("d800"),
        "high-then-letter-escaped" => Escaped("d800") + "x",
        "high-then-escape-escaped" => Escaped("d800") + Backslash + "n",
        "high-then-other-escape" => Escaped("d800") + Escaped("0041"),
        "two-highs-escaped" => Escaped("d800") + Escaped("d800"),
        "high-raw" => ((char)0xD800).ToString(),
        "low-raw" => ((char)0xDC00).ToString(),
        "reversed-raw" => (char)0xDC00 + ((char)0xD800).ToString(),
        "high-raw-low-escaped" => (char)0xD800 + Escaped("dc00"),
        "high-escaped-low-raw" => Escaped("d800") + (char)0xDC00,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public static TheoryData<string> LoneForms =>
    [
        "high-escaped", "high-escaped-upper", "low-escaped", "low-escaped-upper", "reversed-escaped",
        "high-then-letter-escaped", "high-then-escape-escaped", "high-then-other-escape", "two-highs-escaped",
        "high-raw", "low-raw", "reversed-raw",
    ];

    /// <summary>Zusammengesetzt aus rohem und escaptem Teil: für .NET ein gültiges Paar, für UTF-8 aber nicht lesbar.</summary>
    public static TheoryData<string> MixedForms => ["high-raw-low-escaped", "high-escaped-low-raw"];

    public static TheoryData<string> Places => ["value", "key", "kind", "list", "endpoint", "nested-key"];

    private static string ProfileDocument(string place, string fragment)
    {
        var root = JsonNode.Parse(FullDocumentV2)!;
        var profile = root["profiles"]![0]!;
        switch (place)
        {
            case "value": profile["name"] = Placeholder; break;
            case "key": profile[Placeholder] = "x"; break;
            case "kind": root["kind"] = Placeholder; break;
            case "list": profile["vocabulary"] = new JsonArray("Gerber", Placeholder); break;
            case "endpoint": profile["recognition"]!["endpoint"] = "https://stt.example.com/v1/" + Placeholder; break;
            case "nested-key": profile["transform"]![Placeholder] = "x"; break;
            default: throw new ArgumentOutOfRangeException(nameof(place));
        }

        return root.ToJsonString().Replace(Placeholder, fragment, StringComparison.Ordinal);
    }

    private static string CommandDocument(string place, string fragment)
    {
        var root = JsonNode.Parse(BuiltInCommands.Json)!;
        var command = root["commands"]![0]!;
        switch (place)
        {
            case "value": command["description"] = Placeholder; break;
            case "key": command[Placeholder] = "x"; break;
            case "kind": root["kind"] = Placeholder; break;
            case "list": command["phrases"] = new JsonArray("Komma", Placeholder); break;
            case "endpoint": command["description"] = "https://x.example.com/" + Placeholder; break;
            case "nested-key": root[Placeholder] = "x"; break;
            default: throw new ArgumentOutOfRangeException(nameof(place));
        }

        return root.ToJsonString().Replace(Placeholder, fragment, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LoneForms))]
    public void ProfileImportReportsALoneSurrogateAtTheDocumentPathEverywhere(string form)
    {
        foreach (var place in new[] { "value", "key", "kind", "list", "endpoint", "nested-key" })
        {
            var result = ProfileSerializer.Import(ProfileDocument(place, Form(form)), AllowList);

            Assert.False(result.Succeeded, place);
            var error = Assert.Single(result.Errors);
            Assert.Equal("$", error.Path);
            Assert.Contains("Unicode", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(LoneForms))]
    public void CommandImportReportsALoneSurrogateAtTheDocumentPathEverywhere(string form)
    {
        foreach (var place in new[] { "value", "key", "kind", "list", "nested-key" })
        {
            var result = CommandSetSerializer.Import(CommandDocument(place, Form(form)));

            Assert.False(result.Succeeded, place);
            var error = Assert.Single(result.Errors);
            Assert.Equal("$", error.Path);
            Assert.Contains("Unicode", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(MixedForms))]
    public void AMixedRawAndEscapedHalfIsAnImportErrorNotAnException(string form)
    {
        var profile = ProfileSerializer.Import(ProfileDocument("value", Form(form)), AllowList);
        var commands = CommandSetSerializer.Import(CommandDocument("value", Form(form)));

        Assert.False(profile.Succeeded);
        Assert.Equal("$", Assert.Single(profile.Errors).Path);
        Assert.False(commands.Succeeded);
        Assert.Equal("$", Assert.Single(commands.Errors).Path);
    }

    [Fact]
    public void TheHeaderFieldsOfCodexThirdFollowUpAreImportErrors()
    {
        foreach (var half in new[] { "d800", "dc00" })
        {
            var json = "{\"schemaVersion\":2,\"kind\":\"" + Escaped(half) + "\"}";

            Assert.Equal("$", Assert.Single(ProfileSerializer.Import(json, AllowList).Errors).Path);
            Assert.Equal("$", Assert.Single(CommandSetSerializer.Import(json.Replace("2", "1", StringComparison.Ordinal)).Errors).Path);
        }
    }

    // Gültige Paare bleiben erlaubt

    [Theory]
    [InlineData("d83d", "de00")]
    [InlineData("D83D", "DE00")]
    [InlineData("d801", "dc00")]
    [InlineData("dbff", "dfff")]
    public void AValidEscapedPairIsAcceptedInValuesAndKeys(string high, string low)
    {
        var pair = Escaped(high) + Escaped(low);
        var expected = char.ConvertFromUtf32(char.ConvertToUtf32((char)Convert.ToInt32(high, 16), (char)Convert.ToInt32(low, 16)));

        var name = ProfileSerializer.Import(ProfileDocument("value", "Mail " + pair), AllowList);
        var list = ProfileSerializer.Import(ProfileDocument("list", pair), AllowList);

        Assert.True(name.Succeeded, string.Join("\n", name.Errors));
        Assert.Equal("Mail " + expected, Assert.Single(name.Value).Name);
        Assert.True(list.Succeeded, string.Join("\n", list.Errors));
        Assert.Equal(expected, Assert.Single(list.Value).Vocabulary[1]);

        // Als Schlüssel ist das Paar ein unbekanntes Feld, aber kein Unicode-Fehler.
        var key = ProfileSerializer.Import(ProfileDocument("key", pair), AllowList);
        Assert.False(key.Succeeded);
        Assert.DoesNotContain(key.Errors, error => error.Message.Contains("Unicode", StringComparison.Ordinal));
    }

    [Fact]
    public void ARawPairAndAnEscapedBackslashBeforeTheLettersAreAccepted()
    {
        var raw = ProfileSerializer.Import(ProfileDocument("value", "Mail " + char.ConvertFromUtf32(0x1F600)), AllowList);
        // Ein maskierter Backslash vor „ud800“ ist der Text „\ud800“, kein Ersatzzeichen.
        var literal = ProfileSerializer.Import(ProfileDocument("value", "Mail " + Backslash + Backslash + "ud800"), AllowList);

        Assert.True(raw.Succeeded, string.Join("\n", raw.Errors));
        Assert.True(literal.Succeeded, string.Join("\n", literal.Errors));
        Assert.Equal("Mail " + Backslash + "ud800", Assert.Single(literal.Value).Name);
    }

    [Fact]
    public void AnEscapeOutsideAStringIsNoSurrogateButInvalidJson()
    {
        var result = ProfileSerializer.Import("{ " + Escaped("d800") + " }", AllowList);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(result.Errors, error => error.Message.Contains("Unicode", StringComparison.Ordinal));
    }

    // Messbericht

    [Theory]
    [MemberData(nameof(LoneForms))]
    public void TheQualityReportReportsALoneSurrogateAsAJsonException(string form)
    {
        Assert.Throws<JsonException>(() => QualityReportJson.Deserialize("{\"schema\":\"" + Form(form) + "\"}"));
        Assert.Throws<JsonException>(() => QualityReportJson.Deserialize("{\"" + Form(form) + "\":1}"));
    }

    // Sicherungsdatei: ein beschädigter Entwurf, keine Ausnahme aus dem Lesen

    /// <summary>
    /// Schreibt die Nutzlast als Sicherungsdatei und liest die Ablage. Abschnitte ohne <c>id</c> erhalten eine, damit die
    /// Nutzlast ohne das geprüfte Zeichen vollständig und lesbar ist (Codex-Nachprüfung 4, W02). Der Text wird ohne
    /// ersetzenden Encoder zu Bytes: ein rohes einzelnes Ersatzzeichen wird zur ungültigen UTF-8-Folge, nicht zu U+FFFD.
    /// </summary>
    private string ScanWith(string payloadJson)
    {
        var id = Guid.NewGuid();
        var complete = payloadJson.Replace("DRAFTID", id.ToString(), StringComparison.Ordinal)
            .Replace("{\"text\":", "{\"id\":\"" + Guid.NewGuid() + "\",\"text\":", StringComparison.Ordinal);
        var protectedPayload = Protector.Protect(EncodeWithoutReplacement(complete));
        var file = new byte[9 + protectedPayload.Length + 32];
        "FRHD"u8.CopyTo(file);
        file[4] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(5, 4), protectedPayload.Length);
        protectedPayload.CopyTo(file.AsSpan(9));
        SHA256.HashData(protectedPayload, file.AsSpan(9 + protectedPayload.Length));
        Files.Files.Clear();
        Files.Files[id.ToString("N") + ".draft"] = (file, Time.GetUtcNow());

        var scan = Open().Scan();
        return string.Join(",", scan.Issues.Select(issue => issue.Kind));
    }

    /// <summary>UTF-8 für gültigen Text; ein einzelnes Ersatzzeichen als die dreibytige Folge, die UTF-8 nicht erlaubt.</summary>
    private static byte[] EncodeWithoutReplacement(string text)
    {
        var bytes = new List<byte>();
        for (var index = 0; index < text.Length; index++)
        {
            var unit = text[index];
            if (char.IsHighSurrogate(unit) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(text.Substring(index, 2)));
                index++;
            }
            else if (char.IsSurrogate(unit))
            {
                bytes.AddRange([(byte)(0xE0 | (unit >> 12)), (byte)(0x80 | ((unit >> 6) & 0x3F)), (byte)(0x80 | (unit & 0x3F))]);
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(unit.ToString()));
            }
        }

        return [.. bytes];
    }

    [Theory]
    [MemberData(nameof(LoneForms))]
    public void ADraftWithALoneSurrogateIsDamagedNotAnException(string form)
    {
        const string times = "\"createdAt\":\"2026-10-29T10:00:00+00:00\",\"updatedAt\":\"2026-10-29T10:00:00+00:00\",\"handedOverAt\":null";
        string Draft(string text) => "{\"id\":\"DRAFTID\"," + times + ",\"segments\":[{\"text\":\"" + text + "\"}]}";

        // Kontrollen: dieselbe Nutzlast mit ASCII und mit einem gültigen Paar ist lesbar.
        Assert.Equal(string.Empty, ScanWith(Draft("synthetic")));
        Assert.Equal(string.Empty, ScanWith(Draft(Escaped("d83d") + Escaped("de00"))));
        Assert.Equal(string.Empty, ScanWith(Draft(char.ConvertFromUtf32(0x1F600))));

        Assert.Contains("Damaged", ScanWith(Draft(Form(form))), StringComparison.Ordinal);

        // Ein Schlüssel in der Sicherungsdatei ist stets ein unbekanntes Feld und damit auch mit ASCII beschädigt;
        // dort gibt es keine Kontrolle, die das Zeichen vom Schema trennt. Die Matrix (Review/JsonInputUnicodeMatrixTests)
        // führt darum nur Werte und Listeneinträge.
    }
}
