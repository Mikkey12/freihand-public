using System.Globalization;
using System.Security.Cryptography;
using Xunit;

namespace Freihand.Core.Tests.Review;

// PR #23: ausführbare Modelle der genannten Konzeptregeln, keine P7/P8-Umsetzung.
// Modelle auf die korrigierten Regeln umgestellt (*AsSpecified); Prüfaussagen unverändert ausser den vom Bericht
// zugelassenen Fällen C01 (dokumentierte TokenAlignmentException) und C04 (zusätzlich PlanRefused mit Grund).
// Modelle später durch Produktaufrufe ersetzen.
public sealed class Pr23ConceptCounterTests
{
    private sealed record Word(string Text, long Start, long End);
    private sealed record Element(int ProcessId, long ProcessStart, long Window,
        string RuntimeId, string ControlType, string ClassName);
    private sealed record KeyEvent(string Key, bool Down);

    [Fact]
    public void C01_MinimumWordDurationMustNotMoveStartBeforePreviousWord()
    {
        // ER-03, P3 Vertrag 2: 31 920 Samples = 1,995 s, kein Vielfaches von 10 ms.
        // Ausnahme von der unveränderten Prüfaussage: Die ausdrücklich dokumentierte TokenAlignmentException ist die
        // vom ersten Codex-Bericht zu PR #23 (Q01, Abschnitt Gegenproben) zugelassene Alternative. P8 W8/W10 findet
        // hier keine Lage ohne Überlappung. Die ursprüngliche Prüfaussage sichert C01b für einen reparierbaren Fall ab.
        Assert.Throws<TokenAlignmentException>(() => RepairTimesAsSpecified([(199L, 200L), (200L, 210L)], 31_920));
    }

    [Fact]
    public void C01b_RepairedTimesAtOffGridAudioEndRunForward()
    {
        // Ursprüngliche Prüfaussage von C01 auf einem reparierbaren Fall (P8 R07 mit 31 920 Samples):
        // gelingt die Reparatur, ist jedes Wort positiv lang und die Startfolge läuft vorwärts.
        var words = RepairTimesAsSpecified([(190L, 230L), (200L, 210L)], 31_920);
        Assert.All(words, word => Assert.True(word.Start < word.End));
        Assert.True(words[1].Start >= words[0].Start,
            $"Startfolge {words[0].Start}, {words[1].Start} Ticks ist rückwärts.");
    }

    [Fact]
    public void C02_DifferentFieldsInOneWindowMustNotBeTheSameTarget()
    {
        // AU-10: zwei echte verschiedene Edit-Felder desselben Fensters.
        var bound = new Element(7, 10, 100, "field-a", "Edit", "ChromeEdit");
        var other = bound with { RuntimeId = "field-b" };
        Assert.False(SameWindowTargetAsSpecified(bound, other));
    }

    [Fact]
    public async Task C03_RebindingWhileWaitingMustHoldTheOldWrite()
    {
        // AU-10: die alte Ausgabe darf nicht der neuen Bindung folgen.
        var current = Guid.NewGuid();
        var requested = current;
        var newBinding = Guid.NewGuid();
        var reachedWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injected = new List<Guid>();
        var write = WriteAsSpecified(requested, () => current, async () =>
        {
            reachedWait.SetResult();
            await release.Task;
        }, injected);
        await reachedWait.Task.WaitAsync(TimeSpan.FromSeconds(2));
        current = newBinding;
        release.SetResult();
        await write.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(injected);
    }

    [Fact]
    public void C04_UnsplitTextElementMustBeRefusedOrMakeProgress()
    {
        // AU-01: ein Textelement mit 201 UTF-16-Einheiten bei Höchstlänge 200.
        var text = "a" + new string('\u0301', 200);
        var cut = NextCutAsSpecified(text, 200);
        // null bedeutet ausdrückliche Verweigerung; dieses Textelement passt nicht in 200 Einheiten.
        Assert.Null(cut);
        // Vom Bericht verlangte Prüfung: ganzer Plan verweigert, mit dem festgelegten Grund (P7 5.6 Regel 4).
        Assert.Equal(new PlanRefused(OutputReason.TextElementTooLong), PlanAsSpecified(text, 200));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void C05_PartialShiftEnterMustReleaseEveryInjectedDownKey(int insertedEvents)
    {
        // AU-13: Präfix einer Shift+Enter-Folge, ohne ursprünglich gehaltene Tasten.
        KeyEvent[] events = [new("Shift", true), new("Enter", true),
            new("Enter", false), new("Shift", false)];
        var down = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in events.Take(insertedEvents)) Apply(item, down);
        foreach (var item in CleanupTextUnitAsSpecified(events.Take(insertedEvents).ToArray()))
            Apply(item, down);
        Assert.Empty(down);
    }

    [Fact]
    public void C06_SameSizeAndTimestampMustNotApproveChangedModelBytes()
    {
        // WE-14: unveränderte Metadaten sind kein Nachweis gleicher Modellbytes.
        var original = Enumerable.Repeat((byte)1, 1024).ToArray();
        var changed = (byte[])original.Clone();
        changed[500] = 2;
        var stamp = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var expectedHash = SHA256.HashData(original);
        Assert.False(expectedHash.SequenceEqual(SHA256.HashData(changed)));
        var trusted = TrustCachedVerificationAsSpecified(original.Length, stamp, changed.Length, stamp);
        Assert.False(trusted);
    }

    [Fact]
    public void Control_WordModelMatchesR07AndR10()
    {
        var r07 = RepairTimesAsSpecified([(190L, 230L), (200L, 210L)], 32_000);
        Assert.Equal((19_000_000L, 19_900_000L), (r07[0].Start, r07[0].End));
        Assert.Equal((19_900_000L, 20_000_000L), (r07[1].Start, r07[1].End));
        var r10 = RepairTimesAsSpecified([(50L, 80L), (45L, 90L)], 32_000);
        Assert.Equal((5_000_000L, 8_000_000L), (r10[0].Start, r10[0].End));
        Assert.Equal((5_000_000L, 9_000_000L), (r10[1].Start, r10[1].End));
    }

    [Fact]
    public async Task Control_UnchangedBindingWritesOnce()
    {
        var binding = Guid.NewGuid();
        var injected = new List<Guid>();
        await WriteAsSpecified(binding, () => binding, () => Task.CompletedTask, injected);
        Assert.Equal(binding, Assert.Single(injected));
    }

    [Fact]
    public void Control_PlannerAndCompleteKeySequenceProgress()
    {
        Assert.Equal(200, NextCutAsSpecified(new string('a', 250), 200));
        var down = new HashSet<string>(StringComparer.Ordinal);
        KeyEvent[] events = [new("Shift", true), new("Enter", true),
            new("Enter", false), new("Shift", false)];
        foreach (var item in events) Apply(item, down);
        Assert.Empty(down);
    }
    // P8 5.5 W4/W5 (ein Token je Wort), W7, W8, W9, W10; W6 ist hier konstant 0,9.
    private static List<Word> RepairTimesAsSpecified((long Start, long End)[] tokens, int samples)
    {
        const long quantum = TimeSpan.TicksPerMillisecond * 10;
        const long tolerance = TimeSpan.TicksPerMillisecond * 100;
        long audioEnd = samples * 625L;
        var result = new List<Word>();
        var estimated = new List<bool>();
        foreach (var token in tokens)
        {
            long start = Math.Clamp(token.Start * quantum, 0, audioEnd);
            long end = Math.Clamp(token.End * quantum, 0, audioEnd);
            // W7: Untergrenze ist der Start des vorherigen Worts, beim ersten AudioStart.
            long lower = result.Count > 0 ? result[^1].Start : 0;
            if (start < lower) start = lower;
            bool byMinimumDuration = false;
            if (end <= start)
            {
                // W8: Mindestdauer nur im zulässigen Intervall [lower, audioEnd].
                byMinimumDuration = true;
                end = Math.Min(start + quantum, audioEnd);
                if (end <= start)
                {
                    start = Math.Max(lower, audioEnd - quantum);
                    end = audioEnd;
                    if (end <= start) throw new TokenAlignmentException();
                }
            }
            if (result.Count > 0)
            {
                // W9: kleine Überlappung kürzt das vorherige Wort.
                var previous = result[^1];
                if (start < previous.End && previous.End - start <= tolerance && start > previous.Start)
                    result[^1] = previous with { End = start };
            }
            result.Add(new Word("Wort", start, end));
            estimated.Add(byMinimumDuration);
        }
        // W10: Grundregeln aus P3 Vertrag 2, Schritt 1; ein von W8 gesetztes Wort überlappt keinen Nachbarn.
        for (int i = 0; i < result.Count; i++)
        {
            var word = result[i];
            if (word.Start >= word.End || word.Start < 0 || word.End > audioEnd)
                throw new TokenAlignmentException();
            if (i > 0 && word.Start < result[i - 1].Start)
                throw new TokenAlignmentException();
            if (estimated[i] && ((i > 0 && word.Start < result[i - 1].End)
                || (i + 1 < result.Count && result[i + 1].Start < word.End)))
                throw new TokenAlignmentException();
        }
        return result;
    }

    // P8 4.5: ausdrücklich festgelegte Ausnahme der Wortbildung (Modell der Produktklasse).
    private sealed class TokenAlignmentException : Exception;

    // P7 5.4: ohne nachgewiesene Ersatzidentität gilt nur gleiche RuntimeId als dasselbe Feld;
    // Hauptfenster, Art und Klasse allein reichen nie (Ersatz AutomationId kommt im Modell nicht vor).
    private static bool SameWindowTargetAsSpecified(Element a, Element b) =>
        a.ProcessId == b.ProcessId && a.ProcessStart == b.ProcessStart && a.RuntimeId == b.RuntimeId;

    // P7 5.7 Schritte 3, 7, 8: angeforderte BindingId vor jedem Teil und nach jedem Warten erneut vergleichen.
    // Im Beispiel ist das jeweils aktuell gebundene Element tatsächlich fokussiert.
    private static async Task WriteAsSpecified(Guid requested, Func<Guid> current,
        Func<Task> waitForKeys, List<Guid> injected)
    {
        if (requested != current()) return;
        await waitForKeys();
        if (requested != current()) return;   // NotBound: Rest bleibt im Diktatfenster, keine Übertragung
        injected.Add(current());
    }

    // P7 5.6 Regeln 3 und 4, Fall ohne Satzende und ohne Leerzeichen; null = PlanRefused(TextElementTooLong).
    private static int? NextCutAsSpecified(string text, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        if (text.Length <= maximum) return text.Length;
        var cuts = StringInfo.ParseCombiningCharacters(text).Where(index => index > 0 && index <= maximum).ToArray();
        return cuts.Length == 0 ? null : cuts.Max();
    }

    // P7 5.6: Teile von vorn; findet ein Schnitt keinen Fortschritt, wird der ganze Plan vor jeder Ausgabe verweigert.
    private enum OutputReason { TextElementTooLong }
    private abstract record Plan;
    private sealed record PlannedParts(IReadOnlyList<string> Parts) : Plan;
    private sealed record PlanRefused(OutputReason Reason) : Plan;

    private static Plan PlanAsSpecified(string text, int maximum)
    {
        var parts = new List<string>();
        while (text.Length > 0)
        {
            var cut = NextCutAsSpecified(text, maximum);
            if (cut is null) return new PlanRefused(OutputReason.TextElementTooLong);
            parts.Add(text[..cut.Value]);
            text = text[cut.Value..];
        }
        return new PlannedParts(parts);
    }

    // P7 5.7: nach Teilerfolg jede selbst gedrückte, noch nicht freigegebene Taste freigeben,
    // auch Modifikatoren, in umgekehrter Reihenfolge des Drückens (InputEventPlanner.ReleaseAfter).
    private static IEnumerable<KeyEvent> CleanupTextUnitAsSpecified(KeyEvent[] inserted)
    {
        var pressed = new List<string>();
        foreach (var item in inserted)
        {
            if (item.Down) pressed.Add(item.Key);
            else pressed.Remove(item.Key);
        }
        for (int i = pressed.Count - 1; i >= 0; i--)
            yield return new KeyEvent(pressed[i], false);
    }

    private static void Apply(KeyEvent item, HashSet<string> down)
    {
        if (item.Down) down.Add(item.Key);
        else down.Remove(item.Key);
    }

    // P8 5.4: Grösse und Änderungszeit ersetzen keine Prüfung; jedes Laden prüft SHA-256 neu.
    private static bool TrustCachedVerificationAsSpecified(long oldLength, DateTime oldStamp,
        long newLength, DateTime newStamp)
    {
        _ = (oldLength, oldStamp, newLength, newStamp);
        return false;
    }
}
