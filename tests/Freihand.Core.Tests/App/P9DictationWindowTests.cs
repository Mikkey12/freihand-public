using System.Text;
using Freihand.Adapters.Dictation;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;
using Freihand.Core.Recovery;
using Freihand.Core.Segments;
using Freihand.Core.Tests.Recovery;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Abnahmetests für die Steuerung. Sie prüfen Verlustschutz
/// je Diktat, Diktatfenster als Ziel, Übernehmen ins gemerkte Feld, kein Text im Protokoll. Gewartet wird nur mit
/// Zeitgrenze auf beobachtbare Zustände. Erfundene Texte.
/// </summary>
public sealed class P9DictationWindowTests
{
    private const string FirstText = "Der Leuchtturm blinkt zweimal";
    private const string SecondText = "Die Fähre legt um sieben an";
    private const string ProvisionalMarker = "VORLAEUFIG-Q4K";
    private const string TextMarker = "GEHEIM-MARKE-7QZ";

    // ---- D02: jede Aktualisierung geht an Apply, vor dem Schreiben; vorläufiger Text nie --------------------------

    [Fact]
    public async Task D02_EveryFixedSegmentIsSavedBeforeItIsWritten()
    {
        var recovery = new RecoveryRig();
        var savedBefore = new Dictionary<string, bool>();
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Recovery = recovery.Store;
            r.Output.BeforeWrite = segment =>
            {
                // Ein Takt der Sicherung, bevor etwas geschrieben ist: der Text muss schon im Entwurf stehen
                recovery.Tick();
                lock (savedBefore)
                {
                    savedBefore[segment.Text] = recovery.Saved(segment.Text);
                }
            };
        });

        var run = await rig.StartRecordingAsync();
        run.Emit(Output(Fixed(FirstText)));
        run.Emit(Output(Fixed(SecondText)));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 2, "zwei Schreibaufträge");
        await rig.EndAsync(run);

        lock (savedBefore)
        {
            Assert.True(savedBefore[FirstText], "Der erste Abschnitt war vor dem Schreiben gesichert");
            Assert.True(savedBefore[SecondText], "Der zweite Abschnitt war vor dem Schreiben gesichert");
        }
    }

    [Fact]
    public async Task D02_ProvisionalTextIsNeverSaved()
    {
        var recovery = new RecoveryRig();
        await using var rig = new ControllerRig(arrange: r => r.Recovery = recovery.Store);

        var run = await rig.StartRecordingAsync();
        run.Emit(new TextOutput(new RecognitionUpdate([Fixed(FirstText)], [Provisional(ProvisionalMarker)])));
        run.Emit(new TextOutput(new RecognitionUpdate([], [Provisional(ProvisionalMarker + " weiter")])));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "Schreiben des festen Abschnitts");
        recovery.Tick();
        await rig.EndAsync(run);
        recovery.Tick();

        Assert.True(recovery.Saved(FirstText), "Fester Text ist gesichert");
        Assert.False(recovery.Saved(ProvisionalMarker), "Vorläufiger Text kommt nie in die Sicherung");
    }

    [Fact]
    public async Task D02_AFailingSaveIsReportedByKindAndTheDictationGoesOn()
    {
        var recovery = new RecoveryRig();
        await using var rig = new ControllerRig(arrange: r => r.Recovery = recovery.Store);

        var run = await rig.StartRecordingAsync();
        recovery.Files.FailWrites = true;
        run.Emit(Output(Fixed(TextMarker)));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "Schreiben trotz Sicherungsfehler");
        recovery.Tick();
        await rig.WaitUntil(
            () => rig.Updates.Any(update => update.Message == ControllerMessage.RecoveryFailed && update.Reason == nameof(RecoveryIssueKind.WriteFailed)),
            "Meldung „Sicherung gescheitert“ mit der Art");
        await rig.EndAsync(run);

        Assert.Equal([TextMarker], rig.Output.Texts);
        Assert.All(rig.LogLines, line => Assert.DoesNotContain(TextMarker, line, StringComparison.Ordinal));
    }

    // ---- D03: alles geschrieben → übernommen; etwas gehalten → nicht; Abbruch → kein Verwerfen ---------------------

    [Fact]
    public async Task D03_AllSegmentsWrittenMarksTheDraftHandedOver()
    {
        var recovery = new RecoveryRig();
        await using var rig = new ControllerRig(arrange: r => r.Recovery = recovery.Store);

        var run = await rig.StartRecordingAsync();
        run.Emit(Output(Fixed(FirstText), Fixed(SecondText)));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 2, "zwei Schreibaufträge");
        await rig.EndAsync(run);

        var draft = Assert.Single(recovery.Store.Scan().Drafts);
        Assert.NotNull(draft.HandedOverAt);
        Assert.Equal(TextJoin.Join([FirstText, SecondText]), draft.Text);
        Assert.Equal(2, rig.Controller.Current.Written);
    }

    [Fact]
    public async Task D03_HeldTextKeepsTheDraftOpenUntilItIsCopied()
    {
        var recovery = new RecoveryRig();
        var held = Fixed(SecondText);
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Recovery = recovery.Store;
            r.Output.SetReport(held.Id, new OutputReport(OutputResult.HeldInDictationWindow, "", SecondText, OutputReason.FocusElsewhere));
        });

        var run = await rig.StartRecordingAsync();
        run.Emit(Output(Fixed(FirstText), held));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 2, "zwei Schreibaufträge");
        await rig.EndAsync(run);

        // Noch laufend: nicht übernommen, nicht angeboten (nur nach einem Neustart)
        Assert.Empty(recovery.Store.Scan().Drafts);
        Assert.Equal(1, rig.Controller.Current.HeldOpen);
        recovery.Tick();
        Assert.True(recovery.Saved(SecondText), "Der gehaltene Text ist gesichert");

        Assert.True(rig.Controller.CopyHeld(held.Id));

        var draft = Assert.Single(recovery.Store.Scan().Drafts);
        Assert.NotNull(draft.HandedOverAt);
        Assert.Equal(0, rig.Controller.Current.HeldOpen);
        Assert.True(Assert.Single(rig.Controller.Current.Entries, entry => entry.SegmentId == held.Id).Taken);
    }

    [Fact]
    public async Task D03_TakingHeldTextIntoTheWindowHandsTheDraftOver()
    {
        var recovery = new RecoveryRig();
        var first = Fixed(FirstText);
        var second = Fixed(SecondText);
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Recovery = recovery.Store;
            r.Output.SetReport(first.Id, new OutputReport(OutputResult.HeldInDictationWindow, "", FirstText, OutputReason.FocusElsewhere));
            r.Output.SetReport(second.Id, new OutputReport(OutputResult.HeldInDictationWindow, "", SecondText, OutputReason.PreviousHeld));
        });

        var run = await rig.StartRecordingAsync();
        run.Emit(Output(first, second));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 2, "zwei Schreibaufträge");
        await rig.EndAsync(run);

        var text = rig.Controller.TakeHeld();

        Assert.Equal(TextJoin.Join([FirstText, SecondText]), text);
        Assert.Equal(string.Empty, rig.Controller.TakeHeld());
        Assert.Equal(0, rig.Controller.Current.HeldOpen);
        Assert.NotNull(Assert.Single(recovery.Store.Scan().Drafts).HandedOverAt);
    }

    [Fact]
    public async Task D03_CancelNeverDiscardsTheDraft()
    {
        var recovery = new RecoveryRig();
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Recovery = recovery.Store;
            r.Output.HoldWritesOf(FirstText);
        });

        var run = await rig.StartRecordingAsync();
        run.Emit(Output(Fixed(FirstText), Fixed(SecondText)));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "der erste Auftrag hängt");
        await rig.Controller.CancelAsync();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        // Abgebrochen (E4): der Entwurf bleibt bis zur Frist wiederherstellbar, nicht übernommen; nach einem Neustart
        // wird er angeboten, auch der nie geschriebene zweite Abschnitt
        recovery.Tick();
        using var restart = new RecoveryStore(recovery.Files, recovery.Protector, recovery.Time);
        var draft = Assert.Single(restart.Scan().Drafts);
        Assert.Null(draft.HandedOverAt);
        Assert.Equal(TextJoin.Join([FirstText, SecondText]), draft.Text);
        Assert.Contains(rig.Updates, update => update.Message == ControllerMessage.Cancelled);
    }

    // ---- D05: Diktat in die Schreibfläche ------------------------------------------------------------------------

    [Fact]
    public async Task D05_DictationIntoThePadGoesToTheCaretAndNeverIntoAForeignField()
    {
        await using var rig = new ControllerRig(arrange: r => r.Pad = new FakePad(r.Log) { Focused = true, Text = "Anfang Ende", Caret = 6 });

        var run = await rig.StartRecordingAsync();
        run.Emit(Output(Fixed("Mitte")));
        await rig.WaitUntil(() => rig.Controller.Current.Entries.Count == 1, "Eintrag des Abschnitts");
        await rig.EndAsync(run);

        Assert.Equal("Anfang Mitte Ende", rig.Pad!.Text);
        Assert.Empty(rig.Output.Calls);
        Assert.Equal(0, rig.Binding.BindCount);
        Assert.Equal(1, rig.Guard.ArmCount);
        var entry = Assert.Single(rig.Controller.Current.Entries);
        Assert.Equal(EntryStatus.Written, entry.Status);
        Assert.Equal(" Mitte", entry.Written);
        Assert.True(rig.Controller.Current.ToDictationWindow);
        Assert.Equal("Diktatfenster", rig.Controller.Current.Target);
    }

    [Fact]
    public async Task D05_WithoutFocusInThePadTheFocusedFieldIsBoundAsBefore()
    {
        await using var rig = new ControllerRig(arrange: r => r.Pad = new FakePad(r.Log) { Focused = false });

        var run = await rig.StartRecordingAsync();
        run.Emit(Output(Fixed(FirstText)));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "Schreiben ins fremde Feld");
        await rig.EndAsync(run);

        Assert.Equal(1, rig.Binding.BindCount);
        Assert.Equal(string.Empty, rig.Pad!.Text);
        Assert.False(rig.Controller.Current.ToDictationWindow);
    }

    [Fact]
    public async Task D05_AFailingPadEndsTheDictationAndHoldsTheText()
    {
        await using var rig = new ControllerRig(arrange: r => r.Pad = new FakePad(r.Log) { Focused = true, Fail = true });

        var run = await rig.StartRecordingAsync();
        run.Emit(Output(Fixed(FirstText)));
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        var entry = Assert.Single(rig.Controller.Current.Entries);
        Assert.Equal(EntryStatus.Held, entry.Status);
        Assert.Equal(FirstText, entry.Held);
        Assert.Empty(rig.Output.Calls);
        Assert.Equal(ControllerMessage.DictationFailed, rig.Controller.Current.Message);
    }

    // ---- D06: Übernehmen nur ins gemerkte Feld -------------------------------------------------------------------

    [Fact]
    public async Task D06_HandOverWritesOnlyIntoTheMarkedFieldAndReleasesTheBinding()
    {
        await using var rig = new ControllerRig();
        var mark = FakeBinding.NewMark("Gemerktes Feld");

        var report = await rig.Controller.HandOverAsync("Erste Zeile\r\nZweite Zeile", mark).WaitAsync(ControllerRig.Timeout);

        Assert.Equal(HandOverResult.Written, report.Result);
        var call = Assert.Single(rig.Output.Calls);
        Assert.Equal("Erste Zeile\nZweite Zeile", call.Segment.Text);
        Assert.Equal(SegmentState.Fixed, call.Segment.State);
        Assert.Equal("Gemerktes Feld", call.Target.Display);
        Assert.True(rig.Log.IndexOf("Binding.Release") > rig.Log.IndexOf("Output.Done:Erste Zeile\nZweite Zeile"));
        Assert.Equal(0, rig.Binding.BindCount);
    }

    [Fact]
    public async Task D06_AnotherFieldWithTheFocusWritesNothingAndTheTextStays()
    {
        await using var rig = new ControllerRig(
            ControllerRig.Options(ProfileStrictness.Standard) with { HandOverSettle = TimeSpan.FromMilliseconds(200) },
            r => r.Binding.MarkedFocus.Enqueue(BindRefusal.NotMarkedField));

        var report = await rig.Controller.HandOverAsync(FirstText, FakeBinding.NewMark()).WaitAsync(ControllerRig.Timeout);

        Assert.Equal(HandOverResult.Refused, report.Result);
        Assert.Equal(FirstText, report.Held);
        Assert.Equal(nameof(BindRefusal.NotMarkedField), report.Reason);
        Assert.Empty(rig.Output.Calls);
        Assert.True(rig.Binding.MarkedBinds >= 2, "Während der Wartezeit wird erneut geprüft");
        Assert.Contains(rig.Updates, update => update.Message == ControllerMessage.HandOverRefused);
    }

    [Fact]
    public async Task D06_TheMarkedFieldGettingTheFocusWhileSettlingIsUsed()
    {
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Binding.MarkedFocus.Enqueue(BindRefusal.OwnProcess);
            r.Binding.MarkedFocus.Enqueue(BindRefusal.NotMarkedField);
            r.Binding.MarkedFocus.Enqueue(null);
        });

        var report = await rig.Controller.HandOverAsync(FirstText, FakeBinding.NewMark()).WaitAsync(ControllerRig.Timeout);

        Assert.Equal(HandOverResult.Written, report.Result);
        Assert.Equal(3, rig.Binding.MarkedBinds);
        Assert.Equal([FirstText], rig.Output.Texts);
    }

    [Fact]
    public async Task D06_AHardRefusalIsNotRetried()
    {
        await using var rig = new ControllerRig(arrange: r => r.Binding.MarkedFocus.Enqueue(BindRefusal.PasswordField));

        var report = await rig.Controller.HandOverAsync(FirstText, FakeBinding.NewMark()).WaitAsync(ControllerRig.Timeout);

        Assert.Equal(HandOverResult.Refused, report.Result);
        Assert.Equal(nameof(BindRefusal.PasswordField), report.Reason);
        Assert.Equal(1, rig.Binding.MarkedBinds);
        Assert.Empty(rig.Output.Calls);
    }

    [Fact]
    public async Task D06_APartialWriteKeepsTheRestInTheWindow()
    {
        await using var rig = new ControllerRig(arrange: r => r.Output.SetReportForText(
            FirstText, new OutputReport(OutputResult.HeldInDictationWindow, "Der Leucht", "turm blinkt zweimal", OutputReason.FocusElsewhere)));

        var report = await rig.Controller.HandOverAsync(FirstText, FakeBinding.NewMark()).WaitAsync(ControllerRig.Timeout);

        Assert.Equal(HandOverResult.Partial, report.Result);
        Assert.Equal("turm blinkt zweimal", report.Held);
        Assert.Equal(nameof(OutputReason.FocusElsewhere), report.Reason);
    }

    [Fact]
    public async Task D06_NoHandOverDuringADictationAndNoDictationDuringAHandOver()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();

        var busy = await rig.Controller.HandOverAsync(FirstText, FakeBinding.NewMark()).WaitAsync(ControllerRig.Timeout);
        Assert.Equal(HandOverResult.Refused, busy.Result);
        Assert.Equal("Busy", busy.Reason);
        Assert.Empty(rig.Output.Calls);
        await rig.EndAsync(run);

        // Übernehmen hängt im Schreiben: die Aufnahmetaste startet kein Diktat, Abbrechen beendet das Übernehmen
        rig.Output.HoldWritesOf(SecondText);
        var handOver = rig.Controller.HandOverAsync(SecondText, FakeBinding.NewMark());
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "Übernehmen schreibt");
        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        Assert.Equal(1, rig.Factory.StartCount);
        await rig.Controller.CancelAsync();
        var report = await handOver.WaitAsync(ControllerRig.Timeout);

        Assert.Equal(HandOverResult.Refused, report.Result);
        Assert.Equal(SecondText, report.Held);
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
    }

    [Fact]
    public async Task D06_TheWindowKeyMarksTheFocusedFieldBeforeTheWindowOpens()
    {
        await using var rig = new ControllerRig(arrange: r => r.Binding.NextMark = new MarkResult(FakeBinding.NewMark("Word – Dokumenttext"), null));
        rig.Controller.RegisterHotkeys();
        var requests = new List<DictationWindowRequest>();
        rig.Controller.DictationWindowRequested += (_, request) =>
        {
            lock (requests)
            {
                requests.Add(request);
            }
        };

        rig.Hotkeys.Press(HotkeyAction.DictationWindow);
        await rig.WaitUntil(() => { lock (requests) { return requests.Count == 1; } }, "Anfrage fürs Diktatfenster");

        Assert.Equal("Word – Dokumenttext", requests[0].Mark.Mark!.Display);
        Assert.True(rig.Log.Contains("Binding.MarkFocused:Standard"));
        Assert.Null(rig.Binding.Bound);
    }

    [Fact]
    public async Task D06_FromInsideThePadNothingIsMarked()
    {
        await using var rig = new ControllerRig(arrange: r => r.Pad = new FakePad(r.Log) { Focused = true });

        var mark = rig.Controller.MarkTarget();

        Assert.Null(mark.Mark);
        Assert.Equal(BindRefusal.OwnProcess, mark.Refusal);
        Assert.False(rig.Log.Contains("Binding.MarkFocused:Standard"));
    }

    // ---- D08: kein Text im Protokoll, die Sicherung ohne Klartext -------------------------------------------------

    [Fact]
    public async Task D08_TheMarkerNeverReachesTheLogsAndTheDraftFileHasNoPlaintext()
    {
        var recovery = new RecoveryRig();
        var held = Fixed(TextMarker + " gehalten");
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Recovery = recovery.Store;
            r.Output.SetReport(held.Id, new OutputReport(OutputResult.HeldInDictationWindow, "", held.Text, OutputReason.FocusElsewhere));
        });

        var run = await rig.StartRecordingAsync();
        run.Emit(new TextOutput(new RecognitionUpdate([Fixed(TextMarker), held], [Provisional(TextMarker + " vorläufig")])));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 2, "zwei Schreibaufträge");
        recovery.Files.FailWrites = true;
        recovery.Tick();
        recovery.Files.FailWrites = false;
        recovery.Tick();
        await rig.EndAsync(run);
        await rig.Controller.HandOverAsync(TextMarker + " übernommen", FakeBinding.NewMark());
        recovery.Tick();

        Assert.True(recovery.Saved(TextMarker), "Der Marker ist gesichert, also geprüft");
        var marker = Encoding.UTF8.GetBytes(TextMarker);
        Assert.NotEmpty(recovery.Files.Files);
        Assert.All(recovery.Files.Files.Values, file => Assert.True(file.Data.AsSpan().IndexOf(marker) < 0, "Klartext in der Sicherungsdatei"));
        Assert.NotEmpty(rig.LogLines);
        Assert.All(rig.LogLines, line => Assert.DoesNotContain(TextMarker, line, StringComparison.Ordinal));
        Assert.All(rig.Updates, update => Assert.DoesNotContain(TextMarker, update.LogLine(), StringComparison.Ordinal));
        Assert.All(rig.Updates, update => Assert.DoesNotContain(TextMarker, update.Reason ?? string.Empty, StringComparison.Ordinal));
    }

    // ---- D09: Stand der Steuerung für die Pille -------------------------------------------------------------------

    [Fact]
    public async Task D09_FocusWaitOfTheBoundFieldIsShownAndOthersAreIgnored()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var bindingId = rig.Binding.Bound!.BindingId;

        rig.Output.RaiseFocusWait(new FocusWait(Guid.NewGuid(), TimeSpan.FromSeconds(30)));
        Assert.Null(rig.Controller.Current.FocusWait);

        rig.Output.RaiseFocusWait(new FocusWait(bindingId, TimeSpan.FromSeconds(30)));
        var wait = rig.Controller.Current.FocusWait;
        Assert.NotNull(wait);
        Assert.InRange(wait!.Value, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
        Assert.Contains("fokus_warten=ja", rig.Controller.Current.LogLine(), StringComparison.Ordinal);

        rig.Output.RaiseFocusWait(new FocusWait(bindingId, null));
        Assert.Null(rig.Controller.Current.FocusWait);
        await rig.EndAsync(run);
    }

    [Fact]
    public async Task D09_DictationsAndMessagesAreNumbered()
    {
        await using var rig = new ControllerRig();
        var before = rig.Controller.Current;

        var first = await rig.StartRecordingAsync();
        Assert.Equal(FakeAudioCapture.MicA.Name, rig.Controller.Current.Microphone);
        await rig.EndAsync(first);
        var second = await rig.StartRecordingAsync();
        await rig.Controller.CancelAsync();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        _ = second;

        var after = rig.Controller.Current;
        Assert.Equal(0, before.Dictation);
        Assert.Equal(2, after.Dictation);
        Assert.True(after.MessageNumber > before.MessageNumber, "Die Abbruchmeldung zählt");
        Assert.Null(after.Microphone);
    }

    // ---- Hilfen ---------------------------------------------------------------------------------------------------

    private static TextSegment Fixed(string text) =>
        new(Guid.NewGuid(), text, SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

    private static TextSegment Provisional(string text) =>
        new(Guid.NewGuid(), text, SegmentState.Provisional, TimeSpan.Zero, TimeSpan.FromSeconds(1));

    private static TextOutput Output(params TextSegment[] fixedSegments) => new(new RecognitionUpdate(fixedSegments, []));

    /// <summary>Sicherung im Arbeitsspeicher mit Testzeit und Attrappe statt DPAPI; Takt von Hand.</summary>
    private sealed class RecoveryRig
    {
        private readonly Lock _gate = new();

        public RecoveryRig()
        {
            Time = new ManualTimeProvider();
            Files = new InMemoryRecoveryFileSystem(Time);
            Store = new RecoveryStore(Files, Protector, Time);
        }

        public ManualTimeProvider Time { get; }

        public InMemoryRecoveryFileSystem Files { get; }

        public FakeDataProtector Protector { get; } = new();

        public RecoveryStore Store { get; }

        /// <summary>Ein Takt: sichert Geändertes (höchstens jede Sekunde, ER-06).</summary>
        public void Tick()
        {
            lock (_gate)
            {
                Time.Advance(RecoveryOptions.MaxSaveInterval);
            }
        }

        /// <summary>
        /// Stand der Text in einer gesicherten Fassung? Gelesen wird das JSON der Nutzlast, nicht ihr roher Text, weil JSON
        /// Umlaute als <c>ä</c> schreibt.
        /// </summary>
        public bool Saved(string text)
        {
            lock (_gate)
            {
                return Protector.PlaintextStrings.Any(plain => Strings(System.Text.Json.JsonDocument.Parse(plain).RootElement)
                    .Any(value => value.Contains(text, StringComparison.Ordinal)));
            }
        }

        private static IEnumerable<string> Strings(System.Text.Json.JsonElement element) => element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => [element.GetString()!],
            System.Text.Json.JsonValueKind.Object => element.EnumerateObject().SelectMany(property => Strings(property.Value)),
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
            _ => [],
        };
    }
}
