using Freihand.Adapters.Dictation;
using Freihand.Core.Live;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Beta Stufe 2: BT03 Mikrofon ab dem nächsten Diktat, BT04 Tagesprotokoll ohne Text
/// und mit 14 Tagen Aufbewahrung, BT05 „Problem merken“. Erfundene Texte, keine echten Geräte.
/// </summary>
public sealed class BetaStage2Tests
{
    private const string TextMarker = "PROTOKOLL-MARKE-8RT";

    // ---- BT03 Mikrofon --------------------------------------------------------------------------------------------

    [Fact]
    public async Task BT03_TheChoiceHoldsFromTheNextDictationAndARunningOneKeepsItsDevice()
    {
        await using var rig = new ControllerRig();

        rig.Controller.SelectDevice(FakeAudioCapture.MicB.Id);
        var first = await rig.StartRecordingAsync();
        rig.Controller.SelectDevice(null);
        Assert.Equal(FakeAudioCapture.MicB, rig.Audio.Sessions[0].Device);
        await rig.EndAsync(first);

        var second = await rig.StartRecordingAsync();
        Assert.Equal(FakeAudioCapture.MicA, rig.Audio.Sessions[1].Device);
        await rig.EndAsync(second);
        Assert.Null(rig.Controller.DeviceId);
    }

    [Fact]
    public async Task BT03_AMissingChosenDeviceIsReportedNeverReplaced()
    {
        await using var rig = new ControllerRig();
        rig.Controller.SelectDevice("mikro-ausgesteckt");

        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Contains(rig.Updates, update => update.Message == ControllerMessage.NoDevice && update.Reason == "Gerät");
        Assert.Equal(0, rig.Log.CountStarting("Capture.Open:"));
    }

    [Fact]
    public void BT03_TheMenuListsDefaultDevicesAndAMissingChoice()
    {
        IReadOnlyList<AudioDevice> devices = [FakeAudioCapture.MicA, FakeAudioCapture.MicB];

        var standard = MicrophoneMenu.Entries(devices, null);
        var missing = MicrophoneMenu.Entries(devices, "mikro-ausgesteckt");

        Assert.Equal([MicrophoneMenu.WindowsDefault, "Testmikrofon A", "Testmikrofon B"], standard.Select(entry => entry.Name));
        Assert.Equal([true, false, false], standard.Select(entry => entry.Selected));
        var last = missing[^1];
        Assert.Equal(new MicrophoneEntry("mikro-ausgesteckt", MicrophoneMenu.Missing, true, false), last);
        Assert.Single(missing, entry => entry.Selected);
    }

    [Fact]
    public void BT03_TheChoiceIsSavedInTheSettingsFile()
    {
        var folder = NewFolder();
        try
        {
            var path = Path.Combine(folder, "einstellungen.txt");
            PrototypeSettings.LoadOrCreate(path, PrototypeSettings.Defaults(folder));

            PrototypeSettings.SetValue(path, "geraet", FakeAudioCapture.MicB.Id);

            Assert.Contains("\ngeraet = mikro-b\n", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Equal("mikro-b", PrototypeSettings.LoadOrCreate(path, PrototypeSettings.Defaults(folder)).Device);
            PrototypeSettings.SetValue(path, "geraet", string.Empty);
            Assert.Null(PrototypeSettings.LoadOrCreate(path, PrototypeSettings.Defaults(folder)).Device);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Eine Wahl im Menü ändert nur ihre Zeile: Notizen, Kodierung und Zeilenenden bleiben
    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-8-bom")]
    public void BT03_AMenuChoiceChangesOnlyItsLine(string kind)
    {
        var folder = NewFolder();
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "einstellungen.txt");
            var encoding = kind == "utf-16" ? (System.Text.Encoding)System.Text.Encoding.Unicode : new System.Text.UTF8Encoding(true);
            File.WriteAllText(path, "# meine Notiz: Jabra = mikro-b\r\n  GERAET = alt\r\nton = aus\r\n", encoding);

            PrototypeSettings.SetValue(path, "geraet", "mikro-$1");
            PrototypeSettings.SetValue(path, "haltefrist", "30");

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(encoding.GetPreamble(), bytes[..encoding.GetPreamble().Length]);
            Assert.Equal("# meine Notiz: Jabra = mikro-b\r\ngeraet = mikro-$1\r\nton = aus\r\nhaltefrist = 30\r\n",
                encoding.GetString(bytes[encoding.GetPreamble().Length..]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Gleichartige Stelle zu Codex PR #56: Auch das Ergänzen fehlender Zeilen hält reine CR-Zeilenenden
    [Fact]
    public void BT03_MissingKeysAreAppendedWithTheFilesCrLineEnds()
    {
        var folder = NewFolder();
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "einstellungen.txt");
            File.WriteAllText(path, "# Notiz\rton = ein\r", new System.Text.UTF8Encoding(false));

            PrototypeSettings.LoadOrCreate(path, PrototypeSettings.Defaults(folder));
            var text = File.ReadAllText(path);

            Assert.StartsWith("# Notiz\rton = ein\r", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', text);
            Assert.Contains("\rproblem = Strg+Alt+B\r", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- BT04 Protokoll -------------------------------------------------------------------------------------------

    [Fact]
    public async Task BT04_TheDailyFileHasNoTextNoTargetAndOneFilePerDay()
    {
        var folder = NewFolder();
        try
        {
            var time = new FixedTime(new DateTimeOffset(2026, 11, 12, 9, 15, 2, TimeSpan.Zero));
            var log = new DailyLog(folder, time);
            await using (var rig = new ControllerRig(arrange: r => r.Binding.Next = BindingResult("Word – Dokumenttext")))
            {
                rig.Controller.Updated += (_, update) => log.Write(update.LogLine());
                var run = await rig.StartRecordingAsync();
                run.Emit(new TextOutput(new RecognitionUpdate(
                    [new TextSegment(Guid.NewGuid(), TextMarker, SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1))], [])));
                await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "geschrieben");
                await rig.EndAsync(run);
            }

            time.Now = time.Now.AddDays(1);
            log.Write("freihand start");

            var files = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToList();
            Assert.Equal(["2026-11-12.txt", "2026-11-13.txt"], files);
            var content = File.ReadAllText(Path.Combine(folder, "2026-11-12.txt"));
            Assert.Contains("phase=Recording", content, StringComparison.Ordinal);
            Assert.StartsWith("2026-11-12T09:15:02.000+00:00 phase=", content, StringComparison.Ordinal);
            Assert.DoesNotContain(TextMarker, content, StringComparison.Ordinal);
            Assert.DoesNotContain("Word", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void BT04_FilesOlderThan14DaysAreDeletedAndNothingElse()
    {
        var folder = NewFolder();
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var name in new[] { "2026-10-28.txt", "2026-10-29.txt", "2026-11-12.txt", "notiz.txt", "2026-10-01-kopie.txt" })
            {
                File.WriteAllText(Path.Combine(folder, name), "x");
            }

            var deleted = new DailyLog(folder, new FixedTime(new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero))).DeleteOld();

            Assert.Equal(1, deleted);
            Assert.Equal(["2026-10-01-kopie.txt", "2026-10-29.txt", "2026-11-12.txt", "notiz.txt"],
                Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void BT04_AWriteFailureNeverThrows()
    {
        var folder = NewFolder();
        try
        {
            // Der Ordner des Protokolls ist eine Datei: Schreiben scheitert still
            Directory.CreateDirectory(Path.GetDirectoryName(folder + "-datei")!);
            File.WriteAllText(folder + "-datei", "x");
            var log = new DailyLog(folder + "-datei", new FixedTime(DateTimeOffset.UtcNow));

            log.Write("phase=Idle");
            Assert.Equal(0, log.DeleteOld());
        }
        finally
        {
            File.Delete(folder + "-datei");
        }
    }

    [Fact]
    public void BT04_ALineBreakStaysOneLine()
    {
        var folder = NewFolder();
        try
        {
            var log = new DailyLog(folder, new FixedTime(new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero)));

            log.Write("eins\r\nzwei\nDREI");

            Assert.Single(File.ReadAllLines(Path.Combine(folder, "2026-11-12.txt")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- BT05 Problem merken --------------------------------------------------------------------------------------

    [Fact]
    public void BT05_EachMarkIsOneNumberedLineWithStateAndNoText()
    {
        var lines = new List<string>();
        var marker = new ProblemMarker(lines.Add, () => "phase=Idle meldung=None");

        Assert.Equal(1, marker.Mark("taste"));
        Assert.Equal(2, marker.Mark("menü"));

        Assert.Equal(["marke nr=1 quelle=taste phase=Idle meldung=None", "marke nr=2 quelle=menü phase=Idle meldung=None"], lines);
        Assert.Equal(1, new ProblemMarker(lines.Add, () => throw new InvalidOperationException()).Mark("taste"));
        Assert.Equal("marke nr=1 quelle=taste zustand=unbekannt", lines[^1]);
    }

    [Fact]
    public async Task BT05_TheKeyAsksForExactlyOneMark()
    {
        await using var rig = new ControllerRig();
        rig.Controller.RegisterHotkeys();
        var marks = 0;
        rig.Controller.MarkRequested += (_, _) => Interlocked.Increment(ref marks);

        rig.Hotkeys.Press(HotkeyAction.Mark);
        await rig.WaitUntil(() => Volatile.Read(ref marks) == 1, "Marke angefordert");

        Assert.Equal(1, Volatile.Read(ref marks));
    }

    [Fact]
    public void BT05_TheKeyIsCtrlAltBChangeableAndADuplicateIsReported()
    {
        var defaults = PrototypeSettings.Defaults("C:\\Freihand");
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x42), defaults.Gestures(out _)[HotkeyAction.Mark]);
        Assert.Contains("problem = Strg+Alt+B", PrototypeSettings.Write(defaults), StringComparison.Ordinal);

        var doubled = defaults with { Hotkeys = defaults.Hotkeys with { Mark = "Strg+Alt+D" } };
        var gestures = doubled.Gestures(out _, out var duplicates);

        Assert.Equal([new HotkeyDuplicate(HotkeyAction.Mark, HotkeyAction.DictationWindow)], duplicates);
        Assert.False(gestures.ContainsKey(HotkeyAction.Mark));
    }

    private static BindResult BindingResult(string display) =>
        new(new TargetInfo("winword", FieldKinds.DocumentBody, display, false) { BindingId = Guid.NewGuid() }, null);

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "freihand-beta2-" + Guid.NewGuid().ToString("N"));

    /// <summary>Zeit ohne Uhr, Ortszeit gleich UTC: so sind Dateinamen und Zeitstempel in jeder Zeitzone gleich.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
