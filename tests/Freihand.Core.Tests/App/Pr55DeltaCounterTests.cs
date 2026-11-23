using Freihand.Adapters.Dictation;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Routing;
using Freihand.Core.Segments;
using Freihand.Core.Tests.Platform.Fakes;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Deltagegenproben der Codex-Nachprüfung zu PR #55 am Stand bdb2df1, als feste Regressionstests übernommen: Unterbrechung
/// zwischen zwei Teilen beim Ersetzen, Feld beim Tastendruck trotz hängender früherer Abfrage. Kein Desktop, kein Netz.
/// </summary>
public sealed class Pr55DeltaCounterTests
{
    [Fact]
    public async Task AConditionalWriteMustStopAfterFocusLeftAndReturnedBetweenParts()
    {
        var original = FakeBinding.NewMark();
        var rule = original.Rule with { MaxPartLength = 1 };
        var inspector = new FakeTargetInspector();
        var facts = new TargetFacts(rule.ProcessName, "Document", "TestEdit", "", false, false, true, true, false, false);
        var same = (original.Identity, facts);
        inspector.Focus = same;
        inspector.Existing.Add(original.Identity);
        var binding = new TargetBinding(inspector, new TargetRuleTable([rule]));
        var target = binding.BindFocused(ProfileStrictness.Standard).Target!;
        var clock = new ManualClock();
        var injector = new FakeTextInjector();
        var output = new BoundTargetOutput(binding, injector, new FakeHotkeyService(),
            new OutputOptions(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10), ControlKeySafety.NotApproved), clock);
        var text = new TextSegment(Guid.NewGuid(), "ab", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.Zero);
        var work = output.WriteAsync(text, target, () => true);
        Assert.Single(injector.Parts);
        Assert.True(clock.ActiveTimers > 0);
        // Beide Fokusereignisse werden tatsächlich zugestellt. Rückkehr etwa per Klick an eine andere Schreibstelle.
        inspector.MoveFocus((original.Identity with { RuntimeId = "ANDERES-FELD" }, facts));
        inspector.MoveFocus(same);
        for (var n = 0; n < 20 && !work.IsCompleted; n++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            await Task.Delay(1);
        }
        var report = await work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(injector.Parts);
        Assert.Equal(OutputResult.HeldInDictationWindow, report.Result);
        Assert.Equal("b", report.Held);
    }

    [Theory]
    [InlineData(HotkeyAction.Correct)]
    [InlineData(HotkeyAction.Smooth)]
    [InlineData(HotkeyAction.DictationWindow)]
    public async Task ADelayedCaptureMustNeverUseTheFieldFocusedAfterTheKey(HotkeyAction action)
    {
        var log = new EventLog();
        var binding = new FakeBinding(log) { NextMark = new MarkResult(FakeBinding.NewMark("Feld A"), null) };
        using var reader = new SlowReader();
        var hotkeys = new FakeHotkeys(log);
        await using var controller = new DictationController(new FakeRecognizerHost(), new FakeDictationFactory(log),
            new FakeAudioCapture(log), binding, new FakeTextOutput(log), new FakeGuard(log), hotkeys, new FakeClipboard(log),
            ControllerRig.Options(ProfileStrictness.Standard), marker: binding, foreign: reader);
        controller.RegisterHotkeys();
        var captured = new TaskCompletionSource<TargetMark?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        controller.CorrectionRequested += (_, request) =>
        {
            if (Interlocked.Increment(ref requests) == 2) captured.TrySetResult(request.Mark.Mark);
        };
        controller.DictationWindowRequested += (_, request) => captured.TrySetResult(request.Mark.Mark);
        hotkeys.Press(HotkeyAction.Correct);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // Die erste UIA-Abfrage hängt. Die zweite Taste wird eindeutig in Feld B gedrückt.
            binding.NextMark = new MarkResult(FakeBinding.NewMark("Feld B"), null);
            reader.Current = "B";
            hotkeys.Press(action);
            // Vor dem Abarbeiten der eigenen Erfassungsreihe wechselt der Fokus weiter nach C.
            binding.NextMark = new MarkResult(FakeBinding.NewMark("Feld C"), null);
            reader.Current = "C";
        }
        finally { reader.Release.Set(); }
        var mark = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(mark is null || mark.Display == "Feld B", "Nach Fokuswechsel darf kein anderes Feld als B erfasst werden; Abweisung wäre sicher.");
    }

    private sealed class SlowReader : IForeignContentReader, IDisposable
    {
        private int _reads;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public string Current { get; set; } = "A";
        public ForeignText? ReadFocused(ReadPurpose purpose) => null;
        public SelectionRead ReadSelection(ReadPurpose purpose)
        {
            var current = Current;
            if (Interlocked.Increment(ref _reads) == 1)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            return SelectionText.From(current, purpose, DataClass.Confidential);
        }
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }
}
