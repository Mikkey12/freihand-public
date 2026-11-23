using System.Collections;
using System.Reflection;

namespace Freihand.Benchmark.Tests;

// Gegenproben aus der vierten Codex-Nachprüfung (J01, J02), unverändert übernommen.
// Prüft die vorhandenen Doc-Helfer, keine bereits implementierte P3-Pipeline.
public sealed class Pr20FourthReviewTests
{
    [Fact]
    public void J01_SeparateEdgePunctuationBelongsToTheRepeatedWord()
    {
        // Kapitel 5.2: ein Token nur aus Randzeichen gehört zum Wort davor.
        // Dieselbe Fünfwortfolge, einmal mit angehängtem, einmal mit getrenntem Punkt.
        var first = Enumerable.Range(0, 5)
            .Select(k => ((double)k, k + 1.0, k == 4 ? "w4." : $"w{k}"));
        var repeat = Enumerable.Range(0, 5)
            .Select(k => (4.5 + k, 5.5 + k, $"w{k}"));
        var data = first.Concat(repeat).Append((9.5, 9.6, ".")).ToArray();
        var words = Words(data);
        Assert.True((bool)Call("DocValidHypothesis", words, 0.0, 10.0));
        var result = ((IEnumerable)Call("DocAccept", words)).Cast<object>().ToArray();
        Assert.Equal(first.Select(w => w.Item3), result.Select(w => (string)WordType.GetProperty("Text")!.GetValue(w)!));
    }

    [Fact]
    public void J02_EngineWordTimesAreRoundedDownToWholeTicks()
    {
        // Kapitel 3: [0,1) auf drei Wörter, Grenzen auf ganze Ticks abgerundet.
        var result = ((IEnumerable)Call("DocSplit", "a b c", 0.0, 1.0)).Cast<object>().ToArray();
        var starts = result.Select(w => (double)WordType.GetProperty("Start")!.GetValue(w)!);
        var ends = result.Select(w => (double)WordType.GetProperty("End")!.GetValue(w)!);
        Assert.Equal(new[] { 0.0, 3333333.0 / TimeSpan.TicksPerSecond, 6666666.0 / TimeSpan.TicksPerSecond }, starts);
        Assert.Equal(new[] { 3333333.0 / TimeSpan.TicksPerSecond, 6666666.0 / TimeSpan.TicksPerSecond, 1.0 }, ends);
    }

    private static readonly Type Counter = typeof(Pr20ConceptCounterTests);
    private static readonly Type WordType = Counter.GetNestedType("Word", BindingFlags.NonPublic)!;

    private static Array Words((double Start, double End, string Text)[] data)
    {
        var array = Array.CreateInstance(WordType, data.Length);
        for (var i = 0; i < data.Length; i++)
        {
            array.SetValue(Activator.CreateInstance(WordType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [data[i].Start, data[i].End, data[i].Text], null), i);
        }
        return array;
    }

    private static object Call(string name, params object[] arguments) =>
        Counter.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments)!;
}
