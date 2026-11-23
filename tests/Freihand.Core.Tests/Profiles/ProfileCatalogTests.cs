using Freihand.Core.Profiles;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>Eingebaute Profile sind nicht bearbeitbar (BA-21); eigene Profile kommen dazu (BA-01).</summary>
public class ProfileCatalogTests
{
    [Fact]
    public void StartsWithTheBuiltInProfiles()
    {
        var catalog = new ProfileCatalog();

        Assert.Equal(BuiltInProfiles.All, catalog.Profiles);
        Assert.True(ProfileCatalog.IsBuiltIn("standard"));
        Assert.True(ProfileCatalog.IsBuiltIn("strict"));
    }

    [Fact]
    public void ImportsOwnProfilesAfterTheBuiltInOnes()
    {
        var catalog = new ProfileCatalog();

        var result = catalog.Import(FullDocument, AllowList);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(["standard", "strict", "mail-online"], catalog.Profiles.Select(profile => profile.Id));
        Assert.False(ProfileCatalog.IsBuiltIn("mail-online"));
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("strict")]
    public void BuiltInProfilesCannotBeReplaced(string id)
    {
        var catalog = new ProfileCatalog();

        var result = catalog.Import(WithProfile(p => p["id"] = id), AllowList);

        Assert.False(result.Succeeded);
        Assert.Equal("$.profiles[0].id", Assert.Single(result.Errors).Path);
        Assert.Equal(BuiltInProfiles.All, catalog.Profiles);
    }

    [Fact]
    public void BuiltInProfilesCannotBeRemoved()
    {
        var catalog = new ProfileCatalog();

        Assert.False(catalog.Remove("standard"));
        Assert.False(catalog.Remove("strict"));
        Assert.Equal(2, catalog.Profiles.Count);
    }

    [Fact]
    public void AnImportNeverSilentlyReplacesAnOwnProfile()
    {
        var catalog = new ProfileCatalog();
        catalog.Import(FullDocument, AllowList);

        var result = catalog.Import(WithProfile(p => p["name"] = "Anders"), AllowList);

        Assert.False(result.Succeeded);
        Assert.Equal("Mail online", catalog.Find("mail-online")!.Name);
    }

    [Fact]
    public void ImportIsAllOrNothing()
    {
        var catalog = new ProfileCatalog();
        var json = WithRoot(root =>
        {
            var second = root["profiles"]![0]!.DeepClone();
            second["id"] = "standard";
            root["profiles"]!.AsArray().Add(second);
        });

        var result = catalog.Import(json, AllowList);

        Assert.False(result.Succeeded);
        Assert.Null(catalog.Find("mail-online"));
    }

    [Fact]
    public void OwnProfilesCanBeRemovedAndExported()
    {
        var catalog = new ProfileCatalog();
        catalog.Import(FullDocument, AllowList);

        Assert.Equal(FullDocumentV2 + "\n", catalog.ExportImported());
        Assert.True(catalog.Remove("mail-online"));
        Assert.Null(catalog.Find("mail-online"));
    }
}
