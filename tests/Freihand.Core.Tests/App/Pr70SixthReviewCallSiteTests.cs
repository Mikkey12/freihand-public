using Freihand.Zielprobe;

namespace Freihand.Core.Tests.App;

public sealed class Pr70SixthReviewCallSiteTests
{
    [Fact]
    public async Task HintBackspaceCannotReachAFieldFocusedDuringTheFinalContentRead()
    {
        const string draft = "Entwurf";
        var otherField = new ProbeField("41.2", draft);
        var measuredField = new ProbeField("41.1", "", hint: "Eingeben",
            focusTargetOnRead: otherField, moveFocusAtRead: 4);
        ProbeField.Current.Value = measuredField;
        try
        {
            var (hint, _) = await Schreiben.HinweisAsync(measuredField.RuntimeId);

            Assert.Null(hint);
            Assert.Equal(draft, otherField.Text);
        }
        finally
        {
            ProbeField.Current.Value = null;
        }
    }
}