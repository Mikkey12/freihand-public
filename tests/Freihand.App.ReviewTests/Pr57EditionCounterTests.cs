using Freihand.Adapters.Dictation;
using Freihand.Core.Platform;

namespace Freihand.App;

// Nur in der Geschäftsausgabe übersetzt (-p:DefineConstants=GESCHAEFT); dieselbe Regel prüft ohne Ausgabe
// PrototypeSettingsTests.UnavailableActionsTakeNoPartInReadingOrTheDuplicateCheck in jeder CI.
#if GESCHAEFT

public sealed class Pr57EditionCounterTests
{
    [Fact]
    public void ADisabledCorrectionKeyMustNotStealTheProblemKeyInBusinessEdition()
    {
        Assert.True(Edition.Business);
        var settings = PrototypeSettings.Defaults("C:\\Testdaten") with
        {
            Hotkeys = new HotkeyTexts { Correct = "Strg+Alt+B", Mark = "Strg+Alt+B" },
        };
        var keys = Edition.Gestures(settings, out _, out _);
        Assert.Contains(HotkeyAction.Mark, keys.Keys);
    }
}
#endif
