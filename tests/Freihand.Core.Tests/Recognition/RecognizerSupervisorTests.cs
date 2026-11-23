using Freihand.Adapters.Recognition;
using Freihand.Core.Live;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live;
using Freihand.Core.Tests.Recognition.Kit;
using static Freihand.Core.Tests.Recognition.Kit.RecognitionTestKit;

namespace Freihand.Core.Tests.Recognition;

/// <summary>
/// Supervisor mit Launcher-Attrappe und manueller Uhr: Handshake, fremde Prozesse, Wiederanlauf, Sitzungsprüfung und
/// Moduswahl nach dem Laden (P8-Konzept, Kapitel 5.4, 5.5, 5.9; R28–R30, R32, R50). Die Kombinatorik des
/// Wiederanlaufs steht in <see cref="RestartMatrixTests"/>.
/// </summary>
public sealed class RecognizerSupervisorTests
{
    /// <summary>
    /// R28, Teil Supervisor: <c>hello</c> mit <c>protocol</c> 2 → die App antwortet mit <c>failed</c>
    /// (<c>ProtocolVersion</c>; Kapitel 4.7 lässt <c>failed</c> in Richtung App → Erkenner zu, und nur die App erkennt die
    /// fremde Version), <c>Unavailable</c> mit diesem Code, kein <c>configure</c> und kein Neustart.
    /// </summary>
    [Fact]
    public async Task R28_ProtocolVersionTwoMakesTheRecognizerUnavailableWithoutRestart()
    {
        await using var harness = SupervisorHarness.Create(_ => LaunchPlan.Scripted(async scripted =>
        {
            var decode = scripted.Connect();
            await decode.SendAsync(Messages.Hello(2, "decode"));
            await decode.ReadToEndOrBreakAsync();
        }));
        await harness.StartAsync();

        await harness.WaitAvailabilityAsync(RecognizerAvailability.Unavailable);
        Assert.Equal(RecognizerErrorCode.ProtocolVersion, harness.Supervisor.LastError);

        // Was die App gesendet hat, aus der Aufzeichnung (ein sofortiger Kill dürfte das Lesen beim Erkenner verhindern)
        var sent = (await harness.Launcher.Current.DecodeConnected).SentBy(PipeSide.App);
        var failed = Assert.Single(sent);
        Assert.Equal("failed", failed.Type);
        Assert.Equal(RecognizerErrorCode.ProtocolVersion, failed.Code);

        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        await Settle();
        Assert.Equal(1, harness.Launcher.Launches);
        Assert.Equal(RecognizerAvailability.Unavailable, harness.Supervisor.Availability);
    }

    /// <summary>
    /// R28, Teil Supervisor: <c>hello</c> auf <c>detect</c> vor <c>ready</c> (die erste Verbindung meldet sich als
    /// <c>detect</c>) ist ein Rahmenfehler nach Regel 4: die App beendet diese Verbindung ohne <c>configure</c>.
    /// </summary>
    [Fact]
    public async Task R28_HelloOnDetectBeforeReadyEndsTheConnection()
    {
        var frames = new TaskCompletionSource<IReadOnlyList<Frame>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = SupervisorHarness.Create(number => number == 1
            ? LaunchPlan.Scripted(async scripted =>
            {
                var first = scripted.Connect();
                await first.SendAsync(Messages.Hello(1, "detect"));
                frames.TrySetResult(await first.ReadToEndOrBreakAsync());
            })
            : LaunchPlan.Service());
        await harness.StartAsync();

        var received = await frames.Task.WaitAsync(Guard);
        Assert.Empty(received);
        var first = await harness.Launcher.Processes[0].DecodeConnected;
        Assert.True(first.App.IsClosed || first.Severed, "Die App hat die Verbindung nicht beendet.");
    }

    [Fact]
    public async Task R29_ThreeCrashesRestartAndTheFourthWithinFiveMinutesStops()
    {
        await using var harness = await SupervisorHarness.StartedAsync();

        // drei Abstürze in 4 Minuten: je ein sofortiger Neustart, ohne die Uhr vorzustellen
        for (var crash = 1; crash <= 3; crash++)
        {
            if (crash > 1)
            {
                harness.Clock.Advance(TimeSpan.FromMinutes(2));
            }

            harness.Launcher.Current.Crash();
            await harness.Launcher.LaunchedAsync(crash + 1);
            await harness.WaitReadyAsync(crash + 1);
            Assert.Null(harness.Supervisor.LastError);
        }

        Assert.Contains(RecognizerAvailability.Restarting, harness.Transitions);

        // der vierte innerhalb von 5 Minuten seit dem ersten (bei 4:30) → Unavailable, kein Start mehr
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        harness.Launcher.Current.Crash();
        await harness.WaitAvailabilityAsync(RecognizerAvailability.Unavailable);
        harness.Clock.Advance(TimeSpan.FromMinutes(30));
        await Settle();
        Assert.Equal(4, harness.Launcher.Launches);
        await Throws<RecognizerUnavailableException>(harness.SessionAsync());

        // erst RestartAsync startet wieder
        await harness.Supervisor.RestartAsync().WaitAsync(Guard);
        await harness.WaitReadyAsync(5);
    }

    [Fact]
    public async Task R29_CrashesSixMinutesApartAlwaysRestart()
    {
        await using var harness = await SupervisorHarness.StartedAsync();

        for (var crash = 1; crash <= 6; crash++)
        {
            harness.Clock.Advance(TimeSpan.FromMinutes(6));
            harness.Launcher.Current.Crash();
            await harness.WaitReadyAsync(crash + 1);
        }

        Assert.Equal(7, harness.Launcher.Launches);
        Assert.Null(harness.Supervisor.LastError);
    }

    [Fact]
    public async Task R29_ChecksumFailureOnLoadIsUnavailableAtOnceWithoutRestart()
    {
        await using var harness = SupervisorHarness.Create(configuration: files => files.Configuration() with { ModelSha256 = new string('0', 64) });
        await harness.StartAsync();

        await harness.WaitAvailabilityAsync(RecognizerAvailability.Unavailable);
        Assert.Equal(RecognizerErrorCode.ModelChecksum, harness.Supervisor.LastError);
        Assert.Empty(harness.Launcher.Current.Loader.Calls);
        harness.Clock.Advance(TimeSpan.FromMinutes(30));
        await Settle();
        Assert.Equal(1, harness.Launcher.Launches);
        Assert.DoesNotContain(RecognizerAvailability.Restarting, harness.Transitions);
    }

    /// <summary>
    /// R30: Eine Verbindung mit fremder Prozesskennung wird getrennt, bevor eine Nachricht gelesen wird; auf ihr geht
    /// kein <c>configure</c> (und nichts sonst) hinaus. Die fremde Seite hat vorher ein <c>hello</c> gesendet, damit jedes
    /// Lesen auffiele. Geprüft an beiden Stellen, an denen die App eine Verbindung annimmt (vor <c>decode</c>, vor
    /// <c>detect</c>). Was der Supervisor danach tut, legt Kapitel 5.4 nur als „protokolliert nur das Ereignis“ fest;
    /// das prüft der Test nicht.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R30_ForeignProcessIsDisconnectedBeforeAnyMessageIsRead(bool beforeDetect)
    {
        var position = beforeDetect ? ForeignConnection.BeforeDetect : ForeignConnection.BeforeDecode;
        await using var harness = SupervisorHarness.Create(_ => LaunchPlan.Service(foreign: position));
        await harness.StartAsync();

        var process = await harness.Launcher.LaunchedAsync(1);
        var foreign = await process.ForeignConnected.WaitAsync(Guard);
        await Until(() => foreign.App.IsClosed || foreign.Severed, "fremde Verbindung getrennt");
        Assert.Equal(0, foreign.App.ReadCalls);
        Assert.Equal(0, foreign.App.BytesRead);
        Assert.Empty(foreign.WrittenBy(PipeSide.App));
        Assert.False(await UnobservedFailureLog.SeenAsync(harness.Launcher.Marker), $"Unbeobachteter Fehler mit Kennung {harness.Launcher.Marker}.");
    }

    [Theory]
    [InlineData(25.0, 10.0)]
    [InlineData(10.0, 21.0)]
    [InlineData(20.0000001, 10.0)]
    [InlineData(10.0, 20.0000001)]
    public async Task R32_WindowOrMaxUtteranceOverTwentySecondsIsRejected(double window, double maxUtterance)
    {
        await using var harness = await SupervisorHarness.StartedAsync();

        var live = LiveDefaults() with { Window = Sec(window), MaxUtterance = Sec(maxUtterance) };
        await Throws<ArgumentOutOfRangeException>(harness.Supervisor.StartSessionAsync(German(), live));
    }

    [Theory]
    [InlineData(10.0, 10.0)]
    [InlineData(20.0, 20.0)]
    public async Task R32_WindowAndMaxUtteranceUpToTwentySecondsAreAccepted(double window, double maxUtterance)
    {
        await using var harness = await SupervisorHarness.StartedAsync();

        await using var session = await harness.SessionAsync(live: LiveDefaults() with { Window = Sec(window), MaxUtterance = Sec(maxUtterance) });
        Assert.False(session.Failed);
    }

    /// <summary>R32 und Kapitel 5.5: eine nicht abbildbare Sprache weist schon <c>StartSessionAsync</c> ab.</summary>
    [Fact]
    public async Task R32_UnsupportedLanguageIsRejectedAtSessionStart()
    {
        await using var harness = await SupervisorHarness.StartedAsync();

        var rejected = await Throws<RecognizerException>(harness.Supervisor.StartSessionAsync(new RecognitionOptions("rm", []), LiveDefaults()));
        Assert.Equal(RecognizerErrorCode.UnsupportedLanguage, rejected.Code);
    }

    /// <summary>R32: Bei <c>AudioContext</c> 600 (12 s) rechnet ein Auftrag von 12 s ohne, einer von 12,5 s mit vollem Fenster.</summary>
    [Fact]
    public async Task R32_FullContextOnlyForJobsLongerThanTheAudioContext()
    {
        await using var harness = await SupervisorHarness.StartedAsync();
        Assert.Equal(600, harness.Configuration.AudioContext);
        await using var session = await harness.SessionAsync();

        await session.Decoder.DecodeAsync(Samples(192_000), Sec(1), German()).WaitAsync(Guard);
        await session.Decoder.DecodeAsync(Samples(200_000), Sec(20), German()).WaitAsync(Guard);

        var calls = harness.Launcher.Current.Whisper.Calls;
        Assert.Equal([192_000, 200_000], calls.Select(call => call.Audio.Length));
        Assert.False(calls[0].FullContext);
        Assert.True(calls[1].FullContext);
    }

    /// <summary>
    /// R50: <c>RecognizerSession.Info.Identity</c> ist das tatsächlich geladene Modell und die tatsächliche Laufzeit;
    /// <c>Resolve</c> damit ergibt live nur für genau den Abnahmeeintrag, nach einem Rückfall auf den Prozessor satzweise
    /// mit <c>NotApproved</c>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task R50_ModeIsResolvedWithTheIdentityActuallyLoaded(bool cudaLoads)
    {
        using var files = ModelFiles.Create("ggml-large-v3-turbo.bin");
        await using var harness = await SupervisorHarness.StartedAsync(
            _ => LaunchPlan.Service(loader => loader.AvailableRuntimes = cudaLoads ? [RecognizerRuntime.Cuda, RecognizerRuntime.Cpu] : [RecognizerRuntime.Cpu]),
            files,
            model => model.Configuration(RecognizerRuntime.Cuda, RecognizerRuntime.Cpu));

        await using var session = await harness.SessionAsync();

        var expectedRuntime = cudaLoads ? "cuda" : "cpu";
        Assert.Equal(new RecognizerIdentity("ggml-large-v3-turbo.bin", expectedRuntime), session.Info.Identity);
        Assert.Equal(!cudaLoads, session.Info.RuntimeFallback);
        Assert.Equal(cudaLoads ? RecognizerRuntime.Cuda : RecognizerRuntime.Cpu, session.Info.Runtime);

        var approvals = new LiveApprovals([new LiveApproval("reference-desktop", "de-CH", "ggml-large-v3-turbo.bin", "cuda")]);
        var standard = BuiltInProfiles.Standard;
        Assert.Equal(RecognitionMode.Live, standard.RecognitionMode);
        Assert.Equal("de-CH", standard.Language);
        var decision = RecognitionModeResolver.Resolve(standard, "reference-desktop", session.Info.Identity, approvals);
        Assert.Equal(cudaLoads ? LiveMode.Live : LiveMode.Sentence, decision.Mode);
        Assert.Equal(cudaLoads ? ModeReason.ProfileChoice : ModeReason.NotApproved, decision.Reason);
    }

    [Fact]
    public void R50_RuntimeApprovalNames()
    {
        Assert.Equal("cuda", RecognizerRuntimeNames.ToApprovalName(RecognizerRuntime.Cuda));
        Assert.Equal("vulkan", RecognizerRuntimeNames.ToApprovalName(RecognizerRuntime.Vulkan));
        Assert.Equal("cpu", RecognizerRuntimeNames.ToApprovalName(RecognizerRuntime.Cpu));
        Assert.Equal(new RecognizerIdentity("ggml-small.bin", "vulkan"), new RecognizerInfo("ggml-small.bin", "ab", RecognizerRuntime.Vulkan, true, TimeSpan.Zero, "1.9.1").Identity);
    }

    /// <summary>
    /// R50: Neustart während einer Sitzung, danach <c>Cpu</c> statt <c>Cuda</c> → die Sitzung ist gescheitert;
    /// <c>DecodeAsync</c> und <c>Detect</c> werfen sofort (ohne Uhrvorlauf), und der neue Erkenner sieht keinen Aufruf.
    /// </summary>
    [Fact]
    public async Task R50_IdentityChangeAfterRestartFailsTheSession()
    {
        using var files = ModelFiles.Create("ggml-large-v3-turbo.bin");
        await using var harness = await SupervisorHarness.StartedAsync(
            number => LaunchPlan.Service(loader => loader.AvailableRuntimes = number == 1 ? [RecognizerRuntime.Cuda, RecognizerRuntime.Cpu] : [RecognizerRuntime.Cpu]),
            files,
            model => model.Configuration(RecognizerRuntime.Cuda, RecognizerRuntime.Cpu));
        await using var session = await harness.SessionAsync();
        Assert.Equal("cuda", session.Info.Identity.Runtime);

        harness.Launcher.Current.Crash();
        await harness.WaitReadyAsync(2);
        Assert.Equal(RecognizerRuntime.Cpu, harness.Supervisor.Info?.Runtime);
        await Until(() => session.Failed, "Sitzung gescheitert nach Identitätswechsel");

        await Throws<RecognizerUnavailableException>(session.Decoder.DecodeAsync(Samples(16_000), Sec(1), German()));
        await Throws<RecognizerUnavailableException>(DetectAsync(session.Detector, Samples(16_000), Sec(1)));
        Assert.Equal("cuda", session.Info.Identity.Runtime);
        Assert.Empty(harness.Launcher.Current.Whisper.Calls);
        Assert.Empty(harness.Launcher.Current.Vad.Calls);
    }
}
