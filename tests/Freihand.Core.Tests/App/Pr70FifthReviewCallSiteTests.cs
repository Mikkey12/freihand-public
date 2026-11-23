using Freihand.Zielprobe;

namespace Freihand.Core.Tests.App;

public sealed class Pr70FifthReviewCallSiteTests
{
    [Fact]
    public async Task HintDoesNotDeleteADraftLoadedDuringTheFinalFocusQuery()
    {
        const string draft = "Entwurf";
        var field = new ProbeField("41.1", "", hint: "Eingeben",
            draftOnFocusAfterRead: draft, changeOnFocusAfterRead: 3);
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
}