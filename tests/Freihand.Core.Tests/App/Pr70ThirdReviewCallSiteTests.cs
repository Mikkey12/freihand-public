using Freihand.Zielprobe;

namespace Freihand.Core.Tests.App;

public sealed class Pr70ThirdReviewCallSiteTests
{
    [Fact]
    public async Task HintMayTypeOneCharacterButCannotDeleteUserTextWhenControlEndIsIgnored()
    {
        const string original = "Beispiel";
        var field = new ProbeField("41.1", original, ignoresControlEnd: true, caretAfterInsert: 2);
        ProbeField.Current.Value = field;
        try
        {
            var (hint, _) = await Schreiben.HinweisAsync(field.RuntimeId);

            Assert.Null(hint);
            Assert.Contains(field.Text, new[] { original, original + Schreiben.Pruefzeichen });
        }
        finally
        {
            ProbeField.Current.Value = null;
        }
    }

    [Fact]
    public async Task AProvenHintDoesNotAuthorizeNewRawContentWithTrailingParagraphs()
    {
        var hint = Schreiben.Pruefzeichen.ToString();
        var newContent = hint + "\r\n\r\n";
        var field = new ProbeField("41.1", newContent);
        ProbeField.Current.Value = field;
        var path = Path.Combine(Path.GetTempPath(), $"freihand-pr70-{Guid.NewGuid():N}.json");
        try
        {
            var options = Optionen.Lesen(["schreiben", "--wartezeit", "0", "--name", "test", "--out", path,
                "--messmodus", "--anker", field.RuntimeId, "--teile", "50", "--laenge", "250"]);
            await Schreiben.AusfuehrenAsync(options, hinweistext: hint);

            Assert.Equal(newContent, field.Text);
            Assert.Equal(0, field.ClearCalls);
        }
        finally
        {
            ProbeField.Current.Value = null;
            File.Delete(path);
        }
    }
}