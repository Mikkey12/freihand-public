using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Freihand.Core.Recovery;
using Freihand.Core.Tests.Recovery;
namespace Freihand.Core.Tests.Review;
public sealed class FifthDraftCoverageTests : RecoveryTestBase
{
    [Theory]
    [InlineData("root-key")]
    [InlineData("segment-key")]
    [InlineData("root-id")]
    [InlineData("segment-id")]
    [InlineData("createdAt")]
    [InlineData("updatedAt")]
    [InlineData("handedOverAt")]
    public void AdditionalDraftLocationsHaveValidControlsAndRejectSurrogates(string place)
    {
        var id = Guid.NewGuid(); var sid = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new { id, createdAt=Time.GetUtcNow(), updatedAt=Time.GetUtcNow(), handedOverAt=Time.GetUtcNow(), segments=new[] {new {id=sid, text="synthetic"}} });
        var control = Scan(Encoding.UTF8.GetBytes(json), id);
        Assert.Empty(control.Issues); Assert.Single(control.Drafts);
        using var root = JsonDocument.Parse(json);
        string old;
        if(place=="root-key") old="\"segments\"";
        else if(place=="segment-key") old="\"text\"";
        else if(place=="root-id") old="\""+id+"\"";
        else if(place=="segment-id") old="\""+sid+"\"";
        else old="\""+place+"\":"+root.RootElement.GetProperty(place).GetRawText();
        var replacement = place is "createdAt" or "updatedAt" or "handedOverAt" ? "\""+place+"\":\"\\ud800\"" : "\"\\ud800\"";
        Assert.Contains(old,json,StringComparison.Ordinal);
        var invalid=json.Replace(old,replacement,StringComparison.Ordinal);
        var result=Scan(Encoding.UTF8.GetBytes(invalid),id);
        Assert.Empty(result.Drafts); Assert.Contains(result.Issues, issue=>issue.Kind==RecoveryIssueKind.Damaged);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MixedEscapeAndInvalidBytesAreRejected(bool rawHigh)
    {
        var id=Guid.NewGuid();
        var json=JsonSerializer.Serialize(new {id,createdAt=Time.GetUtcNow(),updatedAt=Time.GetUtcNow(),handedOverAt=(DateTimeOffset?)null,segments=new[]{new{id=Guid.NewGuid(),text="PLACEHOLDER"}}});
        Assert.Empty(Scan(Encoding.UTF8.GetBytes(json),id).Issues);
        var parts=json.Split("PLACEHOLDER",StringSplitOptions.None);
        byte[] fragment=rawHigh ? [0xED,0xA0,0x80,..Encoding.ASCII.GetBytes("\\udc00")] : [..Encoding.ASCII.GetBytes("\\ud800"),0xED,0xB0,0x80];
        var result=Scan([..Encoding.UTF8.GetBytes(parts[0]),..fragment,..Encoding.UTF8.GetBytes(parts[1])],id);
        Assert.Empty(result.Drafts); Assert.Contains(result.Issues,issue=>issue.Kind==RecoveryIssueKind.Damaged);
    }
    private RecoveryScan Scan(byte[] json,Guid id)
    {
        var payload=Protector.Protect(json);var file=new byte[9+payload.Length+32];"FRHD"u8.CopyTo(file);file[4]=1;
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(5,4),payload.Length);payload.CopyTo(file.AsSpan(9));SHA256.HashData(payload,file.AsSpan(9+payload.Length));
        Files.Files.Clear();Files.Files[id.ToString("N")+".draft"]=(file,Time.GetUtcNow());using var store=Open();return store.Scan();
    }
}
