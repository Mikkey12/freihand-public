using Freihand.Core.Tests.Review;
namespace Freihand.Core.Tests.Profiles;
[CollectionDefinition("JsonInputGuardAudit", DisableParallelization = true)]
public sealed class JsonInputGuardAuditCollection;
[Collection("JsonInputGuardAudit")]
public sealed class FifthProfileFollowupTests
{
    // Der Wächter muss auch einen zusätzlichen Eingang in einer bereits erlaubten Datei erkennen.
    [Fact]
    public void InputGuardDetectsAdditionalReaderInKnownFile()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Freihand.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "src", "Freihand.Core", "Quality", "QualityReportJson.cs");
        var original = File.ReadAllBytes(path);
        using var matrix = new JsonInputUnicodeMatrixTests();
        matrix.NoJsonInputOutsideTheMatrixExists(); // unveränderter Bestand wird akzeptiert
        Exception? detected;
        try
        {
            var code = File.ReadAllText(path);
            var at = code.LastIndexOf('}');
            Assert.True(at >= 0);
            code = code.Insert(at, "\n    public static object? AdditionalJsonInput(string json) => System.Text.Json.JsonSerializer.Deserialize<object>(json);\n");
            File.WriteAllText(path, code);
            detected = Record.Exception(() => matrix.NoJsonInputOutsideTheMatrixExists());
        }
        finally { File.WriteAllBytes(path, original); }
        Assert.NotNull(detected);
    }
}
