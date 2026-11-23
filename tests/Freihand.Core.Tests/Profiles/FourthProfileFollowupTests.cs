using System.Text.Json;
using System.Text.Json.Nodes;
using Freihand.Core.Quality;
namespace Freihand.Core.Tests.Profiles;
public sealed class FourthProfileFollowupTests
{
    // Konzept Kapitel 3: Einzelne Surrogate werden auch in ignorierten JSON-Werten abgewiesen.
    [Theory]
    [InlineData(false, "\\ud800")]
    [InlineData(false, "\\udc00")]
    [InlineData(true, "\\ud800")]
    [InlineData(true, "\\udc00")]
    public void QualityReportRejectsLoneSurrogateInUnknownValue(bool nested, string escape)
    {
        var report = QualityEvaluator.Evaluate(new QualityRun(
            new MeasurementInfo("test", "test", "test", "de-CH", TextStage.Raw),
            TimeSpan.FromSeconds(1), new QualitySettings(TimeSpan.FromSeconds(0.6))));
        var root = JsonNode.Parse(QualityReportJson.Serialize(report))!;
        var target = nested ? root["measurement"]! : root;
        target["extra"] = "PLACEHOLDER";
        var valid = root.ToJsonString();
        Assert.Equal(QualityReport.CurrentSchema, QualityReportJson.Deserialize(valid).Schema);
        var invalid = valid.Replace("PLACEHOLDER", escape, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => QualityReportJson.Deserialize(invalid));
    }
}
