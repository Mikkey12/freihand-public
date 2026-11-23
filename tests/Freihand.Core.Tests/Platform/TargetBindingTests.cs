using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Tests.Platform.Fakes;

namespace Freihand.Core.Tests.Platform;

/// <summary>Zielentscheid und Zielbindung aus dem P7-Konzept, Kapitel 4.2 und 5.4; Abnahmetests W07 und W08.</summary>
public sealed class TargetBindingTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static TargetRule N => TargetTable.N;

    private static TargetRule T => TargetTable.T;

    private static TargetRule S => TargetTable.S;

    // ---------------------------------------------------------------- W07: Zielentscheid

    private static TargetDecision Decide(TargetFacts facts, ProfileStrictness strictness = ProfileStrictness.Standard) =>
        TargetTable.Table().Decide(facts, strictness);

    [Fact]
    public void W07_DecideRefusesOwnProcess()
    {
        var facts = TargetTable.FactsFor(N) with { IsOwnProcess = true };

        Assert.Equal(new NotBindable(BindRefusal.OwnProcess), Decide(facts));
    }

    // Entscheid vom 11.11.2026 (SI-04): Gesperrt wird nur, was das Feld nachweislich meldet; nicht Lesbares sperrt nicht
    [Fact]
    public void W07_DecideRefusesAnElevatedWindow()
    {
        var facts = TargetTable.FactsFor(N) with { IsElevated = true };

        Assert.Equal(new NotBindable(BindRefusal.Elevated), Decide(facts));
    }

    [Fact]
    public void W07_DecideRefusesAFieldThatReportsItIsAPasswordField()
    {
        var facts = TargetTable.FactsFor(N) with { IsPassword = true };

        Assert.Equal(new NotBindable(BindRefusal.PasswordField), Decide(facts));
    }

    [Theory]
    [InlineData(null, false, true, true)]
    [InlineData(false, null, true, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, null, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, null)]
    public void W07_DecideBindsUnreadableReadOnlyOrDisabledFields(bool? isElevated, bool? isPassword, bool? isReadOnly, bool? isEnabled)
    {
        var facts = TargetTable.FactsFor(N) with
        {
            IsElevated = isElevated, IsPassword = isPassword, IsReadOnly = isReadOnly, IsEnabled = isEnabled,
        };

        Assert.IsType<Bindable>(Decide(facts));
    }

    [Fact]
    public void W07_DecideBindsTheMatchingRowAsKnown()
    {
        var decision = Decide(TargetTable.FactsFor(N));

        Assert.Equal(new Bindable(N, true), decision);
    }

    [Fact]
    public void W07_DecideRejectsUnknownProcessInStrictProfile()
    {
        var facts = TargetTable.FactsFor(N) with { ProcessName = "wordpad" };

        Assert.Equal(new NotBindable(BindRefusal.UnknownFieldStrict), Decide(facts, ProfileStrictness.Strict));
    }

    [Fact]
    public void W07_DecideBindsUnknownProcessInStandardProfileWithUnknownFieldRule()
    {
        var facts = TargetTable.FactsFor(N) with { ProcessName = "wordpad" };

        var bindable = Assert.IsType<Bindable>(Decide(facts, ProfileStrictness.Standard));

        Assert.False(bindable.Known);
        Assert.Equal(FieldKinds.Unknown, bindable.Rule.FieldKind);
        Assert.Equal("wordpad – unbekanntes Feld", bindable.Rule.Display);
        Assert.Equal(LineBreakMode.Refuse, bindable.Rule.LineBreak);
        Assert.Equal(1, bindable.Rule.MaxPartLength);
        Assert.Equal(IdentityMode.RuntimeId, bindable.Rule.Identity);
    }

    [Fact]
    public void W07_UnknownFieldDisplayNeverContainsTheClassName()
    {
        // Die Anzeige kommt nur aus Tabelle bzw. Prozessname, nie aus Eigenschaften des Elements (SI-12)
        var facts = TargetTable.FactsFor(N) with { ProcessName = "wordpad", ClassName = "GEHEIM-4711" };

        var bindable = Assert.IsType<Bindable>(Decide(facts, ProfileStrictness.Standard));

        Assert.DoesNotContain("GEHEIM-4711", bindable.Rule.Display);
    }

    [Theory]
    [InlineData(FieldKinds.ChatInput)]
    [InlineData(FieldKinds.Subject)]
    [InlineData(FieldKinds.SingleLine)]
    [InlineData(FieldKinds.Unknown)]
    public void W07_TableRejectsEnterWhereEnterCouldSend(string fieldKind)
    {
        var rule = N with { FieldKind = fieldKind, LineBreak = LineBreakMode.Enter };

        Assert.Throws<ArgumentException>(() =>
        {
            _ = new TargetRuleTable([rule]);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void W07_TableRejectsNonPositiveMaxPartLength(int maxPartLength)
    {
        var rule = N with { MaxPartLength = maxPartLength };

        Assert.Throws<ArgumentException>(() =>
        {
            _ = new TargetRuleTable([rule]);
        });
    }

    // ---------------------------------------------------------------- W08: Zielbindung

    /// <summary>Inspektor, Bindung und die gebundene Zeile samt Kennung und Fakten des gebundenen Elements.</summary>
    private sealed class Scenario
    {
        public required FakeTargetInspector Inspector { get; init; }

        public required TargetBinding Binding { get; init; }

        public required ElementIdentity Identity { get; init; }

        public required TargetFacts Facts { get; init; }

        public required TargetInfo Target { get; init; }

        public Guid Id => Target.BindingId;

        /// <summary>Setzt den Fokus ohne Fokusereignis.</summary>
        public void Focus(ElementIdentity identity, TargetFacts facts)
        {
            Inspector.Existing.Add(identity);
            Inspector.Focus = (identity, facts);
        }

        /// <summary>Neue Fakten für das unverändert fokussierte, gebundene Element.</summary>
        public void ChangeFacts(Func<TargetFacts, TargetFacts> change) => Inspector.Focus = (Identity, change(Facts));
    }

    private static Scenario Bound(TargetRule rule, string runtimeId = "42.1", string automationId = "")
    {
        var identity = TargetTable.IdentityFor(rule, runtimeId, automationId);
        var facts = TargetTable.FactsFor(rule);
        var inspector = new FakeTargetInspector { Focus = (identity, facts) };
        inspector.Existing.Add(identity);
        if (automationId.Length > 0)
        {
            inspector.AutomationIdCounts[(identity.TopLevelWindow, automationId)] = 1;
        }

        var binding = new TargetBinding(inspector, TargetTable.Table());
        var result = binding.BindFocused(ProfileStrictness.Standard);

        Assert.Null(result.Refusal);
        Assert.NotNull(result.Target);
        return new Scenario { Inspector = inspector, Binding = binding, Identity = identity, Facts = facts, Target = result.Target! };
    }

    /// <summary>Ein anderes Element eines anderen Fensters desselben Programms.</summary>
    private static ElementIdentity OtherElement(ElementIdentity identity) =>
        identity with { ProcessId = 5000, TopLevelWindow = 0x5678, RuntimeId = "7.7" };

    [Fact]
    public void W08_BindingTheSameElementTwiceGivesDifferentBindingIds()
    {
        var scenario = Bound(N);
        var first = scenario.Id;

        var second = scenario.Binding.BindFocused(ProfileStrictness.Standard);

        Assert.NotNull(second.Target);
        Assert.NotEqual(first, second.Target!.BindingId);
        // Die Kennung der früheren Bindung gilt nicht mehr, die der neuen schon
        Assert.Equal(TargetCheck.NotBound, scenario.Binding.CheckBound(first));
        Assert.Equal(TargetCheck.Focused, scenario.Binding.CheckBound(second.Target.BindingId));
    }

    [Fact]
    public void W08_CheckBoundReportsFocusedForTheBoundElementAndSendsOnlyThen()
    {
        var scenario = Bound(N);
        var sent = 0;

        Assert.Equal(TargetCheck.Focused, scenario.Binding.CheckBound(scenario.Id));
        Assert.Equal(TargetCheck.Focused, scenario.Binding.CheckAndSend(scenario.Id, () => sent++));
        Assert.Equal(1, sent);
    }

    [Fact]
    public void W08_CheckBoundReportsFocusElsewhereForAnotherElement()
    {
        var scenario = Bound(N);
        scenario.Focus(OtherElement(scenario.Identity), scenario.Facts);

        Assert.Equal(TargetCheck.FocusElsewhere, scenario.Binding.CheckBound(scenario.Id));
    }

    [Fact]
    public void W08_CheckBoundReportsTargetGoneWhenTheElementNoLongerExists()
    {
        var scenario = Bound(N);
        scenario.Inspector.Existing.Remove(scenario.Identity);

        Assert.Equal(TargetCheck.TargetGone, scenario.Binding.CheckBound(scenario.Id));
    }

    [Fact]
    public void W08_CheckBoundReportsSecureDesktop()
    {
        var scenario = Bound(N);
        scenario.Inspector.SecureDesktop = true;

        Assert.Equal(TargetCheck.SecureDesktop, scenario.Binding.CheckBound(scenario.Id));
    }

    [Fact]
    public void W08_CheckBoundReportsPasswordFieldWhenTheBoundElementTurnsPassword()
    {
        var scenario = Bound(N);
        scenario.ChangeFacts(facts => facts with { IsPassword = true });

        Assert.Equal(TargetCheck.PasswordField, scenario.Binding.CheckBound(scenario.Id));
    }

    [Fact]
    public void W08_CheckReportsElevatedAndSendsNothing()
    {
        // Das erste BindFocused genügt nicht: die Merkmale werden vor jedem Teil neu gelesen
        var scenario = Bound(N);
        scenario.ChangeFacts(facts => facts with { IsPassword = false, IsElevated = true });
        var sent = 0;

        Assert.Equal(TargetCheck.Elevated, scenario.Binding.CheckBound(scenario.Id));
        Assert.Equal(TargetCheck.Elevated, scenario.Binding.CheckAndSend(scenario.Id, () => sent++));
        Assert.Equal(0, sent);
    }

    // Entscheid vom 11.11.2026 (SI-04): nicht lesbare Merkmale, Schreibschutz und deaktiviert sperren das Senden nicht
    [Theory]
    [InlineData(null, false, true, true)]
    [InlineData(false, null, true, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, null, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, null)]
    public void W08_CheckSendsDespiteUnreadableReadOnlyOrDisabled(bool? isElevated, bool? isPassword, bool? isReadOnly, bool? isEnabled)
    {
        var scenario = Bound(N);
        scenario.ChangeFacts(facts => facts with
        {
            IsElevated = isElevated, IsPassword = isPassword, IsReadOnly = isReadOnly, IsEnabled = isEnabled,
        });
        var sent = 0;

        Assert.Equal(TargetCheck.Focused, scenario.Binding.CheckAndSend(scenario.Id, () => sent++));
        Assert.Equal(1, sent);
    }

    [Fact]
    public void W08_ReleaseEndsTheBinding()
    {
        var scenario = Bound(N);

        scenario.Binding.Release();

        Assert.Equal(TargetCheck.NotBound, scenario.Binding.CheckBound(scenario.Id));
    }

    [Fact]
    public void W08_ChangedRuntimeIdIsFocusElsewhereUntilRebinding()
    {
        // Gleiche Prozesskennung, Prozessstart, Fenster, Art und Klasse, aber andere RuntimeId: nicht nachweislich dasselbe Feld
        var scenario = Bound(N);
        var changed = scenario.Identity with { RuntimeId = "42.2" };
        scenario.Focus(changed, scenario.Facts);

        Assert.Equal(TargetCheck.FocusElsewhere, scenario.Binding.CheckBound(scenario.Id));
        Assert.Equal(TargetCheck.FocusElsewhere, scenario.Binding.CheckBound(scenario.Id));

        // Erst eine bewusste neue Bindung gilt wieder
        var rebound = scenario.Binding.BindFocused(ProfileStrictness.Standard);
        Assert.NotNull(rebound.Target);
        Assert.Equal(TargetCheck.Focused, scenario.Binding.CheckBound(rebound.Target!.BindingId));
        Assert.Equal(TargetCheck.NotBound, scenario.Binding.CheckBound(scenario.Id));
    }

    [Fact]
    public void W08_AutomationIdRowAcceptsChangedRuntimeIdWithUniqueAutomationId()
    {
        var scenario = Bound(T, runtimeId: "1.1", automationId: "chat-eingabe");
        var changed = scenario.Identity with { RuntimeId = "2.2" };
        scenario.Focus(changed, scenario.Facts);
        // Alte und neue Kennung gelten als vorhanden: geprüft wird hier nur die Ersatzidentität
        scenario.Inspector.Existing.Add(scenario.Identity);

        Assert.Equal(TargetCheck.Focused, scenario.Binding.CheckBound(scenario.Id));
    }

    [Fact]
    public void W08_AutomationIdRowRejectsAnotherSimilarFieldInTheSameWindow()
    {
        // Gleiche Art und Klasse, anderes Fenster-Feld mit anderer AutomationId
        var scenario = Bound(T, runtimeId: "1.1", automationId: "chat-eingabe");
        var similar = scenario.Identity with { RuntimeId = "3.3", AutomationId = "anderes-feld" };
        scenario.Inspector.AutomationIdCounts[(similar.TopLevelWindow, similar.AutomationId)] = 1;
        scenario.Focus(similar, scenario.Facts);
        scenario.Inspector.Existing.Add(scenario.Identity);

        Assert.Equal(TargetCheck.FocusElsewhere, scenario.Binding.CheckBound(scenario.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("chat-eingabe")]
    public void W08_BindingRefusesEmptyOrAmbiguousAutomationId(string automationId)
    {
        // Leer, oder im Fenster zweimal vorhanden
        var identity = TargetTable.IdentityFor(T, runtimeId: "1.1", automationId: automationId);
        var inspector = new FakeTargetInspector { Focus = (identity, TargetTable.FactsFor(T)) };
        inspector.Existing.Add(identity);
        inspector.AutomationIdCounts[(identity.TopLevelWindow, "chat-eingabe")] = 2;
        var binding = new TargetBinding(inspector, TargetTable.Table());

        var result = binding.BindFocused(ProfileStrictness.Standard);

        Assert.Null(result.Target);
        Assert.Equal(BindRefusal.IdentityUnclear, result.Refusal);
    }

    [Theory]
    [InlineData(2, "chat-eingabe")]
    [InlineData(0, "chat-eingabe")]
    [InlineData(0, "")]
    public void W08_CheckReportsIdentityUnclearWhenAutomationIdIsNoLongerUnique(int count, string focusedAutomationId)
    {
        // Zweimal im Fenster, nicht mehr vorhanden oder am fokussierten Element leer geworden
        var scenario = Bound(T, runtimeId: "1.1", automationId: "chat-eingabe");
        scenario.Inspector.AutomationIdCounts[(scenario.Identity.TopLevelWindow, "chat-eingabe")] = count;
        scenario.Focus(scenario.Identity with { AutomationId = focusedAutomationId }, scenario.Facts);
        scenario.Inspector.Existing.Add(scenario.Identity);

        Assert.Equal(TargetCheck.IdentityUnclear, scenario.Binding.CheckBound(scenario.Id));
    }

    [Fact]
    public void W08_FocusToAnotherForeignElementAndBackToTheSameFieldResumesTheBinding()
    {
        var scenario = Bound(N);
        var foreign = OtherElement(scenario.Identity);
        scenario.Inspector.Existing.Add(foreign);

        scenario.Inspector.MoveFocus((foreign, TargetTable.FactsFor(S)));
        Assert.Equal(TargetCheck.FocusElsewhere, scenario.Binding.CheckBound(scenario.Id));
        scenario.Inspector.MoveFocus((scenario.Identity, scenario.Facts));

        // Entscheid vom 11.11.2026: zurück im selben Feld gilt die Bindung wieder, ohne neu zu binden
        Assert.Equal(TargetCheck.Focused, scenario.Binding.CheckBound(scenario.Id));

        var rebound = scenario.Binding.BindFocused(ProfileStrictness.Standard);
        Assert.NotNull(rebound.Target);
        Assert.Equal(TargetCheck.Focused, scenario.Binding.CheckBound(rebound.Target!.BindingId));
    }

    [Fact]
    public void W08_FocusEventToOwnProgramAndBackKeepsTheBinding()
    {
        var scenario = Bound(N);
        var own = scenario.Identity with { ProcessId = 1, TopLevelWindow = 0x9999, RuntimeId = "own.1" };
        scenario.Inspector.Existing.Add(own);

        scenario.Inspector.MoveFocus((own, scenario.Facts with { ProcessName = "freihand", IsOwnProcess = true }));
        scenario.Inspector.MoveFocus((scenario.Identity, scenario.Facts));

        Assert.Equal(TargetCheck.Focused, scenario.Binding.CheckBound(scenario.Id));
    }

    [Fact]
    public async Task W08_BindFocusedWaitsForARunningCheckAndSend()
    {
        var scenario = Bound(N);
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new ManualResetEventSlim(false);
        var order = new List<string>();
        try
        {
            var sending = Task.Run(() => scenario.Binding.CheckAndSend(scenario.Id, () =>
            {
                sendStarted.TrySetResult();
                gate.Wait(Limit);
                lock (order)
                {
                    order.Add("send-ende");
                }
            }));

            // Schlägt CheckAndSend schon vorher fehl, kommt dieser Fehler hier zum Vorschein
            var first = await Task.WhenAny(sendStarted.Task, sending).WaitAsync(Limit);
            if (first == sending)
            {
                await sending;
            }

            Assert.True(sendStarted.Task.IsCompleted, "send wurde nicht aufgerufen.");

            var binding = Task.Run(() =>
            {
                var result = scenario.Binding.BindFocused(ProfileStrictness.Standard);
                lock (order)
                {
                    order.Add("bind-ende");
                }

                return result;
            });

            // Das Gate ist noch zu: BindFocused muss auf die Sperre warten
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.False(binding.IsCompleted, "BindFocused kehrte vor dem Ende von CheckAndSend zurück.");

            gate.Set();
            await Task.WhenAll(sending, binding).WaitAsync(Limit);

            Assert.Equal(TargetCheck.Focused, await sending);
            Assert.NotNull((await binding).Target);
            lock (order)
            {
                Assert.Equal(["send-ende", "bind-ende"], order);
            }
        }
        finally
        {
            gate.Set();
        }
    }
}
