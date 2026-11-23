using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Freihand.Core.Recovery;
using Freihand.Core.Tests.Recovery;
namespace Freihand.Core.Tests.Profiles;
public sealed class FourthDraftFollowupTests : RecoveryTestBase
{
    // Die Negativ-Fixture muss ohne das absichtlich ungültige Zeichen einen lesbaren Entwurf erzeugen.
    [Fact]
    public void ExistingDraftFixtureAcceptsSafeControl()
    {
        using var fixture = new LoneSurrogateTests();
        const string json = "{\"id\":\"DRAFTID\",\"createdAt\":\"2026-10-29T10:00:00+00:00\",\"updatedAt\":\"2026-10-29T10:00:00+00:00\",\"handedOverAt\":null,\"segments\":[{\"text\":\"synthetic\"}]}";
        var method = typeof(LoneSurrogateTests).GetMethod("ScanWith", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var issues = (string)method.Invoke(fixture, [json])!;
        Assert.Equal(string.Empty, issues);
    }

    // ER-06: Vollständige gültige Kontroll-Nutzlast, danach genau den Textwert beschädigen.
    [Theory]
    [InlineData("\\ud800")]
    [InlineData("\\udc00")]
    [InlineData("\\udc00\\ud800")]
    public void CompleteDraftRejectsEscapedLoneSurrogate(string fragment)
    {
        var id = Guid.NewGuid();
        var json = ValidJson(id);
        var valid = Scan(Encoding.UTF8.GetBytes(json), id);
        Assert.Empty(valid.Issues);
        Assert.Equal("PLACEHOLDER", Assert.Single(Assert.Single(valid.Drafts).Segments).Text);
        var invalid = json.Replace("PLACEHOLDER", fragment, StringComparison.Ordinal);
        var result = Scan(Encoding.UTF8.GetBytes(invalid), id);
        Assert.Empty(result.Drafts);
        Assert.Contains(result.Issues, issue => issue.Kind == RecoveryIssueKind.Damaged);
    }

    [Fact]
    public void CompleteDraftRejectsInvalidUtf8SurrogateBytes()
    {
        var id = Guid.NewGuid();
        var parts = ValidJson(id).Split("PLACEHOLDER", StringSplitOptions.None);
        Assert.Equal(2, parts.Length);
        byte[] invalid = [.. Encoding.UTF8.GetBytes(parts[0]), 0xED, 0xA0, 0x80, .. Encoding.UTF8.GetBytes(parts[1])];
        var result = Scan(invalid, id);
        Assert.Empty(result.Drafts);
        Assert.Contains(result.Issues, issue => issue.Kind == RecoveryIssueKind.Damaged);
    }

    private string ValidJson(Guid id) => JsonSerializer.Serialize(new
    {
        id, createdAt = Time.GetUtcNow(), updatedAt = Time.GetUtcNow(), handedOverAt = (DateTimeOffset?)null,
        segments = new[] { new { id = Guid.NewGuid(), text = "PLACEHOLDER" } },
    });

    private RecoveryScan Scan(byte[] json, Guid id)
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
}
