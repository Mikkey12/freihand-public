using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Segments;
using Freihand.Core.Tests.Platform.Fakes;

namespace Freihand.Core.Tests.Platform;

/// <summary>PR #34: Gegenproben zu BE-06, AU-10, AU-13 und BF-02; ausschliesslich Attrappen.</summary>
public sealed class Pr34ReviewCounterTests
{
    private static TextSegment Fixed(string text) => new(Guid.NewGuid(), text, SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

    private static BoundTargetOutput Output(ITargetBinding binding, FakeTextInjector injector) => new(
        binding, injector, new FakeHotkeyService(), TargetTable.Options() with { PartDelay = TimeSpan.Zero }, TimeProvider.System);

    [Fact]
    public async Task C01_CancelDuringTargetInspectionPreventsTheNotYetStartedInjection()
    {
        // BE-06: Die Zielprüfung läuft noch; noch kein SendInput-Teil hat begonnen.
        var identity = TargetTable.IdentityFor(TargetTable.N);
        var inspector = new FakeTargetInspector { Focus = (identity, TargetTable.FactsFor(TargetTable.N)) };
        inspector.Existing.Add(identity);
        var binding = new TargetBinding(inspector, TargetTable.Table());
        var target = binding.BindFocused(ProfileStrictness.Standard).Target!;
        var injector = new FakeTextInjector();
        using var cancel = new CancellationTokenSource();
        inspector.OnFocused = cancel.Cancel;

        var report = await Output(binding, injector).WriteAsync(Fixed("abc"), target, cancel.Token);

        Assert.True(cancel.IsCancellationRequested);
        Assert.Empty(injector.Parts);
        Assert.Equal(OutputReason.Cancelled, report.Reason);
        Assert.Equal("abc", report.Held);
        Assert.Empty(report.Written);
    }

    [Fact]
    public async Task C02_AnAlreadyCancelledSectionBlocksFollowingSectionsOfTheSameBinding()
    {
        // AU-10, Kapitel 5.7 Schritt 4: Auch ein ganz gehaltener Abschnitt darf nicht überholt werden.
        var binding = new FakeTargetBinding(TargetTable.N);
        var target = binding.BindFocused(ProfileStrictness.Standard).Target!;
        var injector = new FakeTextInjector();
        var output = Output(binding, injector);
        Assert.Equal(OutputResult.Written, (await output.WriteAsync(Fixed("a"), target)).Result);
        var cancelled = await output.WriteAsync(Fixed("b"), target, new CancellationToken(true));
        Assert.Equal(OutputReason.Cancelled, cancelled.Reason);
        Assert.Equal("b", cancelled.Held);

        var later = await output.WriteAsync(Fixed("c"), target);

        Assert.Equal(OutputReason.PreviousHeld, later.Reason);
        Assert.Equal("c", later.Held);
        Assert.Single(injector.Parts);
    }

    [Fact]
    public async Task C03_EmptyFixedSectionDoesNotEraseTheLastWrittenTextForJoining()
    {
        // AU-13: Ein leerer Abschnitt fügt nichts ein; der letzte geschriebene Text bleibt derselbe.
        var binding = new FakeTargetBinding(TargetTable.N);
        var target = binding.BindFocused(ProfileStrictness.Standard).Target!;
        var injector = new FakeTextInjector();
        var output = Output(binding, injector);
        await output.WriteAsync(Fixed("a"), target);
        await output.WriteAsync(Fixed(string.Empty), target);
        var next = await output.WriteAsync(Fixed("b"), target);

        Assert.Equal(" b", next.Written);
        Assert.Equal("a b", string.Concat(injector.Parts.Select(part => part.Text)));
    }

    [Fact]
    public void C04_UnapprovedCheckModeMustNotProduceUnprotectedRealKeys()
    {
        // AU-13, SI-05, Kapitel 10.1: Check ist nicht freigegeben; ohne Modifikatorprüfung darf es nicht senden.
        // Bis zur Freigabe ist die sichere Umsetzung dieselbe Verweigerung wie bei NotApproved.
        var plan = InjectionPlanner.Plan("\n", TargetTable.T, ControlKeySafety.Check);
        var refusal = Assert.IsType<PlanRefused>(plan);
        Assert.Equal(OutputReason.LineBreakRefused, refusal.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("42.1")]
    public void C05_UnknownProcessStartOrRuntimeIdCannotEstablishFieldIdentity(string runtimeId)
    {
        // AU-10: Null-/Fehlerersatzwerte sind kein Nachweis einer Feldidentität.
        var identity = TargetTable.IdentityFor(TargetTable.N, runtimeId) with { ProcessStartTicks = runtimeId.Length == 0 ? 1 : 0 };
        var inspector = new FakeTargetInspector { Focus = (identity, TargetTable.FactsFor(TargetTable.N)) };
        inspector.Existing.Add(identity);
        var binding = new TargetBinding(inspector, TargetTable.Table());

        var result = binding.BindFocused(ProfileStrictness.Standard);

        Assert.Null(result.Target);
        Assert.Equal(BindRefusal.IdentityUnclear, result.Refusal);
    }

    [Fact]
    public void C12_FailedGuardStartDoesNotLeaveTheGuardMarkedAsArmed()
    {
        // SI-04: Nach fehlgeschlagenem Start muss ein bewusster neuer Versuch die Signale erneut starten.
        var signals = new FailOnceSignals();
        using var guard = new RecordingGuard(signals, new GuardOptions(TimeSpan.FromMilliseconds(250)), new ManualClock());
        Assert.Throws<InvalidOperationException>(guard.Arm);
        guard.Arm();
        Assert.Equal(2, signals.Starts);
    }

    private sealed class FailOnceSignals : IGuardSignals
    {
        public int Starts { get; private set; }
        public event EventHandler<GuardReason>? SessionEvent { add { } remove { } }
        public event EventHandler<bool?>? FocusChanged { add { } remove { } }
        public bool? FocusIsPassword() => false;
        public string? InputDesktopName() => "Default";
        public void Start() { if (++Starts == 1) throw new InvalidOperationException("Testfehler"); }
        public void Stop() { }
    }
}
