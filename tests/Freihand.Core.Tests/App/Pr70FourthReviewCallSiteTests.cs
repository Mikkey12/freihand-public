using Freihand.Zielprobe;

namespace Freihand.Core.Tests.App;

public sealed class Pr70FourthReviewCallSiteTests
{
    [Fact]
    public async Task HintBackspaceDoesNotDeleteFromADraftRestoredAfterReadingTheProbe()
    {
        const string draft = "Entwurf";
        var field = new ProbeField("41.1", "", hint: "Eingeben", draftOnSecondEnd: draft);
        ProbeField.Current.Value = field;
        try
        {
            var (hint, _) = await Schreiben.HinweisAsync(field.RuntimeId);

            // Seit Sicherheitsprüfung (F5) tippt die Prüfung nie: weder Taste noch Zeichen, der Inhalt bleibt
            Assert.Null(hint);
            Assert.Equal(string.Empty, field.Raw);
            Assert.Equal(0, field.ClearCalls);
        }
        finally
        {
            ProbeField.Current.Value = null;
        }
    }

    [Fact]
    public async Task LeftoverParagraphsDoNotCountAsASuccessfulClear()
    {
        var field = new ProbeField("41.1", "", clearResult: "\r\n\r\n");
        ProbeField.Current.Value = field;
        var path = Path.Combine(Path.GetTempPath(), $"freihand-pr70-{Guid.NewGuid():N}.json");
        try
        {
            var options = Optionen.Lesen(["schreiben", "--wartezeit", "0", "--name", "test", "--out", path,
                "--messmodus", "--anker", field.RuntimeId, "--teile", "50", "--laenge", "250"]);
            var exit = await Schreiben.AusfuehrenAsync(options);

            Assert.Equal(1, field.ClearCalls);
            Assert.Equal("\r\n\r\n", field.Text);
            Assert.Equal(1, exit);
        }
        finally
        {
            ProbeField.Current.Value = null;
            File.Delete(path);
        }
    }
}