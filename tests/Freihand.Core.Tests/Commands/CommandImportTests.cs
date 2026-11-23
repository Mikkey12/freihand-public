using System.Text.Json.Nodes;
using Freihand.Core.Commands;
using Freihand.Core.Tests.Profiles;

namespace Freihand.Core.Tests.Commands;

/// <summary>Import der Befehlsliste mit Prüfung nach OF-01, Hin- und Rückweg ohne Verlust.</summary>
public class CommandImportTests
{
    /// <summary>Abzuweisende Dokumente: Name, Dokument, erwarteter Fehlerpfad, ob das JSON-Schema den Fehler erkennt.</summary>
    internal static readonly Dictionary<string, (string Json, string Path, bool SchemaRejects)> Invalid = new()
    {
        ["no-json"] = ("{ commands: [] }", "$", false),
        ["kind-profiles"] = (WithRoot(root => root["kind"] = "freihand.profiles"), "$.kind", true),
        ["version-newer"] = (WithRoot(root => root["schemaVersion"] = 2), "$.schemaVersion", true),
        ["commands-missing"] = (WithRoot(root => root.Remove("commands")), "$.commands", true),
        ["unknown-field"] = (WithFirst(c => c["script"] = "Remove-Item C:\\"), "$.commands[0]", true),
        ["id-missing"] = (WithFirst(c => c.Remove("id")), "$.commands[0].id", true),
        ["class-unknown"] = (WithFirst(c => c["class"] = "macro"), "$.commands[0].class", true),
        ["effect-unknown"] = (WithFirst(c => c["effect"] = "runScript"), "$.commands[0].effect", true),
        ["scope-unknown"] = (WithFirst(c => c["scope"] = "outlook"), "$.commands[0].scope", true),
        ["description-missing"] = (WithFirst(c => c.Remove("description")), "$.commands[0].description", true),

        // Grundentscheid 5, BA-20: Klasse passt zur Wirkung; Aktionen gibt es im Pilot nicht.
        ["dictation-mark-as-command"] = (WithFirst(c => c["class"] = "command"), "$.commands[0].class", true),
        ["command-as-dictation-mark"] = (WithCommand("cancel", c => c["class"] = "dictationMark"), "$.commands[13].class", true),
        ["action-class"] = (WithCommand("cancel", c => c["class"] = "action"), "$.commands[13].class", true),

        // BA-22, BE-06: Ausweg und Abbruch gelten überall.
        ["literal-mode-window-only"] = (WithCommand("literal-mode", c => c["scope"] = "dictationWindow"), "$.commands[7].scope", true),
        ["cancel-window-only"] = (WithCommand("cancel", c => c["scope"] = "dictationWindow"), "$.commands[13].scope", true),

        // Codex-Review R8, Anhang A: Korrektur nur im Diktatfenster; ausserhalb wäre sie eine Aktion (SI-09).
        ["delete-word-everywhere"] = (WithCommand("delete-last-word", c => c["scope"] = "everywhere"), "$.commands[8].scope", true),
        ["delete-sentence-everywhere"] = (WithCommand("delete-last-sentence", c => c["scope"] = "everywhere"), "$.commands[9].scope", true),
        ["undo-everywhere"] = (WithCommand("undo", c => c["scope"] = "everywhere"), "$.commands[10].scope", true),
        ["commit-everywhere"] = (WithCommand("commit", c => c["scope"] = "everywhere"), "$.commands[11].scope", true),

        // BA-20: Diktatzeichen setzen nur Satzzeichen aus der festen Liste.
        ["punctuation-text-missing"] = (WithFirst(c => c.Remove("text")), "$.commands[0].text", true),
        ["punctuation-newline"] = (WithFirst(c => c["text"] = "\n"), "$.commands[0].text", true),
        ["punctuation-word"] = (WithFirst(c => c["text"] = "Komma"), "$.commands[0].text", true),
        ["punctuation-markup"] = (WithFirst(c => c["text"] = "<"), "$.commands[0].text", true),
        ["text-on-command"] = (WithCommand("cancel", c => c["text"] = "."), "$.commands[13].text", true),

        // Wendungen: sprechbar, eindeutig, begrenzt
        ["phrases-missing"] = (WithFirst(c => c.Remove("phrases")), "$.commands[0].phrases", true),
        ["phrases-empty"] = (WithFirst(c => c["phrases"] = new JsonArray()), "$.commands[0].phrases", true),
        ["phrase-null"] = (WithFirst(c => c["phrases"] = new JsonArray((JsonNode?)null)), "$.commands[0].phrases[0]", true),
        ["phrase-digits"] = (WithFirst(c => c["phrases"] = new JsonArray("Komma 2")), "$.commands[0].phrases[0]", true),
        ["phrase-double-space"] = (WithFirst(c => c["phrases"] = new JsonArray("neue  Zeile")), "$.commands[0].phrases[0]", true),
        ["phrase-markup"] = (WithFirst(c => c["phrases"] = new JsonArray("<script>")), "$.commands[0].phrases[0]", true),
        ["phrase-too-long"] = (WithFirst(c => c["phrases"] = new JsonArray(new string('a', 49))), "$.commands[0].phrases[0]", true),
        ["too-many-phrases"] = (WithFirst(c => c["phrases"] = new JsonArray([.. "abcdefghi".Select(letter => (JsonNode?)JsonValue.Create(letter.ToString()))])), "$.commands[0].phrases", true),
        ["phrase-duplicate-case"] = (WithFirst(c => c["phrases"] = new JsonArray("Komma", "punkt")), "$.commands[1].phrases[0]", false),
        ["id-duplicate"] = (WithFirst(c => c["id"] = "period"), "$.commands[1].id", false),

        // Keine Geheimnisse und keine ausführbaren Inhalte (OF-01)
        // Codex-Review R7: auch ein syntaktisch gültiger Bezeichner darf kein Geheimnis sein (SI-03).
        ["id-secret"] = (WithFirst(c => c["id"] = FakeKey), "$.commands[0].id", false),
        ["description-secret"] = (WithFirst(c => c["description"] = "token: abcdef"), "$.commands[0].description", false),
        ["description-script"] = (WithFirst(c => c["description"] = "javascript:alert(1)"), "$.commands[0].description", false),
        ["description-placeholder"] = (WithFirst(c => c["description"] = "{{ user.name }}"), "$.commands[0].description", false),
    };

    public static TheoryData<string> InvalidCases => [.. Invalid.Keys];

    /// <summary>Erfundener Wert im Format eines Schlüssels, zugleich ein syntaktisch gültiger Bezeichner.</summary>
    private static string FakeKey => "sk-" + new string('a', 24);

    public static TheoryData<string> SchemaRejectedCases =>
        [.. Invalid.Where(entry => entry.Value.SchemaRejects).Select(entry => entry.Key)];

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void RejectsInvalidOrUnsafeDocument(string name)
    {
        var (json, path, _) = Invalid[name];

        var result = CommandSetSerializer.Import(json);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path.StartsWith(path, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ProfileImportTests.OrdinaryIdentifiers), MemberType = typeof(ProfileImportTests))]
    public void AcceptsOrdinaryIdentifiers(string identifier)
    {
        // Codex-Nachprüfung N2: „task-…“ und „desk-…“ sind keine Schlüssel, auch nicht als Befehls-Id.
        var result = CommandSetSerializer.Import(WithFirst(c => c["id"] = identifier));

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.NotNull(result.Value.FindById(identifier));

        var reimported = CommandSetSerializer.Import(CommandSetSerializer.Export(result.Value));
        Assert.True(reimported.Succeeded, string.Join("\n", reimported.Errors));
        Assert.Equal(result.Value.Commands, reimported.Value.Commands);
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void ErrorMessagesNeverRepeatTheRejectedValue(string name)
    {
        // Codex-Review R7: Eine Meldung landet in Anzeige und Protokoll und darf kein Geheimnis wiedergeben.
        var result = CommandSetSerializer.Import(Invalid[name].Json);

        Assert.All(result.Errors, error => Assert.DoesNotContain("sk-", error.Message, StringComparison.Ordinal));
        Assert.All(result.Errors, error => Assert.DoesNotContain("abcdef", error.Message, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(SchemaRejectedCases))]
    public void JsonSchemaRejectsWhatItCanExpress(string name)
    {
        var validator = new MiniSchemaValidator(RepositoryFiles.ReadSchema("commands.v1.schema.json"));

        Assert.False(validator.IsValid(JsonNode.Parse(Invalid[name].Json)));
    }

    [Fact]
    public void PilotListExportsByteForByte()
    {
        Assert.Equal(BuiltInCommands.Json + "\n", CommandSetSerializer.Export(BuiltInCommands.Pilot));
    }

    [Fact]
    public void ExtendedListSurvivesExportAndImport()
    {
        // Mehrere Wendungen je Eintrag und ein Strichpunkt; erfundene Erweiterung, keine Pilot-Befehle.
        var root = JsonNode.Parse(WithFirst(c => c["phrases"] = new JsonArray("Komma", "Beistrich")))!.AsObject();
        root["commands"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "semicolon",
            ["class"] = "dictationMark",
            ["phrases"] = new JsonArray("Strichpunkt", "Semikolon"),
            ["scope"] = "everywhere",
            ["effect"] = "insertPunctuation",
            ["text"] = ";",
            ["description"] = "setzt einen Strichpunkt",
        });

        var original = CommandSetSerializer.Import(root.ToJsonString());
        Assert.True(original.Succeeded, string.Join("\n", original.Errors));

        var reimported = CommandSetSerializer.Import(CommandSetSerializer.Export(original.Value));

        Assert.True(reimported.Succeeded, string.Join("\n", reimported.Errors));
        Assert.Equal(original.Value.Commands, reimported.Value.Commands);
        Assert.Equal(["Komma", "Beistrich"], reimported.Value.FindById("comma")!.Phrases);
    }

    [Fact]
    public void SchemaEnumerationsMatchTheCode()
    {
        var schema = RepositoryFiles.ReadSchema("commands.v1.schema.json");
        var properties = schema["$defs"]!["command"]!["properties"]!;

        Assert.Equal(CommandSchema.SchemaId, schema["$id"]!.GetValue<string>());
        Assert.Equal(CommandSchema.Kind, schema["properties"]!["kind"]!["const"]!.GetValue<string>());
        Assert.Equal(CommandSchema.CurrentVersion, schema["properties"]!["schemaVersion"]!["const"]!.GetValue<int>());
        Assert.Equal(CommandSchema.Classes.Names, Names(properties["class"]));
        Assert.Equal(CommandSchema.Scopes.Names, Names(properties["scope"]));
        Assert.Equal(CommandSchema.Effects.Names, Names(properties["effect"]));
        Assert.Equal(CommandSchema.Punctuation, Names(properties["text"]));
        Assert.Equal(CommandSchema.PhrasePattern, properties["phrases"]!["items"]!["pattern"]!.GetValue<string>());
    }

    [Fact]
    public void NoEffectBelongsToTheActionClassInThePilot()
    {
        foreach (var effect in Enum.GetValues<CommandEffect>())
        {
            Assert.NotEqual(CommandClass.Action, CommandSchema.ClassOf(effect));
        }
    }

    private static string WithRoot(Action<JsonObject> change)
    {
        var root = JsonNode.Parse(BuiltInCommands.Json)!.AsObject();
        change(root);
        return root.ToJsonString();
    }

    private static string WithFirst(Action<JsonObject> change) =>
        WithRoot(root => change(root["commands"]![0]!.AsObject()));

    private static string WithCommand(string id, Action<JsonObject> change) =>
        WithRoot(root => change(root["commands"]!.AsArray().Single(node => node!["id"]!.GetValue<string>() == id)!.AsObject()));

    private static IEnumerable<string> Names(JsonNode? enumSchema) =>
        enumSchema!["enum"]!.AsArray().Select(node => node!.GetValue<string>());
}
