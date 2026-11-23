using Freihand.Adapters.Dictation;
using Freihand.Adapters.Transform;
using Freihand.Core.Platform;
using Freihand.Core.Routing;
using Freihand.Core.Transform;

namespace Freihand.Core.Tests.App;

/// <summary>
/// BT09: Korrigieren und Glätten einer Markierung in einem anderen Programm. Die Taste
/// liest Feld und Markierung beim Druck, ersetzt wird nur die unveränderte Markierung, Ränder bleiben, nur Fliesstext,
/// nie ein Text im Protokoll. Erfundene Texte, keine echten Aufrufe.
/// </summary>
public sealed class BetaSelectionCorrectionTests
{
    private const string ForeignMarker = "FREMDTEXT-MARKE-3KX";

    // ---- Regel für die gelesene Markierung ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Ein Satz.\r\nZweiter Satz.\r", "Ein Satz.\nZweiter Satz.\n")]
    [InlineData("Spalte\tWert", "Spalte\tWert")]
    public void FlowingTextIsReadWithUnifiedLineBreaksAndConfidential(string raw, string expected)
    {
        var read = SelectionText.From(raw, ReadPurpose.ExplicitCommand, DataClass.Confidential);

        Assert.Equal(SelectionRefusal.None, read.Refusal);
        Assert.Equal(expected, read.Text!.Reveal());
        Assert.Equal(DataClass.Confidential, read.Text.DataClass);
        Assert.Equal(ReadPurpose.ExplicitCommand, read.Text.Purpose);
        Assert.Equal("[Fremdtext]", read.Text.ToString());
    }

    [Theory]
    [InlineData(null, SelectionRefusal.Unreadable)]
    [InlineData("", SelectionRefusal.NoSelection)]
    [InlineData(" \r\n\t", SelectionRefusal.NoSelection)]
    [InlineData("Zelle eins\aZelle zwei\a", SelectionRefusal.SpecialContent)]
    [InlineData("Zeile\vmanueller Umbruch", SelectionRefusal.SpecialContent)]
    [InlineData("Bild \uFFFC im Text", SelectionRefusal.SpecialContent)]
    [InlineData("Feld\u0013Code\u0015", SelectionRefusal.SpecialContent)]
    public void AnythingButFlowingTextIsRefused(string? raw, SelectionRefusal expected)
    {
        var read = SelectionText.From(raw, ReadPurpose.ExplicitCommand, DataClass.Confidential);

        Assert.Null(read.Text);
        Assert.Equal(expected, read.Refusal);
    }

    [Fact]
    public void ALongSelectionIsRefusedNotCut()
    {
        Assert.Equal(SelectionRefusal.None,
            SelectionText.From(new string('a', SelectionText.MaxLength), ReadPurpose.ExplicitCommand, DataClass.Confidential).Refusal);
        Assert.Equal(SelectionRefusal.TooLong,
            SelectionText.From(new string('a', SelectionText.MaxLength + 1), ReadPurpose.ExplicitCommand, DataClass.Confidential).Refusal);
    }

    // ---- Vorschlag: Ränder bleiben, Einstufung geht mit ---------------------------------------------------------------

    [Fact]
    public async Task EdgesStayAsTheyWereAndOnlyTheCoreIsSent()
    {
        var fake = new FakeTransformer(_ => "\n  Ein Satz mit Fehler.  \n");
        var service = new CorrectionService(() => fake, text => text, TimeSpan.FromSeconds(5));

        var suggestion = await service.SuggestAsync("  Ein Satz mit Feler.\r\n", CorrectionKind.Correct, dataClass: DataClass.Confidential);

        var request = Assert.Single(fake.Requests);
        Assert.Equal("Ein Satz mit Feler.", request.Text);
        Assert.Equal(DataClass.Confidential, request.DataClass);
        Assert.Equal("  Ein Satz mit Fehler.\n", suggestion.Text);
        Assert.Equal("  Ein Satz mit Feler.\n", suggestion.Original);
    }

    [Fact]
    public async Task TextFromThePadStaysInternal()
    {
        var fake = new FakeTransformer(_ => "Satz.");
        await new CorrectionService(() => fake, text => text, TimeSpan.FromSeconds(5)).SuggestAsync("Satz", CorrectionKind.Smooth);

        Assert.Equal(DataClass.Internal, Assert.Single(fake.Requests).DataClass);
    }

    [Fact]
    public async Task AnEmptyAnswerIsNoSuggestion()
    {
        var service = new CorrectionService(() => new FakeTransformer(_ => " \n "), text => text, TimeSpan.FromSeconds(5));

        var failure = await Assert.ThrowsAsync<TransformFailedException>(() => service.SuggestAsync("Ein Satz.", CorrectionKind.Smooth));

        Assert.Equal(TransformFailure.Rejected, failure.Failure);
    }

    // ---- Taste: Feld und Markierung beim Druck ------------------------------------------------------------------------

    [Theory]
    [InlineData(HotkeyAction.Correct, CorrectionKind.Correct)]
    [InlineData(HotkeyAction.Smooth, CorrectionKind.Smooth)]
    public async Task TheKeyMarksTheFieldAndReadsItsSelection(HotkeyAction action, CorrectionKind kind)
    {
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Binding.NextMark = new MarkResult(FakeBinding.NewMark("Word – Dokumenttext"), null);
            r.Foreign.Select("Ein Satz mit Feler.");
        });
        rig.Controller.RegisterHotkeys();
        var requests = Collect(rig.Controller);

        rig.Hotkeys.Press(action);
        await rig.WaitUntil(() => { lock (requests) { return requests.Count == 1; } }, "Anfrage fürs Korrigieren");

        var request = requests[0];
        Assert.Equal(kind, request.Kind);
        Assert.False(request.FromPad);
        Assert.Equal("Word – Dokumenttext", request.Mark.Mark!.Display);
        Assert.Equal("Ein Satz mit Feler.", request.Selection.Text!.Reveal());
        Assert.True(rig.Log.IndexOf("Binding.MarkFocused:Standard") < rig.Log.IndexOf("Foreign.ReadSelection:ExplicitCommand"));
        Assert.Null(rig.Binding.Bound);
    }

    [Fact]
    public async Task FromThePadNothingIsMarkedOrRead()
    {
        await using var rig = new ControllerRig(arrange: r => r.Pad = new FakePad(r.Log) { Focused = true });
        rig.Controller.RegisterHotkeys();
        var requests = Collect(rig.Controller);

        rig.Hotkeys.Press(HotkeyAction.Correct);
        await rig.WaitUntil(() => { lock (requests) { return requests.Count == 1; } }, "Anfrage fürs Korrigieren");

        Assert.True(requests[0].FromPad);
        Assert.Null(requests[0].Mark.Mark);
        Assert.Equal(0, rig.Foreign.Reads);
        Assert.False(rig.Log.Contains("Binding.MarkFocused:Standard"));
    }

    [Fact]
    public async Task WithoutAFieldNothingIsRead()
    {
        await using var rig = new ControllerRig(arrange: r => r.Binding.NextMark = new MarkResult(null, BindRefusal.PasswordField));
        rig.Controller.RegisterHotkeys();
        var requests = Collect(rig.Controller);

        rig.Hotkeys.Press(HotkeyAction.Smooth);
        await rig.WaitUntil(() => { lock (requests) { return requests.Count == 1; } }, "Anfrage fürs Glätten");

        Assert.Equal(BindRefusal.PasswordField, requests[0].Mark.Refusal);
        Assert.Null(requests[0].Selection.Text);
        Assert.Equal(0, rig.Foreign.Reads);
    }

    // ---- Ersetzen nur der unveränderten Markierung --------------------------------------------------------------------

    [Fact]
    public async Task TheUnchangedSelectionIsReplaced()
    {
        await using var rig = new ControllerRig(arrange: r => r.Foreign.Select("Ein Satz mit Feler.\n"));

        var report = await rig.Controller.HandOverAsync("Ein Satz mit Fehler.\n", FakeBinding.NewMark(), "Ein Satz mit Feler.\r\n")
            .WaitAsync(ControllerRig.Timeout);

        Assert.Equal(HandOverResult.Written, report.Result);
        Assert.Equal(["Ein Satz mit Fehler.\n"], rig.Output.Texts);
        Assert.True(rig.Log.Contains("Output.Condition:True"), "Die Ausgabe prüft die Markierung vor dem ersten Zeichen");
        // Erst gebunden, dann die Markierung geprüft, dann geschrieben, dann gelöst
        Assert.True(rig.Log.IndexOf("Foreign.ReadSelection:ExplicitCommand") < rig.Log.IndexOf("Output.Done:Ein Satz mit Fehler.\n"));
        Assert.True(rig.Log.IndexOf("Binding.Release") > rig.Log.IndexOf("Output.Done:Ein Satz mit Fehler.\n"));
    }

    [Theory]
    [InlineData("Ein anderer Satz.")]
    [InlineData("")]
    public async Task AChangedOrLostSelectionReplacesNothing(string now)
    {
        await using var rig = new ControllerRig(arrange: r => r.Foreign.Select(now));

        var report = await rig.Controller.HandOverAsync("Ein Satz mit Fehler.", FakeBinding.NewMark(), "Ein Satz mit Feler.")
            .WaitAsync(ControllerRig.Timeout);

        Assert.Equal(HandOverResult.Refused, report.Result);
        Assert.Equal("SelectionChanged", report.Reason);
        Assert.Equal("Ein Satz mit Fehler.", report.Held);
        Assert.Empty(rig.Output.Calls);
        Assert.True(rig.Log.Contains("Binding.Release"));
    }

    [Fact]
    public async Task ASelectionIsNeverReplacedByNothing()
    {
        await using var rig = new ControllerRig(arrange: r => r.Foreign.Select("Ein Satz."));

        var report = await rig.Controller.HandOverAsync(" \n", FakeBinding.NewMark(), "Ein Satz.").WaitAsync(ControllerRig.Timeout);

        Assert.Equal(HandOverResult.Refused, report.Result);
        Assert.Equal("Empty", report.Reason);
        Assert.Empty(rig.Output.Calls);
        Assert.Equal(0, rig.Binding.MarkedBinds);
    }

    [Fact]
    public async Task TheForeignTextNeverReachesTheLogs()
    {
        await using var rig = new ControllerRig(arrange: r => r.Foreign.Select(ForeignMarker, ForeignMarker + " anders"));
        rig.Controller.RegisterHotkeys();
        var requests = Collect(rig.Controller);
        rig.Hotkeys.Press(HotkeyAction.Correct);
        await rig.WaitUntil(() => { lock (requests) { return requests.Count == 1; } }, "Anfrage fürs Korrigieren");

        await rig.Controller.HandOverAsync(ForeignMarker + " korrigiert", FakeBinding.NewMark(), ForeignMarker).WaitAsync(ControllerRig.Timeout);

        Assert.All(rig.LogLines, line => Assert.DoesNotContain(ForeignMarker, line, StringComparison.Ordinal));
        Assert.All(rig.Updates, update => Assert.DoesNotContain(ForeignMarker, update.LogLine(), StringComparison.Ordinal));
        Assert.All(rig.Updates, update => Assert.DoesNotContain(ForeignMarker, update.Reason ?? string.Empty, StringComparison.Ordinal));
    }

    // ---- Tasten in den Einstellungen ----------------------------------------------------------------------------------

    [Fact]
    public void TheKeysAreCtrlAltFAndGForTheLeftHandAndChangeable()
    {
        var gestures = PrototypeSettings.Defaults("C:\\Freihand").Gestures(out var invalid, out var duplicates);
        Assert.Empty(invalid);
        Assert.Empty(duplicates);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x46), gestures[HotkeyAction.Correct]);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x47), gestures[HotkeyAction.Smooth]);

        var folder = Path.Combine(Path.GetTempPath(), "freihand-b9-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "einstellungen.txt");
            File.WriteAllText(path, "korrigieren = Umschalt+F7\nglaetten = Umschalt+F8\n");
            var loaded = PrototypeSettings.LoadOrCreate(path, PrototypeSettings.Defaults(folder));
            Assert.Equal("Umschalt+F7", loaded.Hotkeys.Correct);
            Assert.Equal("Umschalt+F8", loaded.Hotkeys.Smooth);
            var written = PrototypeSettings.Write(PrototypeSettings.Defaults(folder));
            Assert.Contains("korrigieren = Strg+Alt+F", written, StringComparison.Ordinal);
            Assert.Contains("glaetten = Strg+Alt+G", written, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AMissingKnownKeyIsAppendedAndEverythingElseStays()
    {
        var folder = Path.Combine(Path.GetTempPath(), "freihand-b9-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "einstellungen.txt");
            // Stand vor dem Update: ohne die neuen Tasten und ohne Modell fürs Korrigieren, mit eigener Notiz
            var before = "# eigene Notiz\naufnahme = Strg+Alt+Y\nton = ein\nunbekannt = 1";
            File.WriteAllText(path, before);

            var loaded = PrototypeSettings.LoadOrCreate(path, PrototypeSettings.Defaults(folder));
            var after = File.ReadAllText(path);

            Assert.StartsWith(before + "\n", after, StringComparison.Ordinal);
            Assert.Contains("\nkorrigieren = Strg+Alt+F\nglaetten = Strg+Alt+G\n", after, StringComparison.Ordinal);
            Assert.Contains("# Markierten Text", after, StringComparison.Ordinal);
            Assert.Contains("\nkorrektur_modell = claude-haiku-5-5\n", after, StringComparison.Ordinal);
            Assert.DoesNotContain("\naufnahme = ", after[before.Length..], StringComparison.Ordinal);
            Assert.DoesNotContain("\nton = ", after[before.Length..], StringComparison.Ordinal);
            Assert.True(loaded.Sound);

            // Ein zweiter Start ändert nichts mehr
            PrototypeSettings.LoadOrCreate(path, PrototypeSettings.Defaults(folder));
            Assert.Equal(after, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static List<CorrectionRequest> Collect(DictationController controller)
    {
        var requests = new List<CorrectionRequest>();
        controller.CorrectionRequested += (_, request) =>
        {
            lock (requests)
            {
                requests.Add(request);
            }
        };
        return requests;
    }

    private sealed class FakeTransformer(Func<string, string> answer) : ITextTransformer
    {
        public List<TransformRequest> Requests { get; } = [];

        public TransformRoute Route => TransformRoute.PrivateProvider;

        public Task<TransformResult> TransformAsync(TransformRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new TransformResult(answer(request.Text)));
        }
    }
}
