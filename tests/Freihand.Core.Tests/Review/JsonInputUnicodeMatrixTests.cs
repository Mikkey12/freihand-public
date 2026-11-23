using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Freihand.Core.Commands;
using Freihand.Core.Profiles;
using Freihand.Core.Quality;
using Freihand.Core.Recovery;
using Freihand.Core.Tests.Recovery;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Review;

/// <summary>
/// Vollständige Matrix für einzelne Ersatzzeichen in JSON-Text (Codex-Prüfung PR #25, Nachprüfung 4, W01 und W02):
/// Kreuzprodukt aus Eingang, Stelle, Form und Erwartung, damit nicht mehr Einzelfall um Einzelfall nachgebessert wird.
/// <list type="bullet">
/// <item><b>Eingang:</b> Profile Version 1, Profile Version 2, Befehle, Messbericht (<see cref="QualityReportJson"/>) und
/// Entwurfsdatei (<c>DraftFile</c>). Ob es weitere JSON-Eingänge im Quelltext gibt, prüft
/// <see cref="NoJsonInputOutsideTheMatrixExists"/>.</item>
/// <item><b>Stelle:</b> bekannter Stringwert; auf jeder Ebene des Dokuments (aus dem Dokument selbst ermittelt, nicht von
/// Hand aufgezählt) der Wert eines unbekannten Felds, ein unbekannter Schlüssel, ein Listeneintrag und ein verschachteltes
/// Objekt, dort Wert und Schlüssel.</item>
/// <item><b>Form:</b> einzelne hohe Hälfte, einzelne tiefe Hälfte, umgekehrtes Paar, jeweils als Escape und roh. Bei der
/// Entwurfsdatei sind „roh“ echte ungültige UTF-8-Bytes (<c>ED A0 80</c> und Verwandte), die ohne ersetzenden Encoder
/// in die Datei kommen.</item>
/// <item><b>Erwartung:</b> Profile und Befehle ein Importfehler am Pfad <c>$</c>, Messbericht eine
/// <see cref="JsonException"/>, Entwurfsdatei ein beschädigter Entwurf; nie eine andere Ausnahme.</item>
/// </list>
/// Zu jeder Fixture gehören Kontrollen (Befund W02): dieselbe Fixture mit einem gültigen ASCII-Wert, mit einem gültigen
/// Paar als Escape und mit einem gültigen rohen Paar wird angenommen. Wo das Schema die Stelle ohnehin abweist (unbekanntes
/// Feld in Profil und Befehl, Rahmenfeld <c>kind</c>, Aufzählungen), darf die Kontrolle abgewiesen werden, aber nie wegen
/// eines Unicode-Fehlers; die Ablehnung der Fixture liegt dann am Zeichen, weil nur sie die Unicode-Meldung erhält.
/// In der Entwurfsdatei sind die Abschnittstexte und alle bekannten Strukturstellen erfasst (bekannte Schlüssel, Kennungen,
/// Zeitfelder), dazu gemischte Formen aus ungültigen Bytes und Escape. Nur erfundene Schlüssel fehlen:
/// <c>UnmappedMemberHandling.Disallow</c> macht jedes unbekannte Feld zum beschädigten Entwurf, auch mit ASCII; dort gäbe
/// es keine Kontrolle, die das Zeichen vom Schema trennt.
/// Bekannte Stellen in Profilen, Befehlen und Messbericht sind eine Auswahl je Art (Freitext, Kennung, Aufzählung, Muster,
/// Adresse), keine Inventur jedes Felds; alle gehen durch dieselbe Vorprüfung des ganzen Texts.
/// </summary>
public sealed class JsonInputUnicodeMatrixTests : RecoveryTestBase
{
    private const string Placeholder = "PLACEHOLDERTEXT";

    private const string ProfileV1 = "profile-v1";
    private const string ProfileV2 = "profile-v2";
    private const string Commands = "commands";
    private const string QualityInput = "quality-report";

    private static readonly string[] JsonInputs = [ProfileV1, ProfileV2, Commands, QualityInput];

    private static readonly string[] JsonForms =
        ["high-escaped", "low-escaped", "reversed-escaped", "high-raw", "low-raw", "reversed-raw",
         "high-escaped-upper", "low-escaped-upper", "mixed-raw-high", "mixed-raw-low"];

    private static readonly string[] DraftForms =
        ["high-escaped", "low-escaped", "reversed-escaped", "high-bytes", "low-bytes", "reversed-bytes",
         "high-escaped-upper", "low-escaped-upper", "mixed-bytes-high", "mixed-bytes-low"];

    private static readonly string[] UnknownShapes =
        ["unknown-value", "unknown-key", "unknown-list", "unknown-nested", "unknown-nested-key"];

    /// <summary>Bekannte Stellen, an denen das Schema nur bestimmte Werte annimmt (Kennung, Aufzählung, Muster, Adresse).</summary>
    private static readonly string[] SchemaBoundKnownPlaces =
        ["known:kind", "known:endpoint", "known:program", "known:style"];

    /// <summary>Bekannte Stellen, die ASCII annehmen, aber kein Emoji (Wendungen bestehen nur aus Buchstaben).</summary>
    private static readonly string[] AsciiOnlyKnownPlaces = ["known:phrase"];

    private static readonly string Backslash = ((char)92).ToString();

    private const string Pair = "\U0001F600";

    private static string Escaped(string hex) => Backslash + "u" + hex;

    /// <summary>Ein Zeichen oder Paar, so wie es im JSON-Text steht; hier mit Escapes und rohen Zeichen.</summary>
    private static string TextOf(string form) => form switch
    {
        "high-escaped" => Escaped("d800"),
        "low-escaped" => Escaped("dc00"),
        "reversed-escaped" => Escaped("dc00") + Escaped("d800"),
        "high-raw" => new string([(char)0xD800]),
        "low-raw" => new string([(char)0xDC00]),
        "reversed-raw" => new string([(char)0xDC00, (char)0xD800]),
        // Grossgeschriebene Escapes sind die übliche Form
        "high-escaped-upper" => Escaped("D800"),
        "low-escaped-upper" => Escaped("DC00"),
        // Gemischt: eine Hälfte roh, die andere als Escape. Das ist kein gültiger JSON-Text (rohe Zeichen müssen für
        // sich gültiges Unicode sein); abgewiesen wird es beim Umwandeln des Texts, nicht in JsonUnicode
        "mixed-raw-high" => new string([(char)0xD83D]) + Escaped("de00"),
        "mixed-raw-low" => Escaped("d83d") + new string([(char)0xDE00]),
        _ => throw new ArgumentOutOfRangeException(nameof(form)),
    };

    /// <summary>Bytes der Entwurfsdatei: Escapes als ASCII, „roh“ als echte ungültige UTF-8-Folge (verallgemeinertes UTF-8).</summary>
    private static byte[] BytesOf(string form) => form switch
    {
        "high-bytes" => [0xED, 0xA0, 0x80],
        "low-bytes" => [0xED, 0xB0, 0x80],
        "reversed-bytes" => [0xED, 0xB0, 0x80, 0xED, 0xA0, 0x80],

        // Gemischt: eine Hälfte als ungültige UTF-8-Bytes, die andere als Escape
        "mixed-bytes-high" => [0xED, 0xA0, 0x80, .. Encoding.ASCII.GetBytes(Escaped("dc00"))],
        "mixed-bytes-low" => [.. Encoding.ASCII.GetBytes(Escaped("d800")), 0xED, 0xB0, 0x80],
        _ => Encoding.ASCII.GetBytes(TextOf(form)),
    };

    /// <summary>Kontrollen: Name, JSON-Text, zugehöriger gelesener Text.</summary>
    private static readonly (string Name, string Text, string Decoded)[] Controls =
    [
        ("ascii", "ctrl", "ctrl"),
        ("pair-escaped", Escaped("d83d") + Escaped("de00"), Pair),
        ("pair-escaped-upper", Escaped("D83D") + Escaped("DE00"), Pair),
        ("pair-raw", Pair, Pair),
    ];

    // Fixtures für Profile, Befehle und Messbericht

    private static JsonNode BaseDocument(string input) => input switch
    {
        ProfileV1 => JsonNode.Parse(FullDocument)!,
        ProfileV2 => JsonNode.Parse(ExtendedDocumentV2)!,
        Commands => JsonNode.Parse(BuiltInCommands.Json)!,
        QualityInput => JsonNode.Parse(QualityReportJson.Serialize(QualityEvaluator.Evaluate(Run())))!,
        _ => throw new ArgumentOutOfRangeException(nameof(input)),
    };

    /// <summary>Ein Messlauf mit allen Listen gefüllt, damit jede Ebene des Berichts vorkommt.</summary>
    private static QualityRun Run() => new(
        new MeasurementInfo(
            "whisper.cpp", "large-v3-turbo", "Nachstellung", "de-CH", TextStage.Raw,
            ReferenceSet: "referenzsatz-v1", MeasuredAt: new DateTimeOffset(2026, 10, 27, 14, 0, 0, TimeSpan.FromHours(1)),
            Notes: "ohne Last"),
        TimeSpan.FromMinutes(20),
        new QualitySettings(TimeSpan.FromMilliseconds(500)))
    {
        Compute = [.. Enumerable.Range(0, 20).Select(i => new ComputeSample(TimeSpan.FromMinutes(i), TimeSpan.FromSeconds(i < 10 ? 18 : 48)))],
        Utterances =
        [
            new("u1", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(14), TimeSpan.FromSeconds(10.8), TimeSpan.FromSeconds(15.5)),
            new("u2", TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(3), null, null),
        ],
        Sentences =
        [
            new("s1", "Bitte die Rechnung an Frau Gisler.", "Bitte die an Frau Gisler.", [new(TermCategory.Name, "Gisler")]),
            new("s2", "Total 1'250, also 3,5 Franken.", "Total 1250 also 3,5 Franken.", [new(TermCategory.Number, "1'250")]),
        ],
        Commands = [new("c1", "rueckgaengig", "rueckgaengig"), new("c2", null, "abbrechen")],
    };

    /// <summary>Alle Objektebenen eines Dokuments, Listen als <c>[]</c> zusammengefasst, jede Ebene einmal.</summary>
    private static IEnumerable<string> ObjectPaths(JsonNode? node, string path, HashSet<string> seen)
    {
        switch (node)
        {
            case JsonObject obj:
                if (seen.Add(path))
                {
                    yield return path;
                }

                foreach (var (name, child) in obj)
                {
                    foreach (var inner in ObjectPaths(child, path + "." + name, seen))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    foreach (var inner in ObjectPaths(child, path + "[]", seen))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }

    private static JsonObject Navigate(JsonNode root, string path)
    {
        var node = root;
        foreach (Match step in Regex.Matches(path[1..], @"\.([^.\[\]]+)|\[\]"))
        {
            node = step.Groups[1].Success ? node![step.Groups[1].Value] : node!.AsArray()[0];
        }

        return node!.AsObject();
    }

    private static string[] KnownPlaces(string input) => input switch
    {
        ProfileV1 => ["known:name", "known:description", "known:display-name", "known:kind", "known:endpoint"],
        ProfileV2 =>
        [
            "known:name", "known:description", "known:display-name", "known:kind", "known:endpoint",
            "known:vocabulary-entry", "known:program", "known:style",
        ],
        Commands => ["known:description", "known:phrase", "known:kind"],
        QualityInput => ["known:provider", "known:notes", "known:sentence-id"],
        _ => throw new ArgumentOutOfRangeException(nameof(input)),
    };

    private static IEnumerable<string> PlacesOf(string input)
    {
        foreach (var place in KnownPlaces(input))
        {
            yield return place;
        }

        foreach (var path in ObjectPaths(BaseDocument(input), "$", []))
        {
            foreach (var shape in UnknownShapes)
            {
                yield return shape + "@" + path;
            }
        }
    }

    /// <summary>Ob die Stelle mit einem gewöhnlichen Wert (ASCII oder gültigem Paar) angenommen wird.</summary>
    private static bool Accepts(string input, string place, string control) =>
        place.StartsWith("known:", StringComparison.Ordinal)
            ? !SchemaBoundKnownPlaces.Contains(place) && (control == "ascii" || !AsciiOnlyKnownPlaces.Contains(place))
            : input == QualityInput; // Der Messbericht überspringt unbekannte Felder; Profile, Befehle und Entwurf weisen sie ab.

    private static string Build(string input, string place, string text)
    {
        var root = BaseDocument(input);
        if (place.StartsWith("known:", StringComparison.Ordinal))
        {
            InsertKnown(root, place["known:".Length..]);
        }
        else
        {
            var at = place.IndexOf('@', StringComparison.Ordinal);
            var target = Navigate(root, place[(at + 1)..]);
            switch (place[..at])
            {
                case "unknown-value": target["extra"] = Placeholder; break;
                case "unknown-key": target[Placeholder] = "x"; break;
                case "unknown-list": target["extra"] = new JsonArray("a", Placeholder); break;
                case "unknown-nested": target["extra"] = new JsonObject { ["inner"] = new JsonObject { ["deep"] = Placeholder } }; break;
                case "unknown-nested-key": target["extra"] = new JsonObject { ["inner"] = new JsonObject { [Placeholder] = 1 } }; break;
                default: throw new ArgumentOutOfRangeException(nameof(place));
            }
        }

        var json = root.ToJsonString();
        Assert.Contains(Placeholder, json, StringComparison.Ordinal);
        return json.Replace(Placeholder, text, StringComparison.Ordinal);
    }

    private static void InsertKnown(JsonNode root, string name)
    {
        var profile = root["profiles"]?[0];
        var command = root["commands"]?[0];
        switch (name)
        {
            case "kind": root["kind"] = Placeholder; break;
            case "name": profile!["name"] = Placeholder; break;
            case "description" when profile is not null: profile["description"] = Placeholder; break;
            case "description": command!["description"] = Placeholder; break;
            case "display-name": profile!["audioSource"]!["displayName"] = Placeholder; break;
            case "endpoint": profile!["recognition"]!["endpoint"] = "https://stt.example.com/v1/" + Placeholder; break;
            case "vocabulary-entry": profile!["vocabulary"] = new JsonArray("Gerber", Placeholder); break;
            case "program": profile!["programs"] = new JsonArray(Placeholder); break;
            case "style": profile!["transform"]!["style"] = Placeholder; break;
            case "phrase": command!["phrases"] = new JsonArray("Komma", Placeholder); break;
            case "provider": root["measurement"]!["provider"] = Placeholder; break;
            case "notes": root["measurement"]!["notes"] = Placeholder; break;
            case "sentence-id": root["wordErrors"]!["sentences"]![1]!["id"] = Placeholder; break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    private static bool HasUnicodeError(IReadOnlyList<ImportError> errors) =>
        errors.Any(error => error.Message.Contains("Unicode", StringComparison.Ordinal));

    private static void AssertRejectedAsPromised(string input, string json, string context)
    {
        switch (input)
        {
            case ProfileV1 or ProfileV2:
                var profiles = ProfileSerializer.Import(json, AllowList);
                Assert.False(profiles.Succeeded, context);
                var profileError = Assert.Single(profiles.Errors);
                Assert.Equal("$", profileError.Path);
                Assert.Contains("Unicode", profileError.Message, StringComparison.Ordinal);
                break;
            case Commands:
                var commands = CommandSetSerializer.Import(json);
                Assert.False(commands.Succeeded, context);
                var commandError = Assert.Single(commands.Errors);
                Assert.Equal("$", commandError.Path);
                Assert.Contains("Unicode", commandError.Message, StringComparison.Ordinal);
                break;
            default:
                // Genau JsonException, keine abgeleitete und keine andere Ausnahme.
                Assert.Throws<JsonException>(() => QualityReportJson.Deserialize(json));
                break;
        }
    }

    private static void AssertControlAccepted(string input, string json, bool accepts, string context)
    {
        switch (input)
        {
            case ProfileV1 or ProfileV2:
                var profiles = ProfileSerializer.Import(json, AllowList);
                Assert.False(HasUnicodeError(profiles.Errors), context);
                if (accepts)
                {
                    Assert.True(profiles.Succeeded, context + ": " + string.Join("; ", profiles.Errors));
                }

                break;
            case Commands:
                var commands = CommandSetSerializer.Import(json);
                Assert.False(HasUnicodeError(commands.Errors), context);
                if (accepts)
                {
                    Assert.True(commands.Succeeded, context + ": " + string.Join("; ", commands.Errors));
                }

                break;
            default:
                Assert.Equal(QualityReport.CurrentSchema, QualityReportJson.Deserialize(json).Schema);
                break;
        }
    }

    public static TheoryData<string, string, string> JsonCells
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var input in JsonInputs)
            {
                foreach (var place in PlacesOf(input))
                {
                    foreach (var form in JsonForms)
                    {
                        data.Add(input, place, form);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(JsonCells))]
    public void AJsonInputRejectsALoneSurrogateAsPromisedAndAcceptsTheSameFixtureWithAValidValue(string input, string place, string form)
    {
        foreach (var (name, text, _) in Controls)
        {
            AssertControlAccepted(input, Build(input, place, text), Accepts(input, place, name), $"Kontrolle {name}: {input} {place}");
        }

        AssertRejectedAsPromised(input, Build(input, place, TextOf(form)), $"{input} {place} {form}");
    }

    // Entwurfsdatei

    private static readonly string[] DraftPlaces = ["segment-text", "second-segment-text"];

    public static TheoryData<string, string> DraftCells
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var place in DraftPlaces)
            {
                foreach (var form in DraftForms)
                {
                    data.Add(place, form);
                }
            }

            return data;
        }
    }

    /// <summary>Vollständig gültige Nutzlast, der Text der gewählten Stelle ist der Platzhalter.</summary>
    private (Guid Id, string Json) DraftTemplate(string place)
    {
        var id = Guid.NewGuid();
        object[] segments = place == "segment-text"
            ? [new { id = Guid.NewGuid(), text = Placeholder }]
            : [new { id = Guid.NewGuid(), text = "erster" }, new { id = Guid.NewGuid(), text = Placeholder }];
        var json = JsonSerializer.Serialize(new
        {
            id,
            createdAt = Time.GetUtcNow(),
            updatedAt = Time.GetUtcNow(),
            handedOverAt = (DateTimeOffset?)null,
            segments,
        });
        return (id, json);
    }

    /// <summary>Setzt die Bytes in die Nutzlast, ohne sie über einen Encoder zu führen, der ungültige Folgen ersetzt.</summary>
    private static byte[] DraftBytes(string json, byte[] fragment)
    {
        var parts = json.Split(Placeholder, StringSplitOptions.None);
        Assert.Equal(2, parts.Length);
        return [.. Encoding.UTF8.GetBytes(parts[0]), .. fragment, .. Encoding.UTF8.GetBytes(parts[1])];
    }

    private RecoveryScan ScanDraft(byte[] json, Guid id)
    {
        var payload = Protector.Protect(json);
        var file = new byte[9 + payload.Length + 32];
        "FRHD"u8.CopyTo(file);
        file[4] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(5, 4), payload.Length);
        payload.CopyTo(file.AsSpan(9));
        SHA256.HashData(payload, file.AsSpan(9 + payload.Length));
        Files.Files.Clear();
        Files.Files[id.ToString("N") + ".draft"] = (file, Time.GetUtcNow());
        using var store = Open();
        return store.Scan();
    }

    [Theory]
    [MemberData(nameof(DraftCells))]
    public void ADraftFileRejectsALoneSurrogateAsDamagedAndAcceptsTheSameFixtureWithAValidValue(string place, string form)
    {
        var (id, json) = DraftTemplate(place);

        // Kontrollen: dieselbe Nutzlast mit ASCII, mit einem Paar als Escape und mit einem Paar als echte UTF-8-Bytes.
        foreach (var (name, bytes) in new[]
        {
            ("ascii", Encoding.ASCII.GetBytes("ctrl")),
            ("pair-escaped", Encoding.ASCII.GetBytes(Escaped("d83d") + Escaped("de00"))),
            ("pair-bytes", Encoding.UTF8.GetBytes(Pair)),
        })
        {
            var valid = ScanDraft(DraftBytes(json, bytes), id);
            Assert.True(valid.Issues.Count == 0, $"Kontrolle {name}: {string.Join("; ", valid.Issues.Select(issue => issue.Kind))}");
            var draft = Assert.Single(valid.Drafts);
            var expected = name == "ascii" ? "ctrl" : Pair;
            Assert.Equal(expected, draft.Segments[^1].Text);
        }

        var result = ScanDraft(DraftBytes(json, BytesOf(form)), id);
        Assert.Empty(result.Drafts);
        Assert.Contains(result.Issues, issue => issue.Kind == RecoveryIssueKind.Damaged);
    }

    /// <summary>
    /// Bekannte Strukturstellen der Entwurfsdatei mit gültigem Ausgangswert (Codex-Nachprüfung 5 zu PR #25, Y02): bekannte
    /// Schlüssel, Kennungen und Zeitfelder. Kontrolle ist jeweils der ursprüngliche gültige Wert; eine Ersatzhälfte an
    /// dieser Stelle macht den Entwurf beschädigt. Erfundene Schlüssel bleiben ausgenommen: <c>UnmappedMemberHandling</c>
    /// weist jeden unbekannten Schlüssel ab, auch mit ASCII, sodass dort keine trennende Kontrolle möglich ist.
    /// </summary>
    private static readonly string[] StructuralDraftPlaces =
        ["root-key", "segment-key", "root-id", "segment-id", "createdAt", "updatedAt", "handedOverAt"];

    public static TheoryData<string, string> StructuralDraftCells
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var place in StructuralDraftPlaces)
            {
                foreach (var form in DraftForms)
                {
                    data.Add(place, form);
                }
            }

            return data;
        }
    }

    /// <summary>Gültige Nutzlast; die gewählte Stelle ist durch den Platzhalter ersetzt, ihr gültiger Wert ist die Kontrolle.</summary>
    private (Guid Id, string Json, string Control) StructuralDraftTemplate(string place)
    {
        var id = Guid.NewGuid();
        var segment = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new
        {
            id,
            createdAt = Time.GetUtcNow(),
            updatedAt = Time.GetUtcNow(),
            handedOverAt = (DateTimeOffset?)Time.GetUtcNow(),
            segments = new[] { new { id = segment, text = "erster" } },
        });
        string Value(string name)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty(name).GetString()!;
        }

        var (old, control) = place switch
        {
            "root-key" => ("\"segments\":", "segments"),
            "segment-key" => ("\"text\":", "text"),
            "root-id" => ($"\"id\":\"{id}\"", id.ToString()),
            "segment-id" => ($"\"id\":\"{segment}\"", segment.ToString()),
            _ => ($"\"{place}\":\"{Value(place)}\"", Value(place)),
        };
        Assert.Equal(1, json.Split(old).Length - 1);
        var template = place.EndsWith("-key", StringComparison.Ordinal)
            ? json.Replace(old, $"\"{Placeholder}\":", StringComparison.Ordinal)
            : json.Replace(old, old.Replace(control, Placeholder, StringComparison.Ordinal), StringComparison.Ordinal);
        return (id, template, control);
    }

    [Theory]
    [MemberData(nameof(StructuralDraftCells))]
    public void ADraftFileRejectsALoneSurrogateInAStructuralPlaceAndAcceptsItsValidValue(string place, string form)
    {
        var (id, json, control) = StructuralDraftTemplate(place);

        var valid = ScanDraft(DraftBytes(json, Encoding.ASCII.GetBytes(control)), id);
        Assert.True(valid.Issues.Count == 0, $"Kontrolle {place}: {string.Join("; ", valid.Issues.Select(issue => issue.Kind))}");
        Assert.Single(valid.Drafts);

        var result = ScanDraft(DraftBytes(json, BytesOf(form)), id);
        Assert.Empty(result.Drafts);
        Assert.Contains(result.Issues, issue => issue.Kind == RecoveryIssueKind.Damaged);
    }

    // Gibt es einen weiteren JSON-Eingang?

    /// <summary>
    /// Leseformen für JSON, je als einzelne Aufrufstelle gezählt (Codex-Nachprüfung 5 zu PR #25, Y01): ein neuer Leser
    /// fällt auch in einer schon bekannten Datei auf. Gezählt werden typisierte Parse-Aufrufe, jedes
    /// <c>.Deserialize…</c> (statisch oder auf einem Element), <c>Utf8JsonReader</c>, die HTTP-Leser und Newtonsoft.
    /// </summary>
    private static readonly Regex JsonReader = new(
        @"\b(?:JsonDocument|JsonNode|JsonElement|JsonObject|JsonArray|JsonValue)\s*\.\s*(?:Try)?Parse\w*\b"
        + @"|\.\s*Deserialize\w*\b"
        + @"|\bUtf8JsonReader\b"
        + @"|\b(?:ReadFromJson|GetFromJson)\w*\b"
        + @"|\bJsonConvert\s*\.\s*Deserialize\w*\b|\bJ(?:Object|Array|Token)\s*\.\s*Parse\b");

    /// <summary>
    /// Verbotene Schreibweisen, die den Wächter umgehen könnten (Codex-Nachprüfung 6 zu PR #25, 6-25-01): statische Imports
    /// und Aliase von <c>System.Text.Json</c>, jede Form von Newtonsoft, auch als <c>global using</c> und mit
    /// <c>global::</c>. Ein gewöhnliches <c>using System.Text.Json;</c> bleibt erlaubt.
    /// </summary>
    private static readonly Regex HiddenReader = new(
        @"^\s*(?:global\s+)?using\s+(?:static\s+(?:global::)?System\s*\.\s*Text\s*\.\s*Json\b"
        + @"|\w+\s*=\s*(?:global::)?System\s*\.\s*Text\s*\.\s*Json\b"
        + @"|(?:static\s+)?(?:\w+\s*=\s*)?(?:global::)?Newtonsoft\b)",
        RegexOptions.Multiline);

    /// <summary>
    /// Dieselben Umgehungen als <c>&lt;Using&gt;</c> in Projektdateien (implizite globale Usings). Die Datei wird als XML
    /// gelesen, die Reihenfolge der Attribute spielt keine Rolle (Codex-Nachprüfung 7, E01). Eine Projektdatei, die sich
    /// nicht als XML lesen lässt, gilt als Verstoss, damit nichts ungeprüft durchgeht.
    /// </summary>
    private static IEnumerable<string> HiddenProjectReaders(string path, string xml)
    {
        System.Xml.Linq.XElement root;
        try
        {
            // Ein Ausschnitt ohne gemeinsames Wurzelelement wird für die Prüfung eingefasst
            var body = Regex.Replace(xml, @"^\s*<\?xml[^>]*\?>", string.Empty);
            root = System.Xml.Linq.XElement.Parse("<Wurzel>" + body + "</Wurzel>");
        }
        catch (System.Xml.XmlException)
        {
            return [$"{path}: VERBOTEN Projektdatei nicht als XML lesbar"];
        }

        return root.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Using", StringComparison.OrdinalIgnoreCase))
            .Where(element =>
            {
                string? Attribute(string name) => element.Attributes()
                    .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;
                var include = (Attribute("Include") ?? string.Empty).Trim();
                if (include.StartsWith("global::", StringComparison.Ordinal))
                {
                    include = include["global::".Length..];
                }

                var json = include == "System.Text.Json" || include.StartsWith("System.Text.Json.", StringComparison.Ordinal);
                var newtonsoft = include == "Newtonsoft" || include.StartsWith("Newtonsoft.", StringComparison.Ordinal);
                var isStatic = string.Equals(Attribute("Static")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                return newtonsoft || (json && (isStatic || Attribute("Alias") is not null));
            })
            .Select(element => $"{path}: VERBOTEN {element}");
    }

    /// <summary>
    /// Entfernt Kommentare und den Inhalt von Zeichen- und Zeichenkettenliteralen, lexikalisch wie C# (6-25-02): normale,
    /// wörtliche (<c>@</c>), rohe (drei oder mehr Anführungszeichen) und interpolierte (<c>$</c>) Zeichenketten. Der Code in
    /// Interpolationslöchern bleibt erhalten, damit ein Leser dort zählt; ein <c>//</c> in einer Zeichenkette ist kein
    /// Kommentar.
    /// </summary>
    internal static string StripCommentsAndLiterals(string code)
    {
        var output = new StringBuilder(code.Length);
        var index = 0;
        StripCode(code, ref index, output, inHole: false, closeBraces: 0);
        return output.ToString();
    }

    /// <summary>Liest Code bis zum Ende oder, in einem Interpolationsloch, bis zu dessen schliessenden Klammern.</summary>
    private static void StripCode(string code, ref int index, StringBuilder output, bool inHole, int closeBraces)
    {
        var depth = 0;
        var nesting = 0;
        while (index < code.Length)
        {
            var c = code[index];
            var next = index + 1 < code.Length ? code[index + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (index < code.Length && code[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (c == '/' && next == '*')
            {
                var end = code.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? code.Length : end + 2;
                output.Append(' ');
                continue;
            }

            if (c == '\'')
            {
                // Zeichenliteral: bis zum schliessenden Apostroph, Escapes überspringen
                index++;
                while (index < code.Length && code[index] != '\'' && code[index] != '\n')
                {
                    index += code[index] == '\\' ? 2 : 1;
                }

                index++;
                output.Append("' '");
                continue;
            }

            if (c is '"' or '$' or '@' && StartsLiteral(code, index))
            {
                StripLiteral(code, ref index, output);
                continue;
            }

            if (inHole)
            {
                if (c is '(' or '[')
                {
                    nesting++;
                }
                else if (c is ')' or ']')
                {
                    nesting--;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    if (depth == 0)
                    {
                        // Ende des Lochs: so viele schliessende Klammern, wie es geöffnet wurde
                        index += closeBraces;
                        return;
                    }

                    depth--;
                }
                else if (c == ':' && nesting == 0 && depth == 0 && next != ':' && (index == 0 || code[index - 1] != ':'))
                {
                    // Formatteil nach dem Doppelpunkt auf oberster Ebene (Codex-Nachprüfung 7, E02): Text bis zum Ende
                    // des Lochs, kein Code. Ein Bedingungsausdruck muss im Loch geklammert sein, global:: ist kein Format
                    while (index < code.Length && code[index] != '}')
                    {
                        index++;
                    }

                    continue;
                }
            }

            output.Append(c);
            index++;
        }
    }

    private static bool StartsLiteral(string code, int index)
    {
        var i = index;
        while (i < code.Length && code[i] is '$' or '@')
        {
            i++;
        }

        // Ein @ vor einem Bezeichner (@class) beginnt keine Zeichenkette
        return i < code.Length && code[i] == '"';
    }

    private static void StripLiteral(string code, ref int index, StringBuilder output)
    {
        var dollars = 0;
        var verbatim = false;
        while (code[index] is '$' or '@')
        {
            if (code[index] == '$')
            {
                dollars++;
            }
            else
            {
                verbatim = true;
            }

            index++;
        }

        var quotes = Count(code, index, '"');
        var raw = quotes >= 3;
        if (!raw)
        {
            quotes = 1;
        }

        index += quotes;
        output.Append("\"\"");
        while (index < code.Length)
        {
            var c = code[index];
            if (raw && c == '"' && Count(code, index, '"') >= quotes)
            {
                index += quotes;
                return;
            }

            if (!raw && c == '"')
            {
                if (verbatim && index + 1 < code.Length && code[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                index++;
                return;
            }

            if (!raw && !verbatim && c == '\\')
            {
                index += 2;
                continue;
            }

            if (!raw && !verbatim && c == '\n')
            {
                return;
            }

            if (dollars > 0 && c == '{')
            {
                var braces = Count(code, index, '{');
                var opening = raw ? dollars : 1;
                if (!raw && braces >= 2)
                {
                    // {{ ist eine wörtliche Klammer
                    index += 2;
                    continue;
                }

                if (braces >= opening)
                {
                    index += braces;
                    output.Append(' ');
                    StripCode(code, ref index, output, inHole: true, closeBraces: opening);
                    output.Append(' ');
                    continue;
                }
            }

            index++;
        }
    }

    private static int Count(string code, int index, char c)
    {
        var count = 0;
        while (index + count < code.Length && code[index + count] == c)
        {
            count++;
        }

        return count;
    }

    /// <summary>Alle Leser einer Datei als „Pfad: Form“, eine Zeile je Aufrufstelle; Kommentare und Literale zählen nicht.</summary>
    internal static IEnumerable<string> JsonReaderInventory(string path, string code)
    {
        if (path.EndsWith("proj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var hidden in HiddenProjectReaders(path, code))
            {
                yield return hidden;
            }

            yield break;
        }

        var text = StripCommentsAndLiterals(code);
        foreach (Match hidden in HiddenReader.Matches(text))
        {
            yield return $"{path}: VERBOTEN {hidden.Value.Trim()}";
        }

        foreach (Match match in JsonReader.Matches(text))
        {
            yield return $"{path}: {Regex.Replace(match.Value, @"\s+", string.Empty)}";
        }
    }

    [Fact]
    public void NoJsonInputOutsideTheMatrixExists()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Freihand.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        // Quelltext und Projektdateien des Produkts unter src, dazu die gemeinsamen Projektdateien im Hauptverzeichnis
        // (implizite globale Usings könnten dort stehen)
        var source = Path.Combine(directory.FullName, "src");
        var found = new[] { "*.cs", "*.csproj", "*.props", "*.targets" }
            .SelectMany(pattern => Directory.EnumerateFiles(source, pattern, SearchOption.AllDirectories))
            .Concat(new[] { "*.props", "*.targets" }.SelectMany(pattern => Directory.EnumerateFiles(directory.FullName, pattern)))
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .SelectMany(file => JsonReaderInventory(
                Path.GetRelativePath(directory.FullName, file).Replace('\\', '/'), File.ReadAllText(file)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Jede Aufrufstelle einzeln: Profile und Befehle (DataSafety, gemeinsamer Rahmen: Parse und Deserialize des
        // Wurzelelements; ProfileSerializer und CommandSetSerializer rufen DataSafety.Deserialize), Messbericht
        // (Deserialize; Utf8JsonReader nur im Konverter für Zeitspannen, der Werte aus dem schon gelesenen Text liest),
        // Entwurfsdatei. Kommt ein Leser dazu, auch in einer dieser Dateien, muss er in
        // diese Matrix aufgenommen und hier eingetragen werden.
        Assert.Equal(
            [
                // Pipe des Erkenners (P8): einziger Leser ParseHeader, Read auf dem geprüften Kopf (PipeUnicodeMatrixTests)
                "src/Freihand.Adapters/Recognition/PipeFraming.cs: .Deserialize",
                "src/Freihand.Adapters/Recognition/PipeFraming.cs: JsonNode.Parse",
                "src/Freihand.Core/Commands/CommandSetSerializer.cs: .Deserialize",
                "src/Freihand.Core/Profiles/DataSafety.cs: .Deserialize",
                "src/Freihand.Core/Profiles/DataSafety.cs: JsonDocument.Parse",
                "src/Freihand.Core/Profiles/ProfileSerializer.cs: .Deserialize",
                "src/Freihand.Core/Quality/QualityReportJson.cs: .Deserialize",
                "src/Freihand.Core/Quality/QualityReportJson.cs: Utf8JsonReader",
                "src/Freihand.Core/Recovery/DraftFile.cs: .Deserialize",
            ],
            found);
    }

    /// <summary>Kontrollen des Wächters mit Textbeispielen, ohne Quelldateien zu ändern (Y01).</summary>
    [Theory]
    [InlineData("var x = JsonSerializer.Deserialize<Foo>(json);", 1)]
    [InlineData("var x = System.Text.Json.JsonSerializer.Deserialize<object>(json);", 1)]
    [InlineData("var x = await JsonSerializer.DeserializeAsync<Foo>(stream);", 1)]
    [InlineData("await foreach (var x in JsonSerializer.DeserializeAsyncEnumerable<Foo>(stream)) { }", 1)]
    [InlineData("using var d = JsonDocument.Parse(json);", 1)]
    [InlineData("using var d = await JsonDocument.ParseAsync(stream);", 1)]
    [InlineData("var n = JsonNode.Parse(json);", 1)]
    [InlineData("var o = JsonObject.Parse(json);", 1)]
    [InlineData("var e = JsonElement.ParseValue(ref reader);", 1)]
    [InlineData("var v = element.Deserialize<Foo>();", 1)]
    [InlineData("var v = node.Deserialize(typeInfo);", 1)]
    [InlineData("var r = new Utf8JsonReader(bytes);", 1)]
    [InlineData("var v = await content.ReadFromJsonAsync<Foo>();", 1)]
    [InlineData("var v = await client.GetFromJsonAsync<Foo>(uri);", 1)]
    [InlineData("var v = JsonConvert.DeserializeObject<Foo>(json);", 1)]
    [InlineData("var v = JObject.Parse(json);", 1)]
    [InlineData("using static System.Text.Json.JsonSerializer;", 1)]
    [InlineData("using Json = System.Text.Json.JsonSerializer;", 1)]
    [InlineData("using Newtonsoft.Json;", 1)]
    [InlineData("var n = int.Parse(text);", 0)]
    [InlineData("var json = JsonSerializer.Serialize(value);", 0)]
    [InlineData("// JsonSerializer.Deserialize<Foo>(json) im Kommentar", 0)]
    [InlineData("/* JsonDocument.Parse(json) */ var a = 1;", 0)]
    [InlineData("using System.Text.Json;", 0)]
    public void TheInputGuardRecognizesEveryReaderForm(string code, int expected)
    {
        Assert.Equal(expected, JsonReaderInventory("x.cs", code).Count());
    }

    /// <summary>Kontrollen für Literale, Kommentare, globale Imports und Projektdateien (Codex-Nachprüfung 6, 6-25-01/02).</summary>
    [Theory]
    [InlineData("x.cs", "var s = \"JsonSerializer.Deserialize<X>(j)\";", 0)]
    [InlineData("x.cs", "var s = \"https://example.invalid\"; var r = JsonDocument.Parse(j);", 1)]
    [InlineData("x.cs", "var s = \"a\\\" // b\"; var r = JsonDocument.Parse(j);", 1)]
    [InlineData("x.cs", "var s = @\"c:\\x\"\" // JsonDocument.Parse(j)\"; var r = JsonDocument.Parse(j);", 1)]
    [InlineData("x.cs", "var s = \"\"\" JsonDocument.Parse(j) // \"\"\"; var r = JsonNode.Parse(j);", 1)]
    [InlineData("x.cs", "var s = $\"{JsonSerializer.Deserialize<X>(j)}\";", 1)]
    [InlineData("x.cs", "var s = $\"{{ JsonDocument.Parse(j) }}\";", 0)]
    [InlineData("x.cs", "var s = $\"{(a ? \"//\" : \"b\")} {JsonNode.Parse(j)}\";", 1)]
    [InlineData("x.cs", "var s = $$\"\"\"{ JsonDocument.Parse(j) } {{JsonNode.Parse(j)}}\"\"\";", 1)]
    [InlineData("x.cs", "var c = '\"'; var r = JsonDocument.Parse(j);", 1)]
    [InlineData("x.cs", "var c = '\\''; var r = JsonDocument.Parse(j);", 1)]
    [InlineData("x.cs", "var t = \"a\" + /* JsonDocument.Parse(j) */ \"b\";", 0)]
    [InlineData("x.cs", "global using static System.Text.Json.JsonSerializer;", 1)]
    [InlineData("x.cs", "global using J = global::System.Text.Json.JsonDocument;", 1)]
    [InlineData("x.cs", "using static global::System.Text.Json.JsonSerializer;", 1)]
    [InlineData("x.cs", "global using Newtonsoft.Json;", 1)]
    [InlineData("x.cs", "global using System.Text.Json;", 0)]
    [InlineData("x.csproj", "<Using Include=\"System.Text.Json.JsonSerializer\" Static=\"true\" />", 1)]
    [InlineData("x.csproj", "<Using Include=\"System.Text.Json.JsonDocument\" Alias=\"J\" />", 1)]
    [InlineData("Directory.Build.props", "<Using Include=\"Newtonsoft.Json\" />", 1)]
    [InlineData("x.csproj", "<Using Include=\"System.Text.Json\" />", 0)]
    [InlineData("x.csproj", "<Using Alias=\"J\" Include=\"System.Text.Json.JsonDocument\" />", 1)]
    [InlineData("x.csproj", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project><ItemGroup><Using Static=\"True\" Include=\"global::System.Text.Json.JsonSerializer\" /></ItemGroup></Project>", 1)]
    [InlineData("x.csproj", "<?xml version=\"1.0\"?>\n<Project><ItemGroup><Using Include=\"System.Text.Json\" /></ItemGroup></Project>", 0)]
    [InlineData("x.props", "<Project><ItemGroup><Using Include=\"System.Text.Json\"", 1)]
    [InlineData("x.cs", "var s = $\"{(ok ? JsonDocument.Parse(j) : null)}\";", 1)]
    [InlineData("x.cs", "var s = $\"{global::System.Text.Json.JsonDocument.Parse(j)}\";", 1)]
    [InlineData("x.cs", "var s = $\"{value,10:JsonDocument.Parse}\";", 0)]
    [InlineData("x.cs", "var s = $\"{items[0]:N2} {JsonNode.Parse(j)}\";", 1)]
    public void TheInputGuardHandlesLiteralsCommentsAndImports(string path, string code, int expected)
    {
        Assert.Equal(expected, JsonReaderInventory(path, code).Count());
    }

    [Fact]
    public void TheInputGuardCountsAnAdditionalReaderInAKnownFile()
    {
        const string known = "static Foo Read(string json) => JsonSerializer.Deserialize<Foo>(json);";
        var before = JsonReaderInventory("bekannt.cs", known).ToArray();
        var after = JsonReaderInventory("bekannt.cs", known + "\nstatic object? Mehr(string json) => JsonSerializer.Deserialize<object>(json);").ToArray();
        Assert.Single(before);
        Assert.Equal(2, after.Length);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void EveryInputHasKnownAndUnknownPlacesOnEveryLevel()
    {
        foreach (var input in JsonInputs)
        {
            var places = PlacesOf(input).ToArray();
            Assert.Contains(places, place => place.StartsWith("known:", StringComparison.Ordinal));
            Assert.Contains(places, place => place.StartsWith("unknown-value@$.", StringComparison.Ordinal));
            Assert.Contains("unknown-key@$", places);
        }

        var quality = PlacesOf(QualityInput).ToArray();
        Assert.Contains("unknown-value@$.measurement", quality);
        Assert.Contains("unknown-value@$.wordErrors.sentences[]", quality);
        Assert.Contains("unknown-value@$.latency.fixedWindows[]", quality);
        var profile = PlacesOf(ProfileV2).ToArray();
        Assert.Contains("unknown-value@$.profiles[].recognition", profile);
        Assert.Contains("unknown-value@$.profiles[].audioSource", profile);
    }
}
