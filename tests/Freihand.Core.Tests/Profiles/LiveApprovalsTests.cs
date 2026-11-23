using Freihand.Core.Profiles;
using static Freihand.Core.Tests.Profiles.ProfileSamples;

namespace Freihand.Core.Tests.Profiles;

/// <summary>
/// Einstellung, wo live abgenommen ist (Nachtrag P2, S15, S22, S23): je Gerät, Sprache, Modell und Laufzeit,
/// geprüft beim Erstellen, unveränderlich (ER-19).
/// </summary>
public class LiveApprovalsTests
{
    private static LiveApproval Entry(
        string device = KnownDevices.ReferenceDesktop,
        string language = "de-CH",
        string model = ApprovedModel,
        string runtime = KnownRuntimes.Cuda) =>
        new(device, language, model, runtime);

    [Fact]
    public void S15_DefaultIsOnlyTheReferenceDesktopWithHighGermanOnTheGraphicsCard()
    {
        Assert.Equal([new LiveApproval("reference-desktop", "de-CH", "ggml-large-v3-turbo.bin", "cuda")], LiveApprovals.Default.Entries);
        Assert.Equal(ApprovedModel, LiveApprovals.Default.Entries[0].Model);
        Assert.Empty(LiveApprovals.None.Entries);
    }

    public static TheoryData<string> InvalidDevices => ["Test Desktop", "", new string('a', 65)];

    public static TheoryData<string> InvalidLanguages => ["Deutsch", "de_CH"];

    public static TheoryData<string> InvalidModels =>
    [
        "",
        ".bin",
        "-modell.bin",
        "Modell.bin",
        "modell",
        "modell.gguf",
        "modell.BIN",
        "mo dell.bin",
        "../modell.bin",
        new string('a', 125) + ".bin",
    ];

    public static TheoryData<string> InvalidRuntimes => ["CUDA", "Cuda", "", "gpu", "cuda "];

    [Theory]
    [MemberData(nameof(InvalidDevices))]
    public void S15_InvalidDeviceIdsAreRejected(string device) =>
        Assert.Throws<ArgumentException>(() => new LiveApprovals([Entry(device: device)]));

    [Theory]
    [MemberData(nameof(InvalidLanguages))]
    public void S15_InvalidLanguagesAreRejected(string language) =>
        Assert.Throws<ArgumentException>(() => new LiveApprovals([Entry(language: language)]));

    [Theory]
    [MemberData(nameof(InvalidModels))]
    public void S15_InvalidModelsAreRejected(string model) =>
        Assert.Throws<ArgumentException>(() => new LiveApprovals([Entry(model: model)]));

    [Theory]
    [MemberData(nameof(InvalidRuntimes))]
    public void S15_InvalidRuntimesAreRejected(string runtime) =>
        Assert.Throws<ArgumentException>(() => new LiveApprovals([Entry(runtime: runtime)]));

    [Fact]
    public void S15_TheSameFourPartsTwiceAreRejected() =>
        Assert.Throws<ArgumentException>(() => new LiveApprovals([Entry(), Entry()]));

    [Fact]
    public void S15_NullIsRejectedWithArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new LiveApprovals(null!));
        Assert.Throws<ArgumentNullException>(() => new LiveApprovals([null!]));
        Assert.Throws<ArgumentNullException>(() => new LiveApprovals([new LiveApproval(null!, "de-CH", ApprovedModel, "cuda")]));
        Assert.Throws<ArgumentNullException>(() => new LiveApprovals([new LiveApproval("reference-desktop", null!, ApprovedModel, "cuda")]));
        Assert.Throws<ArgumentNullException>(() => new LiveApprovals([new LiveApproval("reference-desktop", "de-CH", null!, "cuda")]));
        Assert.Throws<ArgumentNullException>(() => new LiveApprovals([new LiveApproval("reference-desktop", "de-CH", ApprovedModel, null!)]));
    }

    [Fact]
    public void S15_ValidSettingsAreAccepted()
    {
        Assert.Empty(new LiveApprovals([]).Entries);

        var twoDevices = new LiveApprovals([Entry(KnownDevices.ReferenceNotebook), Entry(language: "gsw-CH")]);
        Assert.Equal(2, twoDevices.Entries.Count);

        Assert.Single(new LiveApprovals([Entry(model: new string('a', 124) + ".bin")]).Entries);
        Assert.Equal(LiveApprovals.MaxModelLength, (new string('a', 124) + ".bin").Length);
        Assert.Single(new LiveApprovals([Entry(model: "a.bin")]).Entries);
        Assert.Single(new LiveApprovals([Entry(model: "ggml-x_1.2-y.bin")]).Entries);

        foreach (var runtime in new[] { KnownRuntimes.Cuda, KnownRuntimes.Vulkan, KnownRuntimes.Cpu })
        {
            Assert.Single(new LiveApprovals([Entry(runtime: runtime)]).Entries);
        }

        // dasselbe Gerät, dieselbe Sprache und dasselbe Modell auf zwei Laufzeiten
        Assert.Equal(2, new LiveApprovals([Entry(runtime: "cuda"), Entry(runtime: "cpu")]).Entries.Count);

        // dasselbe Gerät, dieselbe Sprache und dieselbe Laufzeit mit zwei Modellen
        Assert.Equal(2, new LiveApprovals([Entry(), Entry(model: OtherModel)]).Entries.Count);
    }

    [Fact]
    public void S15_CoversComparesAllFourPartsExactly()
    {
        var approvals = LiveApprovals.Default;

        Assert.True(approvals.Covers("reference-desktop", "de-CH", CudaRecognizer));
        Assert.False(approvals.Covers("reference-desktop", "de-DE", CudaRecognizer));
        Assert.False(approvals.Covers("reference-desktop", "de", CudaRecognizer));
        Assert.False(approvals.Covers("secondary-notebook", "de-CH", CudaRecognizer));
        Assert.False(approvals.Covers("reference-desktop", "de-CH", new RecognizerIdentity(ApprovedModel, "cpu")));
        Assert.False(approvals.Covers("reference-desktop", "de-CH", new RecognizerIdentity(OtherModel, "cuda")));
        Assert.Throws<ArgumentNullException>(() => approvals.Covers(null!, "de-CH", CudaRecognizer));
        Assert.Throws<ArgumentNullException>(() => approvals.Covers("reference-desktop", null!, CudaRecognizer));
        Assert.Throws<ArgumentNullException>(() => approvals.Covers("reference-desktop", "de-CH", null!));
    }

    [Fact]
    public void S22_ApprovalsCopyTheirInputList()
    {
        var source = new List<LiveApproval> { Entry() };
        var approvals = new LiveApprovals(source);

        source.Add(Entry(KnownDevices.SecondaryNotebook));
        source[0] = Entry(language: "gsw-CH");
        source.Clear();

        Assert.Equal([Entry()], approvals.Entries);
        Assert.True(approvals.Covers("reference-desktop", "de-CH", CudaRecognizer));
        Assert.Equal(
            Freihand.Core.Live.LiveMode.Live,
            RecognitionModeResolver.Resolve(BuiltInProfiles.Standard, "reference-desktop", CudaRecognizer, approvals).Mode);

        var asList = approvals.Entries as IList<LiveApproval>;
        Assert.True(asList is null || asList.IsReadOnly);
        if (asList is not null)
        {
            Assert.Throws<NotSupportedException>(() => asList.Add(Entry(KnownDevices.SecondaryNotebook)));
        }
    }

    [Fact]
    public void S23_OneDeviceCanBeApprovedForTwoLanguages()
    {
        var approvals = new LiveApprovals([Entry(), Entry(language: "gsw-CH")]);
        var standard = BuiltInProfiles.Standard;

        Assert.Equal(["de-CH", "gsw-CH"], approvals.Entries.Select(entry => entry.Language));
        Assert.Equal(
            Freihand.Core.Live.LiveMode.Live,
            RecognitionModeResolver.Resolve(standard, "reference-desktop", CudaRecognizer, approvals).Mode);
        Assert.Equal(
            Freihand.Core.Live.LiveMode.Live,
            RecognitionModeResolver.Resolve(standard with { Language = "gsw-CH" }, "reference-desktop", CudaRecognizer, approvals).Mode);

        var german = RecognitionModeResolver.Resolve(standard with { Language = "de-DE" }, "reference-desktop", CudaRecognizer, approvals);
        Assert.Equal(Freihand.Core.Live.LiveMode.Sentence, german.Mode);
        Assert.Equal(ModeReason.NotApproved, german.Reason);

        // Fehlt einer der beiden Einträge, ist nur die zugehörige Sprache nicht abgenommen.
        var onlySwiss = new LiveApprovals([Entry(language: "gsw-CH")]);
        var withoutHighGerman = RecognitionModeResolver.Resolve(standard, "reference-desktop", CudaRecognizer, onlySwiss);
        Assert.Equal(ModeReason.NotApproved, withoutHighGerman.Reason);
        Assert.Equal(
            Freihand.Core.Live.LiveMode.Live,
            RecognitionModeResolver.Resolve(standard with { Language = "gsw-CH" }, "reference-desktop", CudaRecognizer, onlySwiss).Mode);

        var onlyHighGerman = new LiveApprovals([Entry()]);
        Assert.Equal(
            Freihand.Core.Live.LiveMode.Sentence,
            RecognitionModeResolver.Resolve(standard with { Language = "gsw-CH" }, "reference-desktop", CudaRecognizer, onlyHighGerman).Mode);
        Assert.Equal(
            Freihand.Core.Live.LiveMode.Live,
            RecognitionModeResolver.Resolve(standard, "reference-desktop", CudaRecognizer, onlyHighGerman).Mode);
    }
}
