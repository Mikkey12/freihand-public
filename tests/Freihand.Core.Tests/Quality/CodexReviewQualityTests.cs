using Freihand.Core.Quality;

namespace Freihand.Core.Tests.Quality;

/// <summary>
/// Prüftests von Codex für P6, unverändert übernommen aus <c>codex-probes/ReviewRegressionTests.cs</c>
/// (Branch <c>codex/pruefproben</c>), Befund R6 (QM-02, BA-20).
/// </summary>
public sealed class CodexReviewQualityTests
{
    [Fact]
    public void Review_DictationErrorsDoNotCancelAcrossSentences()
    {
        var first = DictationMarks.Evaluate("Hallo, Welt", "Hallo Welt");
        var second = DictationMarks.Evaluate("Guten Tag", "Guten, Tag");
        Assert.Equal(2, DictationMarkErrors.Sum([first, second]).Errors);
    }
}
