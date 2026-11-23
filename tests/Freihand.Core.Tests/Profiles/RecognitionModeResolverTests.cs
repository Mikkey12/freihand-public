using Freihand.Core.Live;
using Freihand.Core.Profiles;
using Freihand.Core.Routing;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Der wirksame Erkennungsmodus (Nachtrag P2, S16 bis S18): das Profil wünscht, die Live-Abnahme je Gerät,
/// Sprache, Modell und Laufzeit entscheidet, und jede fehlende Angabe führt zu satzweise (ER-19, ER-20).
/// </summary>
public class RecognitionModeResolverTests
{
    private sealed record ResolveCase(
        Profile Profile,
        string? DeviceId,
        RecognizerIdentity? Recognizer,
        LiveApprovals Approvals,
        LiveMode Mode,
        ModeReason Reason,
        bool Overrides);

    private static readonly RecognitionStep OnlineRecognition =
        new(RecognitionRoute.Online, new Uri("https://stt.example.com/v1/listen"));

    private static Profile Standard => BuiltInProfiles.Standard;

    private static ResolveCase Case(
        LiveMode mode,
        ModeReason reason,
        bool overrides,
        Profile? profile = null,
        string? deviceId = KnownDevices.ReferenceDesktop,
        RecognizerIdentity? recognizer = null,
        LiveApprovals? approvals = null,
        bool noRecognizer = false) =>
        new(profile ?? Standard, deviceId, noRecognizer ? null : recognizer ?? CudaRecognizer, approvals ?? LiveApprovals.Default, mode, reason, overrides);

    /// <summary>S16 (a) bis (u); das letzte Argument von <c>Case</c> ist die Erwartung von S18 (<c>OverridesProfile</c>).</summary>
    private static readonly Dictionary<string, ResolveCase> Cases = new()
    {
        ["a-profile-sentence"] = Case(LiveMode.Sentence, ModeReason.ProfileChoice, false,
            profile: Standard with { RecognitionMode = RecognitionMode.Sentence }),
        ["b-private-pc"] = Case(LiveMode.Live, ModeReason.ProfileChoice, false),
        ["c-other-device"] = Case(LiveMode.Sentence, ModeReason.NotApproved, true, deviceId: KnownDevices.SecondaryNotebook),
        ["d-swiss-german"] = Case(LiveMode.Sentence, ModeReason.NotApproved, true, profile: Standard with { Language = "gsw-CH" }),
        ["e-german-germany"] = Case(LiveMode.Sentence, ModeReason.NotApproved, true, profile: Standard with { Language = "de-DE" }),
        ["f-device-unknown"] = Case(LiveMode.Sentence, ModeReason.DeviceUnknown, true, deviceId: null),
        ["g-online-route"] = Case(LiveMode.Sentence, ModeReason.RouteNotApproved, true,
            profile: Standard with { Recognition = OnlineRecognition }),
        ["h-no-approvals"] = Case(LiveMode.Sentence, ModeReason.NotApproved, true, approvals: LiveApprovals.None),
        ["i-approved-notebook"] = Case(LiveMode.Live, ModeReason.ProfileChoice, false,
            deviceId: KnownDevices.ReferenceNotebook,
            approvals: new LiveApprovals([new LiveApproval(KnownDevices.ReferenceNotebook, "de-CH", ApprovedModel, KnownRuntimes.Cuda)])),
        ["j-device-case-differs"] = Case(LiveMode.Sentence, ModeReason.NotApproved, true, deviceId: "Referenzrechner"),
        ["k-sentence-online"] = Case(LiveMode.Sentence, ModeReason.ProfileChoice, false,
            profile: Standard with { RecognitionMode = RecognitionMode.Sentence, Recognition = OnlineRecognition }),
        ["l-recognizer-unknown"] = Case(LiveMode.Sentence, ModeReason.RecognizerUnknown, true, noRecognizer: true),
        ["m-fallback-to-cpu"] = Case(LiveMode.Sentence, ModeReason.NotApproved, true,
            recognizer: new RecognizerIdentity(ApprovedModel, KnownRuntimes.Cpu)),
        ["n-vulkan"] = Case(LiveMode.Sentence, ModeReason.NotApproved, true,
            recognizer: new RecognizerIdentity(ApprovedModel, KnownRuntimes.Vulkan)),
        ["o-other-model"] = Case(LiveMode.Sentence, ModeReason.NotApproved, true,
            recognizer: new RecognizerIdentity(OtherModel, KnownRuntimes.Cuda)),
        ["p-runtime-upper-case"] = Case(LiveMode.Sentence, ModeReason.NotApproved, true,
            recognizer: new RecognizerIdentity(ApprovedModel, "CUDA")),
        ["q-cpu-approved-too"] = Case(LiveMode.Live, ModeReason.ProfileChoice, false,
            recognizer: new RecognizerIdentity(ApprovedModel, KnownRuntimes.Cpu),
            approvals: new LiveApprovals(
            [
                .. LiveApprovals.Default.Entries,
                new LiveApproval(KnownDevices.ReferenceDesktop, "de-CH", ApprovedModel, KnownRuntimes.Cpu),
            ])),

        // Reihenfolge bei unbekanntem Erkenner (recognizer null)
        ["r-sentence-no-recognizer"] = Case(LiveMode.Sentence, ModeReason.ProfileChoice, false,
            profile: Standard with { RecognitionMode = RecognitionMode.Sentence }, noRecognizer: true),
        ["s-online-no-recognizer"] = Case(LiveMode.Sentence, ModeReason.RouteNotApproved, true,
            profile: Standard with { Recognition = OnlineRecognition }, noRecognizer: true),
        ["t-device-unknown-no-recognizer"] = Case(LiveMode.Sentence, ModeReason.DeviceUnknown, true, deviceId: null, noRecognizer: true),
        ["u-other-device-no-recognizer"] = Case(LiveMode.Sentence, ModeReason.RecognizerUnknown, true,
            deviceId: KnownDevices.SecondaryNotebook, noRecognizer: true),
    };

    public static TheoryData<string> CaseNames => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void S16_ResolveFollowsTheFixedOrder(string name)
    {
        var expected = Cases[name];

        var decision = RecognitionModeResolver.Resolve(expected.Profile, expected.DeviceId, expected.Recognizer, expected.Approvals);

        Assert.Equal(expected.Mode, decision.Mode);
        Assert.Equal(expected.Reason, decision.Reason);
        Assert.Equal(expected.Profile.RecognitionMode, decision.Requested);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void S18_OverridesProfileIsTrueExactlyWhereLiveWasRequestedButSentenceRuns(string name)
    {
        var expected = Cases[name];

        var decision = RecognitionModeResolver.Resolve(expected.Profile, expected.DeviceId, expected.Recognizer, expected.Approvals);

        Assert.Equal(expected.Overrides, decision.OverridesProfile);
    }

    [Fact]
    public void S18_TheExpectedOverridesFollowTheConcept()
    {
        // Kapitel 7, S18: wahr in (c) bis (h), (j), (l) bis (p) und (s) bis (u); falsch in (a), (b), (i), (k), (q), (r).
        var overriding = new HashSet<char>("cdefghjlmnopstu");
        var notOverriding = new HashSet<char>("abikqr");

        Assert.Equal(Cases.Count, overriding.Count + notOverriding.Count);
        foreach (var (name, expected) in Cases)
        {
            Assert.Equal(overriding.Contains(name[0]), expected.Overrides);
        }
    }

    [Fact]
    public void S16_WrongRuntimeCasingInTheIdentityNeverThrows()
    {
        var decision = RecognitionModeResolver.Resolve(Standard, KnownDevices.ReferenceDesktop, new RecognizerIdentity(ApprovedModel, "CUDA"), LiveApprovals.Default);

        Assert.Equal(ModeReason.NotApproved, decision.Reason);
        Assert.Throws<ArgumentException>(() => new LiveApprovals([new LiveApproval(KnownDevices.ReferenceDesktop, "de-CH", ApprovedModel, "CUDA")]));
    }

    [Fact]
    public void S16_ResolveRejectsNullProfileAndNullApprovals()
    {
        Assert.Throws<ArgumentNullException>(() => RecognitionModeResolver.Resolve(null!, KnownDevices.ReferenceDesktop, CudaRecognizer, LiveApprovals.Default));
        Assert.Throws<ArgumentNullException>(() => RecognitionModeResolver.Resolve(Standard, KnownDevices.ReferenceDesktop, CudaRecognizer, null!));
    }

    [Fact]
    public void S17_T44_TheProfileChoiceSurvivesExportImportAndAppliesFromTheNextDictation()
    {
        var profile = Standard with { Id = "diktat-satzweise", RecognitionMode = RecognitionMode.Sentence };

        // (1) Export, Import, erneuter Export und Import: der Wunsch bleibt satzweise.
        var catalog = new ProfileCatalog();
        Assert.True(catalog.Import(ProfileSerializer.Export([profile]), AllowList).Succeeded);
        var newCatalog = new ProfileCatalog();
        var reimported = newCatalog.Import(catalog.ExportImported(), AllowList);
        Assert.True(reimported.Succeeded, string.Join("\n", reimported.Errors));
        var loaded = newCatalog.Find("diktat-satzweise")!;
        Assert.Equal(RecognitionMode.Sentence, loaded.RecognitionMode);
        var first = RecognitionModeResolver.Resolve(loaded, KnownDevices.ReferenceDesktop, CudaRecognizer, LiveApprovals.Default);
        Assert.Equal(LiveMode.Sentence, first.Mode);
        Assert.Equal(ModeReason.ProfileChoice, first.Reason);

        // (2) Die festgehaltene Entscheidung (Diktatstart) bleibt; erst ein neuer Aufruf sieht den Wechsel.
        var held = first;
        var replaced = newCatalog.Replace(loaded with { RecognitionMode = RecognitionMode.Live }, AllowList);
        Assert.True(replaced.Succeeded, string.Join("\n", replaced.Errors));
        Assert.Equal(LiveMode.Sentence, held.Mode);
        var next = RecognitionModeResolver.Resolve(newCatalog.Find("diktat-satzweise")!, KnownDevices.ReferenceDesktop, CudaRecognizer, LiveApprovals.Default);
        Assert.Equal(LiveMode.Live, next.Mode);
    }

    [Fact]
    public void S17_T44_AVersion1ProfileWithoutTheFieldIsLiveWhereApprovedAndSentenceElsewhere()
    {
        var imported = ProfileSerializer.Import(BuiltInJsonVersion1, EndpointAllowList.None);
        Assert.True(imported.Succeeded, string.Join("\n", imported.Errors));
        var standard = imported.Value.Single(profile => profile.Id == "standard");

        Assert.Equal(RecognitionMode.Live, standard.RecognitionMode);
        Assert.Equal(LiveMode.Live, RecognitionModeResolver.Resolve(standard, KnownDevices.ReferenceDesktop, CudaRecognizer, LiveApprovals.Default).Mode);

        var elsewhere = RecognitionModeResolver.Resolve(standard, KnownDevices.ReferenceNotebook, CudaRecognizer, LiveApprovals.Default);
        Assert.Equal(LiveMode.Sentence, elsewhere.Mode);
        Assert.Equal(ModeReason.NotApproved, elsewhere.Reason);
        Assert.True(elsewhere.OverridesProfile);
    }

    [Fact]
    public void S17_T44_AFallbackFromCudaToTheProcessorRunsSentenceWise()
    {
        var onCpu = RecognitionModeResolver.Resolve(Standard, KnownDevices.ReferenceDesktop, new RecognizerIdentity(ApprovedModel, "cpu"), LiveApprovals.Default);
        Assert.Equal(LiveMode.Sentence, onCpu.Mode);
        Assert.Equal(ModeReason.NotApproved, onCpu.Reason);
        Assert.True(onCpu.OverridesProfile);

        var onCuda = RecognitionModeResolver.Resolve(Standard, KnownDevices.ReferenceDesktop, new RecognizerIdentity(ApprovedModel, "cuda"), LiveApprovals.Default);
        Assert.Equal(LiveMode.Live, onCuda.Mode);

        var notReady = RecognitionModeResolver.Resolve(Standard, KnownDevices.ReferenceDesktop, null, LiveApprovals.Default);
        Assert.Equal(LiveMode.Sentence, notReady.Mode);
        Assert.Equal(ModeReason.RecognizerUnknown, notReady.Reason);
    }
}
