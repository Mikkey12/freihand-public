using System.Reflection;
using Freihand.Core.Platform;

namespace Freihand.App;

/// <summary>
/// Zieltabelle der App nach WM05: Umbrüche nur, wo sie ohne echte Taste gemessen
/// ankommen; nie mit echten Tasten; nie in Feldern, die einzeilig sein können.
/// </summary>
public sealed class P9Wm05TargetTableTests
{
    private static IReadOnlyList<TargetRule> Table() =>
        (TargetRule[])typeof(App).GetField("MeasuredTargets", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static TargetRule Row(string process, string? className, string controlType) =>
        Assert.Single(Table(), rule => rule.ProcessName == process && rule.ClassName == className && rule.ControlType == controlType);

    // Nachmessung 17.11.2026: Claude-App 20 Zeichen je Teil, nie Umbrüche (Enter schickt ab)
    [Fact]
    public void TheClaudeAppTakesTwentyCharactersAndNoLineBreaks()
    {
        var claude = Assert.Single(Table(), rule => rule.ProcessName == "claude");
        Assert.Equal((20, LineBreakMode.Refuse, FieldKinds.ChatInput, (string?)null), (claude.MaxPartLength, claude.LineBreak, claude.FieldKind, claude.ClassName));
    }

    // Messung 19.11.2026: Hermes bis 100 Zeichen je Teil zeichengetreu, ab 200 nicht; 50 mit Abstand, nie Umbrüche
    [Fact]
    public void HermesTakesFiftyCharactersAndNoLineBreaks()
    {
        var hermes = Assert.Single(Table(), rule => rule.ProcessName == "Hermes");
        Assert.Equal((50, LineBreakMode.Refuse, FieldKinds.ChatInput, (string?)null), (hermes.MaxPartLength, hermes.LineBreak, hermes.FieldKind, hermes.ClassName));
    }

    [Fact]
    public void WordAndTheOutlookMessageBodyGetLineBreaksWithoutRealKeys()
    {
        Assert.Equal(LineBreakMode.UnicodeReturn, Row("WINWORD", "_WwG", "Document").LineBreak);

        var outlook = Row("OUTLOOK", "_WwG", "Document");
        Assert.Equal(LineBreakMode.UnicodeReturn, outlook.LineBreak);
        Assert.Equal(1, outlook.MaxPartLength);
        Assert.Equal(FieldKinds.MessageBody, outlook.FieldKind);
    }

    [Fact]
    public void EditorBrowsersAndOutlookSubjectStayWithoutLineBreaks()
    {
        Assert.Equal(LineBreakMode.Refuse, Row("Notepad", "RichEditD2DPT", "Document").LineBreak);
        Assert.Equal(LineBreakMode.Refuse, Row("OUTLOOK", "RichEdit20WPT", "Edit").LineBreak);
        Assert.All(Table().Where(rule => rule.ProcessName is "msedge" or "chrome"), rule => Assert.Equal(LineBreakMode.Refuse, rule.LineBreak));
    }

    [Fact]
    public void NoRowUsesRealKeysForLineBreaks()
    {
        Assert.DoesNotContain(Table(), rule => rule.LineBreak is LineBreakMode.Enter or LineBreakMode.ShiftEnter);
    }

    [Fact]
    public void ALineBreakInWordIsPlannedAsUnicodeReturnWithoutKeys()
    {
        var plan = Assert.IsType<PlannedParts>(InjectionPlanner.Plan("Erste Zeile\nZweite Zeile", Row("WINWORD", "_WwG", "Document"), ControlKeySafety.NotApproved));

        Assert.All(plan.Parts.SelectMany(part => part.Steps), step => Assert.IsType<TypeText>(step));
        Assert.Contains(plan.Parts.SelectMany(part => part.Steps).OfType<TypeText>(), step => step.Text.Contains('\r', StringComparison.Ordinal));
        Assert.Equal("Erste Zeile\nZweite Zeile", string.Concat(plan.Parts.Select(part => part.Text)));
    }
}
