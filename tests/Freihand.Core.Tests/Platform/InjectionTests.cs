using System.Globalization;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Tests.Platform.Fakes;

namespace Freihand.Core.Tests.Platform;

/// <summary>
/// Einfügeplan und Senden auf Windows (P7-Konzept, Kapitel 4.3, 5.6, 5.7 und 5.8); Abnahmetests W09 und W28 aus
/// Kapitel 7.1. Texte sind erfunden, Tasten werden nur an einer Attrappe von <see cref="ISendInputApi"/> beobachtet.
/// </summary>
public class InjectionTests
{
    private const int Enter = 0x0D;
    private const int Shift = 0x10;

    // Linke und rechte Strg-, Alt-, Windows- und Umschalttaste: die acht Modifikatoren von Neutralize (Kapitel 5.8)
    private static readonly int[] NeutralizedKeys = [0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C, 0xA0, 0xA1];

    // ---- W09: InjectionPlanner (Kapitel 5.6) ---------------------------------------------------------------------

    [Fact]
    public void W09a_PlannerKeepsShortTextInOnePart()
    {
        const string text = "Grüsse – 5 € @ CHF";

        var parts = PlanParts(text, TargetTable.N, ControlKeySafety.NotApproved);

        // Ein Teil mit einem einzigen TypeText, der genau den Text trägt
        var part = Assert.Single(parts);
        Assert.Equal(text, part.Text);
        var step = Assert.IsType<TypeText>(Assert.Single(part.Steps));
        Assert.Equal(text, step.Text);
    }

    [Fact]
    public void W09b_PlannerCutsAfterSentenceEnd()
    {
        var text = NineSentences();
        Assert.Equal(449, text.Length);

        var parts = PlanParts(text, TargetTable.N, ControlKeySafety.NotApproved);

        // Teile 200, 200 und 49; die ersten beiden enden nach ". " (Satzende samt Leerzeichen)
        Assert.Equal([200, 200, 49], parts.Select(p => p.Text.Length).ToArray());
        Assert.EndsWith(". ", parts[0].Text, StringComparison.Ordinal);
        Assert.EndsWith(". ", parts[1].Text, StringComparison.Ordinal);
        Assert.Equal(text[..200], parts[0].Text);
        Assert.Equal(text[200..400], parts[1].Text);
        Assert.Equal(text[400..], parts[2].Text);
        AssertStepsCarryText(parts);
    }

    [Fact]
    public void W09c_PlannerCutsAfterLastSpaceWithoutSentenceEnd()
    {
        var text = TwentyFiveWords();
        Assert.Equal(249, text.Length);

        var parts = PlanParts(text, TargetTable.N, ControlKeySafety.NotApproved);

        // Kein Satzende innerhalb von 200 Einheiten: Schnitt nach dem letzten Leerzeichen, Teil 1 endet mit Leerzeichen
        Assert.Equal([200, 49], parts.Select(p => p.Text.Length).ToArray());
        Assert.EndsWith(" ", parts[0].Text, StringComparison.Ordinal);
        Assert.Equal(text[..200], parts[0].Text);
        Assert.Equal(text[200..], parts[1].Text);
        AssertStepsCarryText(parts);
    }

    [Fact]
    public void W09d_PlannerCutsAtMaxPartLengthWithoutSpaceOrSentenceEnd()
    {
        var text = new string('a', 250);

        var parts = PlanParts(text, TargetTable.N, ControlKeySafety.NotApproved);

        // Weder Satzende noch Leerzeichen: Schnitt an der letzten Textelementgrenze innerhalb von 200 Einheiten
        Assert.Equal([200, 50], parts.Select(p => p.Text.Length).ToArray());
        AssertStepsCarryText(parts);
    }

    [Theory]
    [InlineData("\U0001F600")] // Surrogatpaar: Einheiten 199 und 200
    [InlineData("é")] // „e“ mit kombinierendem Akzent: Einheiten 199 und 200
    public void W09d_PlannerNeverSplitsATextElement(string element)
    {
        var text = new string('a', 199) + element + new string('a', 49);
        Assert.Equal(250, text.Length);
        Assert.Equal(2, element.Length);

        var parts = PlanParts(text, TargetTable.N, ControlKeySafety.NotApproved);

        // Ein Schnitt bei 200 träfe das Textelement mitten; der erste Teil hat darum nur 199 Einheiten
        Assert.Equal(2, parts.Count);
        Assert.Equal(199, parts[0].Text.Length);
        Assert.Equal(text[..199], parts[0].Text);
        Assert.Equal(text[199..], parts[1].Text);
        AssertStepsCarryText(parts);
    }

    [Fact]
    public void W09e_UnicodeReturnTurnsLfIntoCarriageReturn()
    {
        var parts = PlanParts("Alpha\nBeta", TargetTable.N, ControlKeySafety.NotApproved);

        // Zeile N: das LF wird im TypeText zu U+000D, der Ausgangstext behält das LF
        var part = Assert.Single(parts);
        Assert.Equal("Alpha\nBeta", part.Text);
        Assert.Equal<InputStep>([new TypeText("Alpha\rBeta")], part.Steps.ToArray());
    }

    [Fact]
    public void W09e_ShiftEnterBecomesPressKeyWithSafety()
    {
        var parts = PlanParts("Alpha\nBeta", TargetTable.T, ControlKeySafety.Neutralize);

        // Zeile T mit Neutralize: Text, Umschalt+Enter mit der Sicherung, Text
        var part = Assert.Single(parts);
        Assert.Equal("Alpha\nBeta", part.Text);
        Assert.Equal<InputStep>(
            [new TypeText("Alpha"), new PressKey(Enter, true, ControlKeySafety.Neutralize), new TypeText("Beta")],
            part.Steps.ToArray());
    }

    [Theory]
    [InlineData("T")] // ShiftEnter, aber NotApproved gilt wie Refuse
    [InlineData("S")] // Refuse
    public void W09e_LineBreakIsRefusedWithoutApprovalOrWithRefuseRule(string rowName)
    {
        var plan = InjectionPlanner.Plan("Alpha\nBeta", Row(rowName), ControlKeySafety.NotApproved);

        var refused = Assert.IsType<PlanRefused>(plan);
        Assert.Equal(OutputReason.LineBreakRefused, refused.Reason);
    }

    [Fact]
    public void W09e_TwoLineBreaksGiveTwoPressKeys()
    {
        var parts = PlanParts("Alpha\n\nBeta", TargetTable.T, ControlKeySafety.Neutralize);

        var part = Assert.Single(parts);
        Assert.Equal("Alpha\n\nBeta", part.Text);
        var keys = part.Steps.OfType<PressKey>().ToArray();
        Assert.Equal(2, keys.Length);
        Assert.All(keys, key => Assert.Equal(new PressKey(Enter, true, ControlKeySafety.Neutralize), key));

        // Reihenfolge: Text, Umbruch, Umbruch, Text (leere TypeText dazwischen sagt das Konzept nicht)
        var rebuilt = string.Concat(part.Steps.Select(step => step is TypeText typed ? typed.Text : "\n"));
        Assert.Equal("Alpha\n\nBeta", rebuilt);
    }

    [Theory]
    [InlineData(0x09, "N")] // Tabulator
    [InlineData(0x0D, "N")] // CR
    [InlineData(0x07, "N")] // U+0007
    [InlineData(0x85, "N")] // U+0085
    [InlineData(0x2028, "N")] // Zeilentrenner
    [InlineData(0x2029, "N")] // Absatztrenner (Kapitel 5.6, Schritt 1)
    [InlineData(0x09, "S")]
    [InlineData(0x0D, "S")]
    [InlineData(0x07, "S")]
    [InlineData(0x85, "S")]
    [InlineData(0x2028, "S")]
    [InlineData(0x2029, "S")]
    public void W09f_ControlCharacterIsRefused(int codePoint, string rowName)
    {
        var text = "Hallo" + (char)codePoint + "Welt";

        var plan = InjectionPlanner.Plan(text, Row(rowName), ControlKeySafety.NotApproved);

        // Auch mit Zeile S ohne LF: Grund ist ControlCharacter, nicht LineBreakRefused
        var refused = Assert.IsType<PlanRefused>(plan);
        Assert.Equal(OutputReason.ControlCharacter, refused.Reason);
    }

    public static IEnumerable<object[]> InvariantCases()
    {
        // Alle Fälle von (a) bis (e) und (h), die einen Plan ergeben: Name, Text, Zeile, Sicherung
        yield return ["a", "Grüsse – 5 € @ CHF", "N", ControlKeySafety.NotApproved];
        yield return ["b", NineSentences(), "N", ControlKeySafety.NotApproved];
        yield return ["c", TwentyFiveWords(), "N", ControlKeySafety.NotApproved];
        yield return ["d", new string('a', 250), "N", ControlKeySafety.NotApproved];
        yield return ["d-surrogat", new string('a', 199) + "\U0001F600" + new string('a', 49), "N", ControlKeySafety.NotApproved];
        yield return ["d-akzent", new string('a', 199) + "é" + new string('a', 49), "N", ControlKeySafety.NotApproved];
        yield return ["e-N", "Alpha\nBeta", "N", ControlKeySafety.NotApproved];
        yield return ["e-T", "Alpha\nBeta", "T", ControlKeySafety.Neutralize];
        yield return ["e-T-doppelt", "Alpha\n\nBeta", "T", ControlKeySafety.Neutralize];
        yield return ["h-201", LongTextElement(), "N201", ControlKeySafety.NotApproved];
    }

    [Theory]
    [MemberData(nameof(InvariantCases))]
    public void W09g_PartsAddUpToInputAndAreNeverEmpty(string name, string text, string rowName, ControlKeySafety safety)
    {
        var rule = Row(rowName);

        var parts = PlanParts(text, rule, safety);

        Assert.NotEmpty(parts);

        // Die Ausgangstexte ergeben aneinandergereiht genau den Eingang
        Assert.Equal(text, string.Concat(parts.Select(p => p.Text)));

        // Kein Teil ist leer, keiner länger als MaxPartLength
        foreach (var part in parts)
        {
            Assert.True(part.Text.Length > 0, $"Fall {name}: ein Teil hat keinen Text");
            Assert.NotEmpty(part.Steps);
            Assert.True(part.Text.Length <= rule.MaxPartLength, $"Fall {name}: ein Teil ist länger als MaxPartLength");
        }

        // Nie ein Schnitt mitten in einem Textelement
        var boundaries = StringInfo.ParseCombiningCharacters(text).ToHashSet();
        var offset = 0;
        foreach (var part in parts)
        {
            Assert.True(boundaries.Contains(offset), $"Fall {name}: ein Teil beginnt mitten in einem Textelement (Stelle {offset})");
            offset += part.Text.Length;
        }
    }

    [Fact]
    public void W09h_TextElementLongerThanMaxIsRefused()
    {
        var element = LongTextElement();

        // Ein Textelement, 201 Einheiten, bei MaxPartLength 200: keine erlaubte Schnittstelle
        var plan = InjectionPlanner.Plan(element, TargetTable.N, ControlKeySafety.NotApproved);

        var refused = Assert.IsType<PlanRefused>(plan);
        Assert.Equal(OutputReason.TextElementTooLong, refused.Reason);
    }

    [Fact]
    public void W09h_TextElementTooLongRefusesTheWholePlanIncludingTextBefore()
    {
        var text = "Hallo " + LongTextElement();

        var plan = InjectionPlanner.Plan(text, TargetTable.N, ControlKeySafety.NotApproved);

        // Verweigert wird der ganze Plan: auch „Hallo “ wird nicht als Teil ausgegeben
        var refused = Assert.IsType<PlanRefused>(plan);
        Assert.Equal(OutputReason.TextElementTooLong, refused.Reason);
    }

    [Fact]
    public void W09h_SameTextElementFitsWhenMaxPartLengthIs201()
    {
        var element = LongTextElement();
        var rule = Row("N201");
        Assert.Equal(201, rule.MaxPartLength);

        var parts = PlanParts(element, rule, ControlKeySafety.NotApproved);

        var part = Assert.Single(parts);
        Assert.Equal(element, part.Text);
        AssertStepsCarryText(parts);
    }

    [Fact]
    public void W09h_MaxPartLengthZeroThrows()
    {
        var rule = TargetTable.N with { MaxPartLength = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => InjectionPlanner.Plan("Hallo", rule, ControlKeySafety.NotApproved));
    }

    // ---- W28: SendInputInjector, Ereignisplan und Freigabe nach Teilerfolg (Kapitel 5.7 und 5.8) ---------------------

    [Fact]
    public void W28a_NeutralizedShiftEnterStartsWithEightReleasesWithoutUnit()
    {
        var events = InputEventPlanner.Events(ShiftEnterPart());

        Assert.Equal(12, events.Count);

        // Acht Auf-Ereignisse ohne Texteinheit; die Reihenfolge dieser acht legt das Konzept nicht fest
        var preparation = events.Take(8).ToArray();
        Assert.All(preparation, e =>
        {
            Assert.Equal(InputEventKind.VirtualKey, e.Kind);
            Assert.False(e.Down);
            Assert.Null(e.Unit);
        });
        Assert.Equal(NeutralizedKeys.Order().ToArray(), preparation.Select(e => e.Code).Order().ToArray());

        // Danach Umschalt ab, Enter ab, Enter auf, Umschalt auf; alle vier gehören zur Einheit des LF
        Assert.Equal(
            [Key(Shift, true, 0), Key(Enter, true, 0), Key(Enter, false, 0), Key(Shift, false, 0)],
            events.Skip(8).ToArray());
    }

    [Theory]
    [InlineData(1, 0)] // Umschalt ab
    [InlineData(2, 1)] // Umschalt ab, Enter ab
    [InlineData(3, 1)] // Umschalt ab, Enter ab, Enter auf
    public void W28b_PartialSuccessReleasesPressedKeysInReverseOrder(int beyondPreparation, int expectedUnits)
    {
        var part = ShiftEnterPart();
        var events = InputEventPlanner.Events(part);
        var inserted = 8 + beyondPreparation;

        // Die Attrappe meldet die acht vorbereitenden und 1, 2 bzw. 3 weitere Ereignisse; das zweite Send gelingt
        var (api, result) = Inject(part, inserted);

        Assert.Equal(2, api.Calls.Count);
        Assert.Equal(events.ToArray(), api.Calls[0].ToArray());
        var expectedRelease = beyondPreparation switch
        {
            1 => new[] { Key(Shift, false, 0) },
            2 => new[] { Key(Enter, false, 0), Key(Shift, false, 0) },
            _ => new[] { Key(Shift, false, 0) },
        };
        Assert.Equal(expectedRelease, api.Calls[1].ToArray());
        Assert.Equal(expectedUnits, result.Units);
        Assert.Empty(result.KeysMaybeDown);
        AssertOnlyPressedKeysReleased(api, inserted);

        // Dasselbe über den Ereignisplan (Kapitel 4.3)
        Assert.Equal(expectedRelease, InputEventPlanner.ReleaseAfter(events, inserted).ToArray());
        Assert.Equal(expectedUnits, InputEventPlanner.UnitsInserted(events, inserted));
    }

    [Fact]
    public void W28c_PartialTextReleasesOnlyTheKeyOfTheLastInsertedUnit()
    {
        var part = new InjectionPart([new TypeText("abc")], "abc");
        var events = InputEventPlanner.Events(part);

        // Je Einheit Ab und Auf, ein Unicode-Ereignis mit dem Zeichen als Code
        Assert.Equal(
            [
                Letter('a', true, 0), Letter('a', false, 0),
                Letter('b', true, 1), Letter('b', false, 1),
                Letter('c', true, 2), Letter('c', false, 2),
            ],
            events.ToArray());

        // 3 von 6 Ereignissen (a ab, a auf, b ab): das zweite Send enthält genau „b auf“
        var (api, result) = Inject(part, 3);

        Assert.Equal(2, api.Calls.Count);
        Assert.Equal(events.ToArray(), api.Calls[0].ToArray());
        Assert.Equal([Letter('b', false, 1)], api.Calls[1].ToArray());
        Assert.Equal(2, result.Units);
        Assert.Empty(result.KeysMaybeDown);
        AssertOnlyPressedKeysReleased(api, 3);

        Assert.Equal([Letter('b', false, 1)], InputEventPlanner.ReleaseAfter(events, 3).ToArray());
        Assert.Equal(2, InputEventPlanner.UnitsInserted(events, 3));
    }

    [Theory]
    [InlineData(1)] // Umschalt ab
    [InlineData(2)] // Umschalt ab, Enter ab
    [InlineData(3)] // Umschalt ab, Enter ab, Enter auf
    public void W28d_FailedReleaseIsRetriedOnceAndReportsShiftAsMaybeDown(int beyondPreparation)
    {
        var part = ShiftEnterPart();

        // Zweites und drittes Send melden 0: die Freigabe scheitert, der Rest wird genau einmal erneut versucht
        var (api, result) = Inject(part, 8 + beyondPreparation, 0, 0);

        Assert.Equal(3, api.Calls.Count);
        Assert.Equal(api.Calls[1].ToArray(), api.Calls[2].ToArray());
        Assert.Contains(Shift, result.KeysMaybeDown);

        // Ohne Enter-Auf bleibt auch Enter offen; sonst nur Umschalt (Ableitung aus „die betroffenen Tasten“)
        var expectedOpen = beyondPreparation == 2 ? new[] { Enter, Shift } : new[] { Shift };
        Assert.Equal(expectedOpen.Order().ToArray(), result.KeysMaybeDown.Order().ToArray());
        AssertOnlyPressedKeysReleased(api, 8 + beyondPreparation);
    }

    [Fact]
    public void W28d_RetryOnlySendsWhatWasNotReleased()
    {
        var part = ShiftEnterPart();

        // Umschalt ab, Enter ab eingefügt; das zweite Send fügt nur „Enter auf“ ein, das dritte den Rest
        var (api, result) = Inject(part, 10, 1);

        Assert.Equal(3, api.Calls.Count);
        Assert.Equal([Key(Enter, false, 0), Key(Shift, false, 0)], api.Calls[1].ToArray());
        Assert.Equal([Key(Shift, false, 0)], api.Calls[2].ToArray());
        Assert.Equal(1, result.Units);
        Assert.Empty(result.KeysMaybeDown);
        AssertOnlyPressedKeysReleased(api, 10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void W28e_NeverReleasesAKeyThatFreihandDidNotPress(int inserted)
    {
        var part = ShiftEnterPart();
        var events = InputEventPlanner.Events(part);

        // Nur vorbereitende Auf-Ereignisse (oder keines) eingefügt: Freihand hat nichts gedrückt, also nichts freizugeben
        var (api, result) = Inject(part, inserted);

        Assert.Empty(InputEventPlanner.ReleaseAfter(events, inserted));
        Assert.Equal(0, result.Units);
        Assert.Empty(result.KeysMaybeDown);
        Assert.Equal(events.ToArray(), api.Calls[0].ToArray());
        AssertOnlyPressedKeysReleased(api, inserted);
        Assert.All(api.Calls.Skip(1), call => Assert.Empty(call));
    }

    [Fact]
    public void W28f_FullSuccessSendsOnceAndReleasesNothingExtra()
    {
        var text = new InjectionPart([new TypeText("abc")], "abc");
        var (textApi, textResult) = Inject(text);

        // Ein Send je Teil, alle Einheiten eingefügt, nichts bleibt gedrückt
        Assert.Equal(InputEventPlanner.Events(text).ToArray(), Assert.Single(textApi.Calls).ToArray());
        Assert.Equal(3, textResult.Units);
        Assert.Empty(textResult.KeysMaybeDown);

        var press = ShiftEnterPart();
        var (pressApi, pressResult) = Inject(press);

        Assert.Equal(InputEventPlanner.Events(press).ToArray(), Assert.Single(pressApi.Calls).ToArray());
        Assert.Equal(1, pressResult.Units);
        Assert.Empty(pressResult.KeysMaybeDown);
    }

    // ---- Hilfen ----------------------------------------------------------------------------------------------------

    private static TargetRule Row(string name) => name switch
    {
        "N" => TargetTable.N,
        "T" => TargetTable.T,
        "S" => TargetTable.S,
        "N201" => TargetTable.N with { MaxPartLength = 201 },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static List<InjectionPart> PlanParts(string text, TargetRule rule, ControlKeySafety safety)
    {
        var plan = InjectionPlanner.Plan(text, rule, safety);

        var planned = Assert.IsType<PlannedParts>(plan);
        return [.. planned.Parts];
    }

    /// <summary>Ohne Umbrüche tragen die Schritte eines Teils genau dessen Text.</summary>
    private static void AssertStepsCarryText(IReadOnlyList<InjectionPart> parts)
    {
        foreach (var part in parts)
        {
            var steps = part.Steps.Select(step => Assert.IsType<TypeText>(step).Text);
            Assert.Equal(part.Text, string.Concat(steps));
        }
    }

    /// <summary>Neun Sätze zu je 48 Zeichen plus „. “, ohne das letzte Leerzeichen: 449 Zeichen.</summary>
    private static string NineSentences()
    {
        var sentences = new List<string>();
        for (var i = 1; i <= 9; i++)
        {
            var body = $"Satz Nummer {i} bleibt kurz und ruhig".PadRight(48, 'x');
            Assert.Equal(48, body.Length);
            sentences.Add(body + ". ");
        }

        return string.Concat(sentences)[..^1];
    }

    /// <summary>25 Wörter zu je 9 Buchstaben, durch je ein Leerzeichen getrennt: 249 Zeichen.</summary>
    private static string TwentyFiveWords()
    {
        var words = Enumerable.Range(0, 25).Select(i => new string((char)('a' + (i % 26)), 9));
        return string.Join(' ', words);
    }

    /// <summary>„a“ mit 200 kombinierenden Akzenten: ein Textelement, 201 Einheiten.</summary>
    private static string LongTextElement()
    {
        var element = "a" + new string('́', 200);
        Assert.Equal(201, element.Length);
        Assert.Equal(1, new StringInfo(element).LengthInTextElements);
        return element;
    }

    private static InjectionPart ShiftEnterPart() =>
        new([new PressKey(Enter, true, ControlKeySafety.Neutralize)], "\n");

    private static InputEvent Key(int virtualKey, bool down, int? unit) =>
        new(InputEventKind.VirtualKey, virtualKey, down, unit);

    private static InputEvent Letter(char unit, bool down, int index) =>
        new(InputEventKind.Unicode, unit, down, index);

    /// <summary>Sendet den Teil an die Attrappe; <paramref name="sendResults"/> sind die Rückgabewerte je Aufruf.</summary>
    private static (FakeSendInputApi Api, InjectionResult Result) Inject(InjectionPart part, params int[] sendResults)
    {
        var api = new FakeSendInputApi();
        foreach (var inserted in sendResults)
        {
            api.Results.Enqueue(inserted);
        }

        var result = new SendInputInjector(api).Inject(part);
        return (api, result);
    }

    /// <summary>
    /// Nie ein Auf-Ereignis für eine Taste, die Freihand nicht gedrückt hat: Jedes Ereignis nach dem ersten Send ist ein
    /// Auf-Ereignis einer Taste, deren Ab-Ereignis unter den ersten <paramref name="insertedByFirstSend"/> lag.
    /// </summary>
    private static void AssertOnlyPressedKeysReleased(FakeSendInputApi api, int insertedByFirstSend)
    {
        var pressed = api.Calls[0]
            .Take(insertedByFirstSend)
            .Where(e => e.Down)
            .Select(e => (e.Kind, e.Code))
            .ToHashSet();

        foreach (var e in api.Calls.Skip(1).SelectMany(call => call))
        {
            Assert.False(e.Down, "Nach dem ersten Send darf kein Ab-Ereignis folgen");
            Assert.True(pressed.Contains((e.Kind, e.Code)), $"Auf-Ereignis für Taste {e.Code:X}, die Freihand nicht gedrückt hat");
        }
    }
}
