using Freihand.Adapters.Dictation;
using Freihand.Core.Platform;

namespace Freihand.Core.Tests.App;

/// <summary>Einstellungsdatei des Prototyps (D2, D6 im Konzept des Durchstichs); keine Texte, Tasten je Person änderbar.</summary>
public sealed class PrototypeSettingsTests
{
    [Theory]
    [InlineData("Strg+Alt+Y", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x59)]
    [InlineData("ctrl + alt + q", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x51)]
    [InlineData("Umschalt+F8", HotkeyModifiers.Shift, 0x77)]
    [InlineData("F24", HotkeyModifiers.None, 0x87)]
    [InlineData("Pause", HotkeyModifiers.None, 0x13)]
    [InlineData("Win+Leertaste", HotkeyModifiers.Windows, 0x20)]
    [InlineData("Strg+Alt+5", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x35)]
    public void ReadableGesturesAreParsed(string text, HotkeyModifiers modifiers, int key)
    {
        Assert.Equal(new HotkeyGesture(modifiers, key), PrototypeSettings.ParseGesture(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Strg+Alt")]
    [InlineData("Strg+Strg+Y")]
    [InlineData("Hyper+Y")]
    [InlineData("Strg+F25")]
    [InlineData("Strg+Ä")]
    public void UnreadableGesturesGiveNull(string? text)
    {
        Assert.Null(PrototypeSettings.ParseGesture(text));
    }

    [Fact]
    public void UnavailableActionsTakeNoPartInReadingOrTheDuplicateCheck()
    {
        // Codex-Prüfung PR #57: In der Geschäftsausgabe fehlen Korrigieren und Glätten; sie dürfen weder die Problemtaste
        // verdrängen noch als nicht lesbar gemeldet werden
        var settings = PrototypeSettings.Defaults("C:\\Freihand") with
        {
            Hotkeys = new HotkeyTexts { Correct = "Strg+Alt+B", Mark = "Strg+Alt+B", Smooth = "Strg+?" },
        };

        var gestures = settings.Gestures([HotkeyAction.Correct, HotkeyAction.Smooth], out var invalid, out var duplicates);

        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x42), gestures[HotkeyAction.Mark]);
        Assert.DoesNotContain(HotkeyAction.Correct, gestures.Keys);
        Assert.DoesNotContain(HotkeyAction.Smooth, gestures.Keys);
        Assert.Empty(invalid);
        Assert.Empty(duplicates);

        // Ohne Ausschluss gilt weiter die Reihenfolge: Korrigieren kommt vor Problem merken
        settings.Gestures(out var allInvalid, out var allDuplicates);
        Assert.Equal([HotkeyAction.Smooth], allInvalid);
        Assert.Equal(HotkeyAction.Mark, Assert.Single(allDuplicates).Action);
    }

    [Fact]
    public void DefaultsAreTheProposalFromP7AndUnreadableEntriesAreReported()
    {
        var defaults = PrototypeSettings.Defaults("C:\\Freihand");
        var gestures = defaults.Gestures(out var invalid);
        Assert.Empty(invalid);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x59), gestures[HotkeyAction.Record]);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x58), gestures[HotkeyAction.Command]);
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x51), gestures[HotkeyAction.Cancel]);

        var broken = defaults with { Hotkeys = new HotkeyTexts { Command = "Strg+?" } };
        var some = broken.Gestures(out var failed);
        Assert.Equal([HotkeyAction.Command], failed);
        Assert.Equal(6, some.Count);
    }

    [Fact]
    public void MissingFileIsCreatedWithDefaultsAndReadBack()
    {
        var folder = Path.Combine(Path.GetTempPath(), "freihand-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "einstellungen.txt");
        try
        {
            var defaults = PrototypeSettings.Defaults(folder) with { Device = "geraet-1" };
            var created = PrototypeSettings.LoadOrCreate(path, defaults);
            Assert.True(File.Exists(path));
            Assert.Equal(defaults, created with { Runtimes = defaults.Runtimes, Hotkeys = defaults.Hotkeys });

            var read = PrototypeSettings.LoadOrCreate(path, PrototypeSettings.Defaults("anders"));
            Assert.Equal(defaults.Model, read.Model);
            Assert.Equal("geraet-1", read.Device);
            Assert.Equal(defaults.Runtimes, read.Runtimes);
            Assert.Equal(defaults.Hotkeys, read.Hotkeys);

            // Eigene Belegung und leeres Gerät aus der Datei
            File.WriteAllText(path, "# Kommentar\naufnahme = Umschalt+F8\ngeraet =\nthreads = acht\nunbekannt = 1\n");
            var own = PrototypeSettings.LoadOrCreate(path, defaults);
            Assert.Equal("Umschalt+F8", own.Hotkeys.Record);
            Assert.Equal(defaults.Hotkeys.Cancel, own.Hotkeys.Cancel);
            Assert.Null(own.Device);
            Assert.Equal(defaults.Threads, own.Threads);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
