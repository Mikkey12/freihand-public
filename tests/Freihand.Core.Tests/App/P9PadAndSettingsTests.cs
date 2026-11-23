using Freihand.Adapters.Dictation;
using Freihand.Core.Platform;
using Freihand.Core.Recovery;
using Freihand.Core.Tests.Recovery;

namespace Freihand.Core.Tests.App;

/// <summary>
/// P9 Diktatfenster: Fügeregel an der Schreibmarke (D05), Sicherung der Schreibfläche
/// (Grundentscheid 2), Pegel der Pille und Einstellungen (D10: Ton, Tasten, doppelte Belegung). Erfundene Texte.
/// </summary>
public sealed class P9PadAndSettingsTests
{
    // ---- D05: Fügeregel an der Schreibmarke ----------------------------------------------------------------------

    [Theory]
    [InlineData("", 0, "Hallo", "Hallo")]
    [InlineData("Anfang", 6, "weiter", " weiter")]
    [InlineData("Anfang ", 7, "weiter", "weiter")]
    [InlineData("Zeile\n", 6, "Neu", "Neu")]
    [InlineData("Zeile\r\n", 7, "Neu", "Neu")]
    [InlineData("Satz", 4, ". Und", ". Und")]
    [InlineData("Satz", 4, ", dann", ", dann")]
    [InlineData("Anfang Ende", 6, "Mitte", " Mitte")]
    [InlineData("Anfang", 99, "weiter", " weiter")]
    [InlineData("Anfang", 3, "", "")]
    public void D05_InsertionFollowsTheJoinRuleWithTheOwnTextBeforeTheCaret(string text, int caret, string segment, string expected) =>
        Assert.Equal(expected, PadText.Insertion(text, caret, segment));

    [Fact]
    public void D05_LineBreaksOfThePadBecomeLineFeeds() =>
        Assert.Equal("a\nb\nc", PadText.ToCore("a\r\nb\rc"));

    // ---- Pegel ------------------------------------------------------------------------------------------------------

    [Fact]
    public void LevelIsZeroForSilenceAndHighForAFullScaleSignal()
    {
        Assert.Equal(0, AudioLevel.Of([]));
        Assert.Equal(0, AudioLevel.Of(new byte[640]));
        var loud = new byte[640];
        for (var index = 0; index < loud.Length; index += 2)
        {
            var sample = (short)(index % 4 == 0 ? short.MaxValue : short.MinValue + 1);
            loud[index] = (byte)sample;
            loud[index + 1] = (byte)(sample >> 8);
        }

        Assert.InRange(AudioLevel.Of(loud), 0.95f, 1f);
        var quiet = new byte[640];
        for (var index = 0; index < quiet.Length; index += 2)
        {
            // −40 dBFS: kaum sichtbar, aber nicht leer
            var sample = (short)(index % 4 == 0 ? 328 : -328);
            quiet[index] = (byte)sample;
            quiet[index + 1] = (byte)(sample >> 8);
        }

        Assert.InRange(AudioLevel.Of(quiet), 0.1f, 0.3f);
    }

    // ---- Sicherung der Schreibfläche ----------------------------------------------------------------------------------

    [Fact]
    public void PadTextIsSavedWithinASecondAndOfferedAfterARestart()
    {
        var (time, files, protector, store) = Store();
        using var pad = new PadDraft(store);

        pad.Changed("Notiz für später");
        time.Advance(RecoveryOptions.MaxSaveInterval);

        // Harter Abbruch: kein Dispose, kein letztes Sichern
        using var restart = new RecoveryStore(files, protector, time);
        var draft = Assert.Single(restart.Scan().Drafts);
        Assert.Equal("Notiz für später", draft.Text);
        Assert.Null(draft.HandedOverAt);
    }

    [Fact]
    public void HandedOverStartsTheHoldPeriodAndNewTextGoesIntoANewDraft()
    {
        var (time, _, _, store) = Store();
        using var pad = new PadDraft(store);
        pad.Changed("Erster Text");
        var first = pad.DraftId;

        pad.HandedOver();
        pad.Changed("Zweiter Text");

        Assert.NotNull(first);
        Assert.NotEqual(first, pad.DraftId);
        var handedOver = Assert.Single(store.Scan().Drafts);
        Assert.Equal(first, handedOver.Id);
        Assert.NotNull(handedOver.HandedOverAt);
        time.Advance(RecoveryOptions.DefaultHoldPeriod + TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(store.Scan().Drafts, draft => draft.Id == first);
    }

    [Fact]
    public void ClearingDeletesTheSavedTextAndEmptyTextIsNotOffered()
    {
        var (time, files, _, store) = Store();
        using var pad = new PadDraft(store);
        pad.Changed("Weg damit");
        time.Advance(RecoveryOptions.MaxSaveInterval);
        Assert.NotEmpty(files.Files);

        pad.Cleared();

        Assert.Empty(files.Files);
        pad.Changed("Neu");
        pad.Changed(string.Empty);
        time.Advance(RecoveryOptions.MaxSaveInterval);
        Assert.Empty(files.Files);
    }

    [Fact]
    public void DisposeKeepsThePadTextForTheNextStart()
    {
        var (time, files, protector, store) = Store();
        var pad = new PadDraft(store);
        pad.Changed("Noch nicht fertig");

        pad.Dispose();

        using var restart = new RecoveryStore(files, protector, time);
        Assert.Equal("Noch nicht fertig", Assert.Single(restart.Scan().Drafts).Text);
    }

    [Fact]
    public void ASaveProblemIsReportedByKindAndNeverThrows()
    {
        var (_, files, _, store) = Store();
        using var pad = new PadDraft(store);
        var reported = new List<string>();
        pad.Failed += (_, kind) => reported.Add(kind);
        pad.Changed("Text");
        files.FailWrites = true;

        Assert.False(pad.Flush());
        Assert.Equal([nameof(RecoveryIssueKind.WriteFailed)], reported);
    }

    [Fact]
    public void WithoutAStoreNothingHappens()
    {
        using var pad = new PadDraft(null);
        pad.Changed("Text");
        Assert.True(pad.Flush());
        pad.HandedOver();
        pad.Cleared();
        Assert.Null(pad.DraftId);
    }

    // ---- D10: Einstellungen -------------------------------------------------------------------------------------------

    [Fact]
    public void D10_DefaultsHaveTheWindowKeySoundOffAndRecoveryOn()
    {
        var defaults = PrototypeSettings.Defaults("C:\\Freihand");

        var gestures = defaults.Gestures(out var invalid, out var duplicates);

        Assert.Empty(invalid);
        Assert.Empty(duplicates);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x44), gestures[HotkeyAction.DictationWindow]);
        Assert.False(defaults.Sound);
        Assert.True(defaults.Recovery);
        Assert.Equal(15, defaults.HoldMinutes);
    }

    [Fact]
    public void D10_ADuplicateIsReportedAndNotSilentlyApplied()
    {
        var settings = PrototypeSettings.Defaults("C:\\Freihand") with
        {
            Hotkeys = new HotkeyTexts { DictationWindow = "Strg+Alt+Y", Cancel = "ctrl+alt+x" },
        };

        var gestures = settings.Gestures(out var invalid, out var duplicates);

        Assert.Empty(invalid);
        Assert.Equal(
            [new HotkeyDuplicate(HotkeyAction.Cancel, HotkeyAction.Command), new HotkeyDuplicate(HotkeyAction.DictationWindow, HotkeyAction.Record)],
            duplicates);
        Assert.Equal([HotkeyAction.Record, HotkeyAction.Command, HotkeyAction.Correct, HotkeyAction.Smooth, HotkeyAction.Mark], gestures.Keys.Order());
    }

    [Fact]
    public void D10_AnUnreadableKeyIsReported()
    {
        var settings = PrototypeSettings.Defaults("C:\\Freihand") with { Hotkeys = new HotkeyTexts { DictationWindow = "Strg+Ä" } };

        var gestures = settings.Gestures(out var invalid, out var duplicates);

        Assert.Equal([HotkeyAction.DictationWindow], invalid);
        Assert.Empty(duplicates);
        Assert.False(gestures.ContainsKey(HotkeyAction.DictationWindow));
    }

    [Theory]
    [InlineData("ton = ein\nsicherung = aus\nhaltefrist = 0\ndiktatfenster = Umschalt+F9\n", true, false, 0, "Umschalt+F9")]
    [InlineData("ton = JA\nsicherung = nein\nhaltefrist = 1440\n", true, false, 1440, "Strg+Alt+D")]
    [InlineData("ton = vielleicht\nsicherung = egal\nhaltefrist = 1441\n", false, true, 15, "Strg+Alt+D")]
    [InlineData("haltefrist = -5\n", false, true, 15, "Strg+Alt+D")]
    public void D10_SoundRecoveryHoldAndWindowKeyAreReadFromTheFile(
        string content, bool sound, bool recovery, int hold, string windowKey)
    {
        var folder = Path.Combine(Path.GetTempPath(), "freihand-p9-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "einstellungen.txt");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, content);

            var read = PrototypeSettings.LoadOrCreate(path, PrototypeSettings.Defaults(folder));

            Assert.Equal(sound, read.Sound);
            Assert.Equal(recovery, read.Recovery);
            Assert.Equal(hold, read.HoldMinutes);
            Assert.Equal(windowKey, read.Hotkeys.DictationWindow);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void D10_SavingTheSoundSwitchKeepsEverythingElse()
    {
        var folder = Path.Combine(Path.GetTempPath(), "freihand-p9-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "einstellungen.txt");
        try
        {
            var defaults = PrototypeSettings.Defaults(folder) with
            {
                Device = "geraet-7",
                HoldMinutes = 30,
                Hotkeys = new HotkeyTexts { DictationWindow = "Umschalt+F9" },
            };
            PrototypeSettings.LoadOrCreate(path, defaults);

            PrototypeSettings.Save(path, PrototypeSettings.LoadOrCreate(path, defaults) with { Sound = true });
            var read = PrototypeSettings.LoadOrCreate(path, PrototypeSettings.Defaults("anders"));

            Assert.True(read.Sound);
            Assert.Equal("geraet-7", read.Device);
            Assert.Equal(30, read.HoldMinutes);
            Assert.Equal("Umschalt+F9", read.Hotkeys.DictationWindow);
            Assert.Contains("ton = ein", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static (ManualTimeProvider Time, InMemoryRecoveryFileSystem Files, FakeDataProtector Protector, RecoveryStore Store) Store()
    {
        var time = new ManualTimeProvider();
        var files = new InMemoryRecoveryFileSystem(time);
        var protector = new FakeDataProtector();
        return (time, files, protector, new RecoveryStore(files, protector, time));
    }
}
