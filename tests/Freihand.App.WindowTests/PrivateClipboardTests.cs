using System.IO;
using System.Windows;

namespace Freihand.App;

/// <summary>
/// Kopierter Text nur in die aktuelle Zwischenablage, nie in Verlauf oder Cloud (Prüfung der IT-Unterlagen,
/// 22.11.2026). Geprüft wird der Inhalt, den Freihand übergibt; die Zwischenablage des Rechners bleibt unberührt.
/// </summary>
public sealed class PrivateClipboardTests
{
    [Fact]
    public void CopiedTextCarriesTheFormatsThatExcludeHistoryAndCloud()
    {
        var data = PrivateClipboard.Create("Erfundener Testsatz");

        Assert.Equal("Erfundener Testsatz", data.GetText(TextDataFormat.UnicodeText));
        foreach (var format in new[] { PrivateClipboard.ExcludeFromMonitor, PrivateClipboard.HistoryFormat, PrivateClipboard.CloudFormat })
        {
            var stream = Assert.IsType<MemoryStream>(data.GetData(format));
            Assert.Equal(BitConverter.GetBytes(0), stream.ToArray());
        }
    }

    // Strg+C, Strg+X und Kontextmenü in Textfeldern: dieselben Formate; im Profil „Streng“ abgebrochen (Vorprüfung 22.11.2026)
    [Fact]
    public void CopyingFromATextFieldGetsTheSameFormatsAndIsCancelledWhenStrict()
    {
        try
        {
            PrivateClipboard.Register(copyAllowed: true);
            var allowed = new DataObjectCopyingEventArgs(new DataObject(DataFormats.UnicodeText, "Erfundener Satz"), isDragDrop: false);
            PrivateClipboard.OnCopying(this, allowed);

            Assert.False(allowed.CommandCancelled);
            foreach (var format in new[] { PrivateClipboard.ExcludeFromMonitor, PrivateClipboard.HistoryFormat, PrivateClipboard.CloudFormat })
            {
                Assert.Equal(BitConverter.GetBytes(0), Assert.IsType<MemoryStream>(allowed.DataObject.GetData(format)).ToArray());
            }

            PrivateClipboard.Register(copyAllowed: false);
            var strict = new DataObjectCopyingEventArgs(new DataObject(DataFormats.UnicodeText, "Erfundener Satz"), isDragDrop: false);
            PrivateClipboard.OnCopying(this, strict);

            Assert.True(strict.CommandCancelled);
        }
        finally
        {
            PrivateClipboard.Register(copyAllowed: true);
        }
    }

    // Regel für jede Stelle: keine Kopierstelle der App oder des Messwerkzeugs legt Text ohne diese Formate ab
    [Fact]
    public void NoCopyPointBypassesThePrivateClipboard()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Freihand.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var sources = new[] { "src", Path.Combine("tools", "Freihand.Zielprobe") }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(root!.FullName, folder), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.EndsWith("PrivateClipboard.cs", StringComparison.Ordinal));
        foreach (var path in sources)
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("Clipboard.SetText(", text.Replace("PrivateClipboard.SetText(", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain("Clipboard.SetDataObject(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SetClipboardData", text, StringComparison.Ordinal);
        }
    }
}
