using System.Collections;
using System.Reflection;

namespace Freihand.Benchmark.Tests;

// Gegenproben aus der dritten Codex-Nachprüfung (E01–E05), unverändert übernommen.
// Ruft die im Repository vorhandenen Doc-Hilfsfunktionen auf, nicht eine Kopie davon.
// Diese Gegenproben prüfen das Konzeptmodell, keine bereits implementierte P3-Pipeline.
public sealed class Pr20ThirdReviewTests
{
    [Theory]
    [InlineData(1.94)]
    [InlineData(1.96)]
    public void E01_EmptyFinalRemainderAlwaysAdvances(double wordDuration)
    {
        // Gültige Optionen: Window=2, MaxUtterance=10, CutGuard=0.3.
        // Fünf volle Aufträge übernehmen in Fall b jeweils das erste Wort trotz Guard.
        double cutPoint = 0;
        for (var i = 0; i < 5; i++)
        {
            var end = cutPoint + 2;
            Assert.True(end < 10);
            cutPoint = Cut([(cutPoint, cutPoint + wordDuration, "Wort")], end, 0.3, cutPoint);
        }

        Assert.Equal(5 * wordDuration, cutPoint, 9);
        var next = Cut([], 10, 0.3, cutPoint); // leerer Restauftrag, nur noch 0.3 bzw. 0.2 Sekunden
        Assert.True(next > cutPoint, $"K={cutPoint:R}, neuer Schnitt={next:R}: kein Fortschritt.");
    }

    [Fact]
    public void E02_RepetitionUsesTheSpecifiedTextNormalization()
    {
        var first = Enumerable.Range(0, 5).Select(k => ((double)k, k + 1.0, $"wort{k}"));
        var repeated = Enumerable.Range(0, 5).Select(k => (4.5 + k, 5.5 + k, $"WORT{k}."));
        var words = Words(first.Concat(repeated).ToArray());
        var accepted = ((IEnumerable)Call("DocAccept", words)).Cast<object>().ToArray();
        Assert.Equal(5, accepted.Length);
        Assert.True((bool)Call("DocValid", words));
    }

    [Theory]
    [InlineData(1, 1, "Wort")]
    [InlineData(2, 1, "Wort")]
    [InlineData(1, 2, "")]
    public void E03_BasicInvalidWordsAreRejectedBeforeFiltering(double start, double end, string text)
    {
        Assert.False((bool)Call("DocValid", Words([(start, end, text)])));
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

    private static double Cut((double Start, double End, string Text)[] words, double end, double guard, double cutPoint) =>
        (double)Call("DocCut", Words(words), end, guard, cutPoint);
}

// Ausführbarer Widerspruch zwischen dem wörtlichen Konzept und T61.
// CompleteUntil ist noch nicht implementiert; dies ist ausdrücklich nur ein Modell der Textregel.
// Prüfaussagen unverändert; die beiden Modellfunktionen bilden jetzt die korrigierten Regeln aus Kapitel 3 ab
// (Zusage sinkt nie, Zuordnung je Wort nach Beginn). Vorher waren E04 und E05 rot.
public sealed class Pr20WatermarkContractTest
{
    [Fact]
    public void E04_CompleteUntilMustPermitTheT61Sequence()
    {
        Assert.True(AllowedByLiteralDocument(previous: 1, next: 2),
            "T61 verlangt 1 -> 2; 'Der Wert steigt nie' verbietet genau diesen Fortschritt.");
    }

    [Fact]
    public void E05_CommandBoundaryMustSeparateWordsInsideAnEngineSegment()
    {
        const double segmentStart = 0;
        const double boundary = 2;
        // Ein später fester Engine-Abschnitt [0,4) mit gleichmässig verteilten Wörtern (Kapitel 3).
        (string Text, double Start, double End)[] words = [("Alpha", 0, 2), ("abbrechen", 2, 4)];
        var assignedBefore = AssignByWordStart(words, segmentStart, boundary);
        // Kapitel 5.1 ordnet Wörter, die erst an/nach der Taste beginnen, der nächsten Äusserung zu.
        Assert.Equal(new[] { "Alpha" }, assignedBefore.Select(w => w.Text));
    }

    // Kapitel 3: ein Abschnitt über einer Grenze wird je Wort nach dessen Beginn zugeordnet, nicht als Ganzes.
    private static (string Text, double Start, double End)[] AssignByWordStart(
        (string Text, double Start, double End)[] words, double segmentStart, double boundary) =>
        segmentStart < boundary ? words.Where(w => w.Start < boundary).ToArray() : [];

    // Kapitel 3: die Zusage sinkt nie; gleich oder grösser ist erlaubt.
    private static bool AllowedByLiteralDocument(double previous, double next) => next >= previous;
}
