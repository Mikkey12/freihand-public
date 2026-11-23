using Freihand.Adapters.Audio;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;

namespace Freihand.Core.Tests.Audio;

/// <summary>R33: Auflösung der Audioquelle und Auswahl je Profil (P8-Konzept, Kapitel 4.2 und 5.1; ER-12, ER-13).</summary>
public sealed class AudioSourceTests
{
    private static readonly AudioDevice A = new("{0.0.1.00000000}.{aaaa}", "Headset");

    /// <summary>Anderes Gerät mit demselben Anzeigenamen: gesucht wird nur über die Kennung, nie über den Namen.</summary>
    private static readonly AudioDevice B = new("{0.0.1.00000000}.{bbbb}", "Headset");

    private static readonly AudioDevice C = new("{0.0.1.00000000}.{cccc}", "Monitor-Mikrofon");

    private static readonly AudioSourceSelection ChosenA = AudioSourceSelection.Device(A.Id, "Headset");

    [Fact]
    public void R33_ChosenDeviceThatIsPresentIsResolved()
    {
        var resolution = AudioSourceResolver.Resolve(ChosenA, [B, A, C], systemDefault: B);

        Assert.Equal(new ResolvedDevice(A, IsSystemDefault: false), resolution);
    }

    [Fact]
    public void R33_ChosenDeviceIsNotTheSystemDefaultEvenWhenWindowsUsesIt()
    {
        // IsSystemDefault heisst: gewählt war das Standardgerät, nicht „das Gerät ist zufällig Standard“
        var resolution = AudioSourceResolver.Resolve(ChosenA, [A, C], systemDefault: A);

        Assert.Equal(new ResolvedDevice(A, IsSystemDefault: false), resolution);
    }

    [Fact]
    public void R33_MissingChosenDeviceIsUnavailableAndNeverTheDefault()
    {
        // B ist Standard und heisst sogar gleich wie A; trotzdem kein Ausweichen (ER-13)
        var resolution = AudioSourceResolver.Resolve(ChosenA, [B, C], systemDefault: B);

        Assert.Equal(new SourceUnavailable(CaptureStopReason.DeviceMissing, "Headset"), resolution);
    }

    [Fact]
    public void R33_MissingChosenDeviceWithoutAnyDeviceIsUnavailable()
    {
        var resolution = AudioSourceResolver.Resolve(ChosenA, [], systemDefault: null);

        Assert.Equal(new SourceUnavailable(CaptureStopReason.DeviceMissing, "Headset"), resolution);
    }

    [Fact]
    public void R33_SystemDefaultResolvesToTheCurrentDefault()
    {
        var resolution = AudioSourceResolver.Resolve(AudioSourceSelection.SystemDefault, [A, B, C], systemDefault: B);

        Assert.Equal(new ResolvedDevice(B, IsSystemDefault: true), resolution);
    }

    [Fact]
    public void R33_SystemDefaultWithoutDefaultIsUnavailableWithoutName()
    {
        var resolution = AudioSourceResolver.Resolve(AudioSourceSelection.SystemDefault, [A, C], systemDefault: null);

        Assert.Equal(new SourceUnavailable(CaptureStopReason.DeviceMissing, null), resolution);
    }

    [Fact]
    public void R33_OverrideForTheProfileGoesBeforeTheProfile()
    {
        var standard = BuiltInProfiles.Standard;
        Assert.Equal(AudioSourceSelection.SystemDefault, standard.AudioSource);
        var overrides = new AudioSourceOverrides(new Dictionary<string, AudioSourceSelection> { ["standard"] = ChosenA });

        var effective = overrides.For(standard);

        Assert.Equal(ChosenA, effective);
        Assert.Equal(new ResolvedDevice(A, IsSystemDefault: false), AudioSourceResolver.Resolve(effective, [A, B], systemDefault: B));

        // Das Profil selbst bleibt unverändert (BA-21)
        Assert.Equal(AudioSourceSelection.SystemDefault, BuiltInProfiles.Standard.AudioSource);
    }

    [Fact]
    public void R33_OverrideForAnotherIdHasNoEffect()
    {
        var overrides = new AudioSourceOverrides(new Dictionary<string, AudioSourceSelection> { ["unbekannt"] = ChosenA });

        Assert.Equal(AudioSourceSelection.SystemDefault, overrides.For(BuiltInProfiles.Standard));
        Assert.Equal(AudioSourceSelection.SystemDefault, overrides.For(BuiltInProfiles.Strict));
    }

    [Fact]
    public void R33_WithoutOverridesTheProfileApplies()
    {
        var chosen = AudioSourceSelection.Device(C.Id, "Monitor-Mikrofon");
        var profile = BuiltInProfiles.Standard with { Id = "eigen", AudioSource = chosen };
        var overrides = new AudioSourceOverrides(new Dictionary<string, AudioSourceSelection>());

        Assert.Equal(chosen, overrides.For(profile));
    }
}
