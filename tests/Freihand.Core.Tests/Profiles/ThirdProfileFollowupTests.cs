using Freihand.Core.Commands;
using Freihand.Core.Profiles;
using static Freihand.Core.Tests.Profiles.ProfileSamples;
namespace Freihand.Core.Tests.Profiles;
public sealed class ThirdProfileFollowupTests
{
    [Fact]
    public void EscapedLoneSurrogateInNameUsesDocumentErrorPath()
    {
        var json = FullDocumentV2.Replace("Mail online", "Mail" + (char)92 + "ud800", StringComparison.Ordinal);
        var result = ProfileSerializer.Import(json, AllowList);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path == "$");
    }
    [Theory]
    [InlineData(false, "\\ud800")]
    [InlineData(false, "\\udc00")]
    [InlineData(true, "\\ud800")]
    [InlineData(true, "\\udc00")]
    public void InvalidUnicodeInKindReturnsImportError(bool commands, string escape)
    {
        var json = "{\"schemaVersion\":" + (commands ? 1 : 2) + ",\"kind\":\"" + escape + "\"}";
        if (commands)
        {
            var result = CommandSetSerializer.Import(json);
            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, error => error.Path == "$");
        }
        else
        {
            var result = ProfileSerializer.Import(json, AllowList);
            Assert.False(result.Succeeded);
            Assert.Contains(result.Errors, error => error.Path == "$");
        }
    }
}
