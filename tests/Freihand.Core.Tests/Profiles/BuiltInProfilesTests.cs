using Freihand.Core.Profiles;
using Freihand.Core.Routing;

namespace Freihand.Core.Tests.Profiles;

/// <summary>Die eingebauten Pilot-Profile „Standard“ und „Streng“ (BA-21, BA-12, SI-04).</summary>
public class BuiltInProfilesTests
{
    [Fact]
    public void LoadsBothBuiltInProfilesInOrder()
    {
        Assert.Equal(["standard", "strict"], BuiltInProfiles.All.Select(profile => profile.Id));
        Assert.Equal(["Standard", "Streng"], BuiltInProfiles.All.Select(profile => profile.Name));
    }

    [Fact]
    public void BothRecognizeOnDeviceWithoutTransformInThePilot()
    {
        foreach (var profile in BuiltInProfiles.All)
        {
            // WE-07, Grundentscheid 3: im Pilot Erkennung auf dem Gerät und keine KI-Umformung.
            Assert.Equal(new RecognitionStep(RecognitionRoute.Device), profile.Recognition);
            Assert.Equal(new TransformStep(TransformRoute.None), profile.Transform);
            Assert.False(profile.Recognition.Route.LeavesDevice());

            // Grundentscheid 1, BA-17: Hochdeutsch in Schweizer Schreibweise.
            Assert.Equal("de-CH", profile.Language);
            Assert.Equal("ch-standard", profile.FormatRules);

            // BF-06: Ein/Aus ist Standard. ER-12: Audioquelle im Profil.
            Assert.Equal(RecordingMode.Toggle, profile.RecordingMode);
            Assert.Equal(AudioSourceSelection.SystemDefault, profile.AudioSource);
        }
    }

    [Fact]
    public void StrictIsAnExactProfileThatRejectsUnknownFields()
    {
        var strict = BuiltInProfiles.Strict;

        Assert.True(strict.IsExact); // BA-12
        Assert.True(strict.RejectsUnknownFields); // SI-04
        Assert.Equal(ProfileStrictness.Strict, strict.Strictness);
        Assert.Equal(DataClass.Confidential, strict.DataClass);
        Assert.Equal(OutputMode.DictationWindow, strict.Output);
    }

    [Fact]
    public void StandardDoesNotRejectUnknownFields()
    {
        var standard = BuiltInProfiles.Standard;

        Assert.False(standard.RejectsUnknownFields);
        Assert.Equal(DataClass.Internal, standard.DataClass);
        Assert.Equal(OutputMode.BoundTarget, standard.Output);
    }

    [Fact]
    public void BuiltInProfilesAreStoredInTheImportSchema()
    {
        // BA-21: im Datenschema nach OF-01 abgelegt; dieselbe Prüfung wie ein Import, ohne Endpunkte.
        var result = ProfileSerializer.Import(BuiltInProfiles.Json, EndpointAllowList.None);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(BuiltInProfiles.All, result.Value);
    }

    [Fact]
    public void BuiltInProfilesConformToTheJsonSchema()
    {
        var validator = new MiniSchemaValidator(RepositoryFiles.ReadSchema("profiles.v2.schema.json"));

        Assert.Empty(validator.Validate(System.Text.Json.Nodes.JsonNode.Parse(BuiltInProfiles.Json)));
    }
}
