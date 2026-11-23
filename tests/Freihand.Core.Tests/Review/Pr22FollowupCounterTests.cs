using Freihand.Core.Profiles;
using Freihand.Core.Tests.Profiles;

namespace Freihand.Core.Tests.Review;

// OF-01: Zeuge für die Erreichbarkeit der in P2 S25 beschriebenen Fixture.
public sealed class Pr22FollowupCounterTests
{
    [Fact]
    public void N01_S25FixtureFamilyMustBeAbleToReachTheDocumentLimit()
    {
        var imported = ProfileSerializer.Import(ProfileSamples.FullDocument, ProfileSamples.AllowList);
        Assert.True(imported.Succeeded);
        var prototype = Assert.Single(imported.Value!);
        var profiles = Enumerable.Range(0, ProfileSchema.MaxProfiles)
            .Select(i => prototype with
            {
                Id = "p" + i.ToString("D63", System.Globalization.CultureInfo.InvariantCulture),
                // Beschreibung zunächst mit einem Zeichen; die Obergrenze unten rechnet das Wachstum bis zur Höchstlänge ein.
                Description = "a",
                // S25-Fixture-Familie (P2-Konzept, Kapitel 7): weitere gültige v1-Felder mit Höchstlänge.
                Name = new string('n', ProfileSchema.MaxNameLength),
                Recognition = prototype.Recognition with
                {
                    Endpoint = new Uri("https://stt.example.com/v1/" + new string('a', 1500 - 27)),
                },
                Transform = prototype.Transform with
                {
                    Endpoint = new Uri("http://127.0.0.1:1234/" + new string('a', 1500 - 22)),
                },
                AudioSource = AudioSourceSelection.Device(
                    new string('d', ProfileSchema.MaxDeviceIdLength), new string('h', ProfileSchema.MaxNameLength)),
            }).ToArray();
        var canonicalV1 = ProfileSerializer.Export(profiles);
        var withMode = canonicalV1.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2",
            StringComparison.Ordinal).Replace("\"language\": \"de-CH\",",
            "\"language\": \"de-CH\",\n      \"recognitionMode\": \"live\",", StringComparison.Ordinal);

        // Eine UTF-16-Einheit kann in JSON höchstens sechs Zeichen belegen (\uXXXX).
        // Selbst diese grosszügige Obergrenze lässt jede Beschreibung von einem Zeichen auf die Höchstlänge wachsen.
        // Die 64 Ids sind bereits maximal lang; S25 variiert sonst nur Beschreibung, wie oben ergänzt.
        int upperBoundV2 = withMode.Length
            + (6 * ProfileSchema.MaxDescriptionLength - 1) * ProfileSchema.MaxProfiles;
        const int documentLimit = 256 * 1024; // DataSafety.MaxDocumentLength, bestehender Vertrag
        Assert.True(upperBoundV2 >= documentLimit,
            $"S25 kann mit dieser Familie die Grenze nicht erreichen: V1={canonicalV1.Length}, "
            + $"V2={withMode.Length}, Obergrenze V2={upperBoundV2}, Grenze={documentLimit}.");
    }
}
