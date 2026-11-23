using System.Diagnostics;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Segments;
using Freihand.Core.Tests.Platform.Fakes;

namespace Freihand.Core.Tests.Platform;

/// <summary>
/// P7-Konzept, Kapitel 7.1, W10–W17: Ausgabe ins gebundene Ziel (Kapitel 5.7). Zeit läuft nur über die
/// <see cref="ManualClock"/>; <see cref="RunAsync"/> dreht sie in kleinen Schritten weiter, bis der Auftrag fertig ist.
/// </summary>
public sealed class BoundTargetOutputTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(5);
    private const int MaxSteps = 2000;

    // ---- W10 ----------------------------------------------------------------------------------------------------

    // BE-01: In fremde Programme gelangt nur fester Text.
    [Fact]
    public async Task W10_ProvisionalSegmentIsRejectedWithoutInjecting()
    {
        var rig = new Rig();
        var provisional = new TextSegment(Guid.NewGuid(), "Das ist noch offen", SegmentState.Provisional, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Output.WriteAsync(provisional, rig.Target));

        Assert.Empty(rig.Injector.Parts);
    }

    // ---- W11 ----------------------------------------------------------------------------------------------------

    // AU-13, P3 T50: Zwischen Abschnitten genau ein Leerzeichen, ausser vor „,“.
    [Fact]
    public async Task W11a_JoinRuleAddsSpaceOnlyWhereTextJoinDoes()
    {
        var rig = new Rig();

        var first = await rig.Run("Das ist gut");
        var comma = await rig.Run(",");
        var last = await rig.Run("danke");

        AssertReport(first, OutputResult.Written, OutputReason.None, "Das ist gut", "");
        AssertReport(comma, OutputResult.Written, OutputReason.None, ",", "");
        AssertReport(last, OutputResult.Written, OutputReason.None, " danke", "");
        Assert.Equal("Das ist gut, danke", first.Written + comma.Written + last.Written);
        Assert.Equal("Das ist gut, danke", string.Concat(rig.Texts()));
    }

    // P3 T56: Nach einem Umbruch kein Leerzeichen; der Umbruch steht als eigener Abschnitt.
    [Fact]
    public async Task W11b_NoSpaceAroundALineBreakSection()
    {
        var rig = new Rig();

        var alpha = await rig.Run("Alpha");
        var lineBreak = await rig.Run("\n");
        var beta = await rig.Run("Beta");

        AssertReport(alpha, OutputResult.Written, OutputReason.None, "Alpha", "");
        AssertReport(lineBreak, OutputResult.Written, OutputReason.None, "\n", "");
        AssertReport(beta, OutputResult.Written, OutputReason.None, "Beta", "");
        Assert.Equal("Alpha\nBeta", string.Concat(rig.Texts()));
    }

    // P3 T56: Endet der Abschnitt mit dem Umbruch, folgt der nächste ohne Leerzeichen.
    [Fact]
    public async Task W11c_NoSpaceAfterASectionEndingInALineBreak()
    {
        var rig = new Rig();

        var alpha = await rig.Run("Alpha\n");
        var beta = await rig.Run("Beta");

        AssertReport(alpha, OutputResult.Written, OutputReason.None, "Alpha\n", "");
        AssertReport(beta, OutputResult.Written, OutputReason.None, "Beta", "");
        Assert.Equal("Alpha\nBeta", string.Concat(rig.Texts()));
    }

    // AU-13: P7 liest den Text vor der Schreibmarke nicht; nach neuer Bindung beginnt der erste Abschnitt ohne Leerzeichen.
    [Fact]
    public async Task W11d_FirstSectionAfterNewBindingGetsNoSpace()
    {
        var rig = new Rig();
        await rig.Run("Das ist gut");

        rig.Bind();
        var report = await rig.Run("Neu");

        AssertReport(report, OutputResult.Written, OutputReason.None, "Neu", "");
        Assert.Equal(["Das ist gut", "Neu"], rig.Texts());
    }

    // ---- W12 ----------------------------------------------------------------------------------------------------

    // Handtest P9 vom 11.11.2026: Das Leerzeichen nach dem Diktat bringt seine Trennung selbst mit, kein zweites davor
    [Theory]
    [InlineData("Satz zu Ende.")]
    [InlineData("ohne Punkt")]
    public async Task P9_APartStartingWithWhitespaceGetsNoSeparator(string before)
    {
        var rig = new Rig();

        await rig.Run(before);
        var report = await rig.Run(" ");

        AssertReport(report, OutputResult.Written, OutputReason.None, " ", "");
        Assert.Equal(before + " ", string.Concat(rig.Texts()));
    }

    // Entscheid vom 11.11.2026: Kehrt der Fokus ins selbe Feld zurück, schreibt die Ausgabe den Rest weiter, ohne Lücke
    [Fact]
    public async Task W12d_FocusReturningToTheSameFieldResumesTheRestInOrder()
    {
        var rig = new Rig(TargetTable.Options() with { FocusReturnLimit = TimeSpan.FromSeconds(1) });
        var text = LongText();
        var away = 0;
        rig.Binding.Override = () => rig.Injector.Parts.Count == 1 && away++ < 4 ? TargetCheck.FocusElsewhere : null;

        var report = await rig.Run(text);

        AssertReport(report, OutputResult.Written, OutputReason.None, text, "");
        Assert.Equal(text, string.Concat(rig.Texts()));
        Assert.True(away >= 4, "Die Ausgabe hat gewartet");
    }

    // D09 (P9 Diktatfenster, Kapitel 3.5): Das Warten auf die Rückkehr meldet sich mit Restzeit und endet mit null, damit
    // die Pille „Zurück ins Feld klicken“ zeigen kann
    [Fact]
    public async Task D09_WaitingForTheFocusIsReportedWithItsRemainingTimeAndItsEnd()
    {
        var limit = TimeSpan.FromSeconds(1);
        var rig = new Rig(TargetTable.Options() with { FocusReturnLimit = limit });
        var text = LongText();
        var away = 0;
        rig.Binding.Override = () => rig.Injector.Parts.Count == 1 && away++ < 4 ? TargetCheck.FocusElsewhere : null;
        var waits = new List<FocusWait>();
        rig.Output.FocusWaitChanged += (_, wait) =>
        {
            lock (waits)
            {
                waits.Add(wait);
            }
        };

        var report = await rig.Run(text);

        Assert.Equal(OutputResult.Written, report.Result);
        List<FocusWait> seen;
        lock (waits)
        {
            seen = [.. waits];
        }

        Assert.NotEmpty(seen);
        Assert.All(seen, wait => Assert.Equal(rig.Target.BindingId, wait.BindingId));
        Assert.NotNull(seen[0].Remaining);
        Assert.InRange(seen[0].Remaining!.Value, TimeSpan.Zero, limit);
        Assert.Null(seen[^1].Remaining);
    }

    // Codex zu PR #50: Ein kurzes Zurück, das vor dem Senden wieder weg ist, startet die Frist nicht neu
    [Fact]
    public async Task W12g_FlickeringFocusDoesNotRestartTheLimit()
    {
        var limit = TimeSpan.FromMilliseconds(500);
        var rig = new Rig(TargetTable.Options() with { FocusReturnLimit = limit });
        var text = LongText();
        rig.Binding.OverrideSend = () => rig.Injector.Parts.Count >= 1 ? TargetCheck.FocusElsewhere : null;
        var start = rig.Clock.GetTimestamp();

        var report = await rig.Run(text);

        AssertReport(report, OutputResult.HeldInDictationWindow, OutputReason.FocusElsewhere, text[..200], text[200..]);
        Assert.Single(rig.Injector.Parts);
        Assert.True(rig.Clock.GetElapsedTime(start) < limit + TimeSpan.FromMilliseconds(300), "Die Frist begann nicht neu");
    }

    [Fact]
    public async Task W12e_FocusNotReturningWithinTheLimitHoldsTheRest()
    {
        var rig = new Rig(TargetTable.Options() with { FocusReturnLimit = TimeSpan.FromMilliseconds(300) });
        var text = LongText();
        rig.Binding.Override = () => rig.Injector.Parts.Count >= 1 ? TargetCheck.FocusElsewhere : null;

        var report = await rig.Run(text);

        AssertReport(report, OutputResult.HeldInDictationWindow, OutputReason.FocusElsewhere, text[..200], text[200..]);
        Assert.Single(rig.Injector.Parts);
    }

    [Fact]
    public async Task W12f_FieldGoneWhileWaitingHoldsAtOnceWithItsReason()
    {
        var rig = new Rig(TargetTable.Options() with { FocusReturnLimit = TimeSpan.FromSeconds(1) });
        var text = LongText();
        var checks = 0;
        rig.Binding.Override = () => rig.Injector.Parts.Count == 1 ? (checks++ < 2 ? TargetCheck.FocusElsewhere : TargetCheck.TargetGone) : null;

        var report = await rig.Run(text);

        AssertReport(report, OutputResult.HeldInDictationWindow, OutputReason.TargetGone, text[..200], text[200..]);
        Assert.Single(rig.Injector.Parts);
    }

    // AU-10: Meldet die Prüfung vor dem zweiten Teil „Fokus woanders“, bleibt der Rest im Diktatfenster.
    [Fact]
    public async Task W12a_FocusChangeBeforeTheSecondPartHoldsTheRest()
    {
        var rig = new Rig();
        var text = LongText();
        rig.Binding.Override = () => rig.Injector.Parts.Count >= 1 ? TargetCheck.FocusElsewhere : null;

        var report = await rig.Run(text);

        AssertReport(report, OutputResult.HeldInDictationWindow, OutputReason.FocusElsewhere, text[..200], text[200..]);
        Assert.Equal(249, report.Held.Length);
        Assert.Single(rig.Injector.Parts);

        // Der nächste Abschnitt bleibt ganz im Diktatfenster, damit kein späterer Satz vor einen früheren gerät
        rig.Binding.Override = null;
        var next = await rig.Run("Nächster Satz");

        AssertReport(next, OutputResult.HeldInDictationWindow, OutputReason.PreviousHeld, "", "Nächster Satz");
        Assert.Single(rig.Injector.Parts);

        // Nach neuer Bindung wird wieder geschrieben
        rig.Bind();
        var again = await rig.Run("Neu");

        AssertReport(again, OutputResult.Written, OutputReason.None, "Neu", "");
        Assert.Equal(2, rig.Injector.Parts.Count);
    }

    // AU-10: Wurde in der Pause nach dem ersten Teil neu gebunden, endet der alte Auftrag; der Rest wandert nicht in die neue Bindung.
    [Fact]
    public async Task W12b_RebindingDuringThePauseEndsTheOldJobWithoutWritingToTheNewBinding()
    {
        var rig = new Rig();
        var text = LongText();
        var write = rig.Write(text);
        await WaitUntilAsync(() => rig.Injector.Parts.Count >= 1, write, "erster Teil eingefügt");

        // Die Uhr steht: der Auftrag ist in der Pause nach dem ersten Teil
        rig.Bind();
        var report = await RunAsync(write, rig.Clock);

        AssertReport(report, OutputResult.HeldInDictationWindow, OutputReason.NotBound, text[..200], text[200..]);
        Assert.Single(rig.Injector.Parts);

        // Der nächste Abschnitt an die neue Bindung wird geschrieben, ohne Leerzeichen davor
        var next = await rig.Run("Neu");

        AssertReport(next, OutputResult.Written, OutputReason.None, "Neu", "");
        Assert.Equal(2, rig.Injector.Parts.Count);
        Assert.Equal("Neu", rig.Injector.Parts[1].Text);
    }

    // AU-10: Zwei gleichzeitig gestartete Aufträge derselben Bindung senden nie ineinander.
    [Fact]
    public async Task W12c_ConcurrentWritesAreSerialized()
    {
        var rig = new Rig();
        var text = LongText();
        var first = rig.Write(text);
        var second = rig.Write("Zweiter Abschnitt");

        await PumpAsync(Task.WhenAll(first, second), rig.Clock);

        AssertReport(await first, OutputResult.Written, OutputReason.None, text, "");
        AssertReport(await second, OutputResult.Written, OutputReason.None, " Zweiter Abschnitt", "");
        var texts = rig.Texts();
        Assert.Equal(4, texts.Length);
        Assert.Equal([text[..200], text[200..400], text[400..], " Zweiter Abschnitt"], texts);
    }

    // ---- W13 ----------------------------------------------------------------------------------------------------

    // SI-04: Ein Passwortfeld verweigert die Ausgabe; spätere Abschnitte bleiben gehalten.
    [Fact]
    public async Task W13a_PasswordFieldRefusesAndHoldsEverythingAfter()
    {
        var rig = new Rig();
        rig.Binding.Override = () => TargetCheck.PasswordField;

        var report = await rig.Run("Das Feld bleibt leer");

        AssertReport(report, OutputResult.Refused, OutputReason.PasswordField, "", "Das Feld bleibt leer");
        Assert.Empty(rig.Injector.Parts);

        rig.Binding.Override = null;
        var next = await rig.Run("Und weiter");

        AssertReport(next, OutputResult.HeldInDictationWindow, OutputReason.PreviousHeld, "", "Und weiter");
        Assert.Empty(rig.Injector.Parts);
    }

    // SI-04, AU-01: Sicherheitsmerkmale werden vor jedem Teil neu geprüft; wird das Ziel unsicher, bleibt der Rest gehalten.
    [Theory]
    [InlineData(TargetCheck.Elevated, OutputReason.Elevated)]
    [InlineData(TargetCheck.ReadOnly, OutputReason.ReadOnly)]
    public async Task W13b_TargetBecomingUnsafeBeforeTheSecondPartHoldsTheRest(TargetCheck check, OutputReason reason)
    {
        var rig = new Rig();
        var text = LongText();
        rig.Binding.Override = () => rig.Injector.Parts.Count >= 1 ? check : null;

        var report = await rig.Run(text);

        AssertReport(report, OutputResult.HeldInDictationWindow, reason, text[..200], text[200..]);
        Assert.Single(rig.Injector.Parts);
    }

    // ---- W14 ----------------------------------------------------------------------------------------------------

    // BE-06: Nach dem Abbrechen geht höchstens der laufende Teil zu Ende, kein weiterer.
    [Fact]
    public async Task W14a_CancelDuringTheFirstPartLetsOnlyThatPartFinish()
    {
        var rig = new Rig();
        var text = LongText();
        using var cancel = new CancellationTokenSource();
        rig.Injector.During = _ => cancel.Cancel();

        var report = await rig.Run(text, cancel.Token);

        AssertReport(report, OutputResult.HeldInDictationWindow, OutputReason.Cancelled, text[..200], text[200..]);
        Assert.Single(rig.Injector.Parts);
    }

    // BE-06: Ein schon abgebrochener Auftrag sendet nichts.
    [Fact]
    public async Task W14b_AlreadyCancelledTokenInjectsNothing()
    {
        var rig = new Rig();
        var text = LongText();

        var report = await rig.Run(text, new CancellationToken(canceled: true));

        AssertReport(report, OutputResult.HeldInDictationWindow, OutputReason.Cancelled, "", text);
        Assert.Empty(rig.Injector.Parts);
    }

    // ---- W15 ----------------------------------------------------------------------------------------------------

    // AU-01: Meldet die Injektion keine einzige Einheit, bleibt der Abschnitt ganz im Diktatfenster.
    [Fact]
    public async Task W15a_InjectionReportingZeroUnitsHoldsTheWholeSection()
    {
        var rig = new Rig();
        rig.Injector.Respond = _ => new InjectionResult(0, []);

        var report = await rig.Run("Hallo Welt");

        AssertReport(report, OutputResult.HeldInDictationWindow, OutputReason.InjectionBlocked, "", "Hallo Welt");
        Assert.Single(rig.Injector.Parts);
    }

    // AU-01: Bei Teilerfolg gilt als geschrieben, was eingefügt wurde; der Rest bleibt gehalten.
    [Fact]
    public async Task W15b_PartialInjectionWritesOnlyTheInsertedUnits()
    {
        var rig = new Rig();
        rig.Injector.Respond = _ => new InjectionResult(3, []);

        var report = await rig.Run("abcd");

        AssertReport(report, OutputResult.HeldInDictationWindow, OutputReason.InjectionBlocked, "abc", "d");
        Assert.Single(rig.Injector.Parts);
    }

    // AU-13: Kann eine Taste noch gedrückt sein, schreibt die Ausgabe bis zur nächsten bewussten Bindung nichts mehr.
    [Fact]
    public async Task W15c_KeysMaybeDownHoldsEverythingUntilNewBinding()
    {
        var rig = new Rig();
        rig.Injector.Respond = part => new InjectionResult(part.Text.Length, [0x10]);

        var report = await rig.Run("Hallo");

        Assert.Equal(OutputResult.HeldInDictationWindow, report.Result);
        Assert.Equal(OutputReason.KeyReleaseFailed, report.Reason);
        Assert.Equal("Hallo", report.Written + report.Held);
        Assert.Single(rig.Injector.Parts);

        rig.Injector.Respond = null;
        var second = await rig.Run("Erster weiterer Abschnitt");
        var third = await rig.Run("Zweiter weiterer Abschnitt");

        AssertReport(second, OutputResult.HeldInDictationWindow, OutputReason.KeyReleaseFailed, "", "Erster weiterer Abschnitt");
        AssertReport(third, OutputResult.HeldInDictationWindow, OutputReason.KeyReleaseFailed, "", "Zweiter weiterer Abschnitt");
        Assert.Single(rig.Injector.Parts);

        rig.Bind();
        var again = await rig.Run("Neu");

        AssertReport(again, OutputResult.Written, OutputReason.None, "Neu", "");
        Assert.Equal(2, rig.Injector.Parts.Count);
    }

    // ---- W16 ----------------------------------------------------------------------------------------------------

    // SI-05, AU-13: Die Ausgabe wartet auf die eigenen Tasten und fügt erst ein, wenn sie oben sind.
    [Fact]
    public async Task W16a_WaitsForOwnKeysAndInjectsOnlyAfterTheyAreReleased()
    {
        var rig = new Rig();
        var injectedAt = new List<TimeSpan>();
        rig.Hotkeys.KeysReleased = () => rig.Clock.Elapsed >= TimeSpan.FromSeconds(1.5);
        rig.Injector.During = _ => injectedAt.Add(rig.Clock.Elapsed);

        var report = await rig.Run("Hallo Welt");

        AssertReport(report, OutputResult.Written, OutputReason.None, "Hallo Welt", "");
        var at = Assert.Single(injectedAt);
        Assert.True(at >= TimeSpan.FromSeconds(1.5), $"Eingefügt nach {at.TotalSeconds} s, vor dem Loslassen der Tasten.");
        Assert.Single(rig.Injector.Parts);
    }

    // SI-05, AU-13: Nach höchstens KeyWaitLimit gibt die Ausgabe auf und hält den Abschnitt.
    [Fact]
    public async Task W16b_KeysHeldLongerThanTheLimitHoldTheSection()
    {
        var rig = new Rig();
        rig.Hotkeys.KeysReleased = () => rig.Clock.Elapsed >= TimeSpan.FromSeconds(2.5);

        var report = await rig.Run("Hallo Welt");

        AssertReport(report, OutputResult.HeldInDictationWindow, OutputReason.KeysHeld, "", "Hallo Welt");
        Assert.Empty(rig.Injector.Parts);
    }

    // ---- W17 ----------------------------------------------------------------------------------------------------

    // AU-10: Eine andere als die aktuelle Bindung ergibt „nicht gebunden“, ohne einzufügen.
    [Fact]
    public async Task W17a_RequestForAnotherBindingIsHeldAsNotBound()
    {
        var rig = new Rig();
        var other = rig.Target with { BindingId = Guid.NewGuid() };

        var report = await rig.Run("Hallo Welt", other);

        AssertReport(report, OutputResult.HeldInDictationWindow, OutputReason.NotBound, "", "Hallo Welt");
        Assert.Empty(rig.Injector.Parts);
    }

    // AU-10: Die Prüfung der Bindung wiederholt sich nach dem Warten auf die eigenen Tasten.
    [Fact]
    public async Task W17b_RebindingWhileWaitingForOwnKeysHoldsTheWholeSection()
    {
        var rig = new Rig();
        using var released = new ManualResetEventSlim();
        rig.Hotkeys.KeysReleased = () => released.IsSet;
        var write = rig.Write("Hallo Welt");
        await WaitUntilAsync(() => rig.Hotkeys.ReleaseQueries >= 1, write, "Warten auf die eigenen Tasten");

        rig.Bind();
        released.Set();
        var report = await RunAsync(write, rig.Clock);

        AssertReport(report, OutputResult.HeldInDictationWindow, OutputReason.NotBound, "", "Hallo Welt");
        Assert.Empty(rig.Injector.Parts);
    }

    // AU-10: Die Prüfung der Bindung wiederholt sich auch nach dem Warten auf den vorherigen Auftrag.
    [Fact]
    public async Task W17c_RebindingWhileWaitingForThePreviousJobHoldsTheWholeSection()
    {
        var rig = new Rig();
        var first = rig.Write(LongText());
        var second = rig.Write("Zweiter Abschnitt");
        await WaitUntilAsync(() => rig.Injector.Parts.Count >= 1, first, "erster Teil des ersten Auftrags eingefügt");

        // Der erste Auftrag ist in der Pause, der zweite wartet auf ihn
        rig.Bind();
        await PumpAsync(Task.WhenAll(first, second), rig.Clock);

        AssertReport(await second, OutputResult.HeldInDictationWindow, OutputReason.NotBound, "", "Zweiter Abschnitt");
        Assert.Single(rig.Injector.Parts);
    }

    // ---- Hilfen -------------------------------------------------------------------------------------------------

    /// <summary>Neun Sätze zu je 48 Zeichen plus „. “, ohne das letzte Leerzeichen; mit MaxPartLength 200 ergibt das Teile 200, 200, 49.</summary>
    private static string LongText()
    {
        var text = string.Join(". ", Enumerable.Range(1, 9).Select(i => $"Satz {i} ".PadRight(48, 'b'))) + ".";
        Assert.Equal(449, text.Length);
        return text;
    }

    private static TextSegment Fixed(string text) =>
        new(Guid.NewGuid(), text, SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

    private static void AssertReport(OutputReport report, OutputResult result, OutputReason reason, string written, string held)
    {
        Assert.Equal(result, report.Result);
        Assert.Equal(reason, report.Reason);
        Assert.Equal(written, report.Written);
        Assert.Equal(held, report.Held);
    }

    /// <summary>Dreht die Uhr in kleinen Schritten weiter, bis der Auftrag fertig ist; ohne echte Wartezeit ausser kurzen Wechseln.</summary>
    private static async Task<OutputReport> RunAsync(Task<OutputReport> write, ManualClock clock)
    {
        await PumpAsync(write, clock);
        return await write;
    }

    private static async Task PumpAsync(Task work, ManualClock clock)
    {
        var real = Stopwatch.StartNew();
        for (var step = 0; step < MaxSteps && !work.IsCompleted; step++)
        {
            // Zuerst die Fortsetzung laufen lassen, bis sie den nächsten Zeitgeber angelegt hat oder fertig ist
            var settle = Stopwatch.StartNew();
            while (!work.IsCompleted && clock.ActiveTimers == 0 && settle.Elapsed < TimeSpan.FromMilliseconds(20))
            {
                await Task.Yield();
            }

            if (work.IsCompleted)
            {
                break;
            }

            Assert.True(real.Elapsed < TimeSpan.FromSeconds(60), $"Auftrag nach {real.Elapsed.TotalSeconds:F0} s echter Zeit nicht fertig.");
            clock.Advance(Step);
        }

        await Task.Yield();
        Assert.True(work.IsCompleted, $"Auftrag nach {MaxSteps} Schritten ({clock.Elapsed.TotalSeconds} s Uhrzeit) nicht fertig.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Task work, string what)
    {
        var real = Stopwatch.StartNew();
        while (!condition() && !work.IsCompleted && real.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Yield();
        }

        if (!condition() && work.IsCompleted)
        {
            await work;
        }

        Assert.True(condition(), $"Nicht eingetreten: {what}.");
    }

    // ---- Bedingung vor dem ersten Zeichen (Beta B9, Codex zu PR #55) ---------------------------------------------------

    [Fact]
    public async Task B9_AFailedConditionWritesNothing()
    {
        var rig = new Rig();

        var report = await RunAsync(rig.Output.WriteAsync(Fixed("Vorschlag"), rig.Target, () => false), rig.Clock);

        AssertReport(report, OutputResult.Refused, OutputReason.ConditionFailed, "", "Vorschlag");
        Assert.Empty(rig.Injector.Parts);
    }

    // Die Bedingung gilt nach dem Warten auf den Fokus, nicht davor
    [Fact]
    public async Task B9_TheConditionIsCheckedAgainAfterWaitingForTheFocus()
    {
        var rig = new Rig(TargetTable.Options() with { FocusReturnLimit = TimeSpan.FromSeconds(1) });
        var away = 0;
        var checks = 0;
        rig.Binding.Override = () => away++ < 3 ? TargetCheck.FocusElsewhere : null;

        var report = await RunAsync(rig.Output.WriteAsync(Fixed("Vorschlag"), rig.Target, () => checks++ == 0), rig.Clock);

        AssertReport(report, OutputResult.Refused, OutputReason.ConditionFailed, "", "Vorschlag");
        Assert.Empty(rig.Injector.Parts);
        Assert.Equal(2, checks);
    }

    // Nach dem ersten Zeichen wird mit Bedingung nach einer Unterbrechung nicht weitergeschrieben
    [Fact]
    public async Task B9_WithAConditionAnInterruptionAfterTheFirstPartHoldsTheRest()
    {
        var rig = new Rig(TargetTable.Options() with { FocusReturnLimit = TimeSpan.FromSeconds(1) });
        var text = LongText();
        var away = 0;
        rig.Binding.Override = () => rig.Injector.Parts.Count == 1 && away++ < 2 ? TargetCheck.FocusElsewhere : null;

        var report = await RunAsync(rig.Output.WriteAsync(Fixed(text), rig.Target, () => true), rig.Clock);

        AssertReport(report, OutputResult.HeldInDictationWindow, OutputReason.FocusElsewhere, text[..200], text[200..]);
        Assert.Single(rig.Injector.Parts);
    }

    /// <summary>
    /// Ausgabe mit allen Attrappen und einer frischen Bindung an Zeile N. <see cref="Output"/> startet mit den Startwerten
    /// und <see cref="ControlKeySafety.NotApproved"/>.
    /// </summary>
    private sealed class Rig
    {
        public Rig(OutputOptions? options = null)
        {
            Binding = new ScriptedBinding(new FakeTargetBinding(TargetTable.N));
            Output = new BoundTargetOutput(Binding, Injector, Hotkeys, options ?? TargetTable.Options(), Clock);
            Target = Bind();
        }

        public ManualClock Clock { get; } = new();

        public ScriptedBinding Binding { get; }

        public FakeTextInjector Injector { get; } = new();

        public FakeHotkeyService Hotkeys { get; } = new();

        public BoundTargetOutput Output { get; }

        /// <summary>Die aktuelle Bindung; <see cref="Bind"/> ersetzt sie.</summary>
        public TargetInfo Target { get; private set; }

        public TargetInfo Bind()
        {
            Target = Binding.BindFocused(ProfileStrictness.Standard).Target!;
            return Target;
        }

        public string[] Texts() => [.. Injector.Parts.Select(part => part.Text)];

        /// <summary>Startet den Auftrag mit einem festen Abschnitt, ohne zu warten.</summary>
        public Task<OutputReport> Write(string text, TargetInfo? target = null, CancellationToken cancellationToken = default) =>
            Output.WriteAsync(Fixed(text), target ?? Target, cancellationToken);

        public Task<OutputReport> Run(string text, TargetInfo? target = null, CancellationToken cancellationToken = default) =>
            RunAsync(Write(text, target, cancellationToken), Clock);

        public Task<OutputReport> Run(string text, CancellationToken cancellationToken) => Run(text, null, cancellationToken);
    }

    /// <summary>
    /// Hüllt <see cref="FakeTargetBinding"/> (Kennungsregel, Sperre). <see cref="Override"/> ersetzt das Ergebnis der Prüfung
    /// nach Zustand statt nach Zahl der Aufrufe, damit die Tests nicht davon abhängen, wie oft die Ausgabe zwischen den
    /// Teilen prüft; <c>null</c> heisst keine Änderung. Bei einem Ersatz wird <c>send</c> nicht aufgerufen.
    /// </summary>
    private sealed class ScriptedBinding(FakeTargetBinding inner) : ITargetBinding
    {
        public Func<TargetCheck?>? Override { get; set; }

        /// <summary>Ersetzt nur das Ergebnis von <see cref="CheckAndSend"/>; vor <see cref="Override"/> ausgewertet.</summary>
        public Func<TargetCheck?>? OverrideSend { get; set; }

        public TargetInfo? Bound => inner.Bound;

        public TargetRule? BoundRule => inner.BoundRule;

        public BindResult BindFocused(ProfileStrictness strictness) => inner.BindFocused(strictness);

        public TargetCheck CheckBound(Guid bindingId)
        {
            var check = inner.CheckBound(bindingId);
            return check == TargetCheck.Focused ? Override?.Invoke() ?? check : check;
        }

        public TargetCheck CheckAndSend(Guid bindingId, Action send)
        {
            TargetCheck? forced = null;
            var check = inner.CheckAndSend(bindingId, () =>
            {
                forced = OverrideSend?.Invoke() ?? Override?.Invoke();
                if (forced is null)
                {
                    send();
                }
            });
            return forced ?? check;
        }

        public void Release() => inner.Release();
    }
}
