using Freihand.Core.Profiles;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Leerraum in Endpunkten (Fund beim Abgleich von Schema und Import, PR #25): Das Schema weist jeden Leerraum nach
/// ECMA 262 ab (<c>[^?#@\s]</c>). Der Import liess geschützte Leerzeichen und ähnliche Zeichen im Rechnernamen
/// durch und warf dann beim Abgleich mit den zugelassenen Endpunkten eine <see cref="UriFormatException"/>, statt
/// den Fehler zu melden.
/// </summary>
public class EndpointWhiteSpaceTests
{
    public static TheoryData<char> WhiteSpace => [.. SchemaPatternTests.EcmaWhiteSpace, .. SchemaPatternTests.DotNetOnlyWhiteSpace];

    [Theory]
    [MemberData(nameof(WhiteSpace))]
    public void WhiteSpaceInTheHostIsReportedAsAnInvalidAddress(char c)
    {
        var json = WithProfileV2(p => p["recognition"]!["endpoint"] = "https://stt.exa" + c + "mple.com/v1/listen");

        var result = ProfileSerializer.Import(json, AllowList);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path == "$.profiles[0].recognition.endpoint");
    }

    [Theory]
    [MemberData(nameof(WhiteSpace))]
    public void WhiteSpaceInThePathIsReportedAsAnInvalidAddress(char c)
    {
        var json = WithProfileV2(p => p["recognition"]!["endpoint"] = "https://stt.example.com/v1/li" + c + "sten");

        var result = ProfileSerializer.Import(json, AllowList);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Path == "$.profiles[0].recognition.endpoint");
    }

    [Fact]
    public void AnAllowListNeverThrowsForAHostThatHasNoInternationalForm()
    {
        var allowList = new EndpointAllowList([new Uri("https://stt.example.com/v1")]);
        var created = Uri.TryCreate("https://stt.exa" + char.ConvertFromUtf32(0x00A0) + "mple.com/v1", UriKind.Absolute, out var endpoint);

        Assert.True(created, "Der Test setzt voraus, dass .NET diese Adresse erzeugt.");
        Assert.False(allowList.Allows(endpoint!));
    }
}
