using Freihand.Core.Recognition;

namespace Freihand.Core.Tests.Live;

/// <summary>Verträge aus dem P3-Konzept, Kapitel 4; das Verhalten prüfen später T01–T68.</summary>
public class ContractTests
{
    [Fact]
    public void ExistingCallersOfRecognitionUpdateGiveNoCompletion()
    {
        // Bestehende Aufrufer bleiben gültig: ohne Angabe gibt es keine Zusage
        Assert.Null(new RecognitionUpdate([], []).CompleteUntil);
    }

    [Fact]
    public void CompletionIsCarriedAsGiven()
    {
        var update = new RecognitionUpdate([], [], TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(2), update.CompleteUntil);
    }
}
