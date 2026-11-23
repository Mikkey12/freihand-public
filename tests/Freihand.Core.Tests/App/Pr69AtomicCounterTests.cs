using System.Reflection;
using System.Text;
using Freihand.Adapters.Dictation;
using Xunit;

public sealed class Pr69AtomicCounterTests
{
    [Fact]
    public void AnEditorSavingWhileSettingsAreStagedDoesNotLoseItsChange()
    {
        var directory = Directory.CreateTempSubdirectory("freihand-pr69-");
        var path = Path.Combine(directory.FullName, "einstellungen.txt");
        try
        {
            File.WriteAllText(path, "modell = ggml-small.bin\nton = aus\n");
            var editorSaved = false;
            var encoding = new EditorDuringStaging(() =>
            {
                File.WriteAllText(path, "modell = ggml-small.bin\nton = ein\n");
                editorSaved = true;
            });
            var replace = typeof(PrototypeSettings).GetMethod("Replace", BindingFlags.NonPublic | BindingFlags.Static)!;
            replace.Invoke(null, [path, "modell = ggml-large-v3-turbo-q5_0.bin\nton = aus\n", encoding]);
            Assert.True(editorSaved);
            Assert.Contains("ton = ein", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    private sealed class EditorDuringStaging(Action save) : UTF8Encoding(false)
    {
        private bool _saved;

        public override Encoder GetEncoder()
        {
            SaveOnce();
            return base.GetEncoder();
        }

        public override byte[] GetPreamble()
        {
            SaveOnce();
            return base.GetPreamble();
        }

        private void SaveOnce()
        {
            if (!_saved)
            {
                _saved = true;
                save();
            }
        }
    }
}