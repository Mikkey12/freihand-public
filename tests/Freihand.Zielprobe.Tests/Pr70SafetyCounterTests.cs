namespace Freihand.Zielprobe.Tests;

public sealed class Pr70SafetyCounterTests
{
    [Fact]
    public void AChangedRuntimeIdIsAFieldChangeEvenWithTheSameUiTraits()
    {
        const string json = """
            [{"prozess":"notepad","controlType":"Document","className":"Edit","frameworkId":"Win32"},
             {"prozess":"notepad","controlType":"Document","className":"Edit","frameworkId":"Win32",
              "teilgroesse":50,"zeichengetreu":true,"lesbar":true,"runtimeIdGleich":false}]
            """;

        Assert.Contains("abweichung = ja", Messen.Zusammenfassung(json));
    }

    [Fact]
    public void ShortExistingContentCannotBeDeletedAsPartOfTheProbe()
    {
        Assert.False(Logik.DarfLeerenImMessmodus("cababc", "abc", true, true, "cab"));
    }
}