namespace Freihand.App;

/// <summary>
/// Handtest P11 vom 17.11.2026: Nach einem Neustart mit geretteten Texten öffnete Freihand das Diktatfenster aktiviert
/// und schrieb danach Diktate dorthin statt nach Outlook und Teams. Regel: ins Diktatfenster nur, wenn Windows es als
/// Vordergrundfenster meldet; das Angebot beim Start nimmt den Fokus nie.
/// </summary>
public sealed class PadFocusTests
{
    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, true, false, true, false)]
    public void OnlyAForegroundPadWithFocusTakesTheDictation(bool visible, bool active, bool keyboard, bool foreground, bool expected) =>
        Assert.Equal(expected, DictationWindow.TakesDictation(visible, active, keyboard, foreground));
}
