namespace Freihand.Core.Tests.Review;

// PR #23: Modell der Prüfreihenfolge aus P7 Kapitel 5.4, nicht Produktcode; auf die korrigierte Regel umgestellt.
public sealed class Pr23FollowupCounterTests
{
    private sealed record Facts(bool? IsPassword, bool? IsReadOnly, bool? IsEnabled, bool? IsElevated);
    private enum Check { Focused, NotBound, SecureDesktop, TargetGone, IdentityUnclear, FocusElsewhere, PasswordField,
        Elevated, ReadOnly }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void N01_UnreadableSafetyPropertyAfterBindingMustPreventInjection(int property)
    {
        // Bindung und Feldidentität unverändert, dasselbe Feld im Fokus, kein Passwortfeld.
        // Nach dem Binden schlägt die Abfrage genau einer weiteren Sicherheitseigenschaft fehl.
        var facts = property switch
        {
            0 => new Facts(false, null, true, false),
            1 => new Facts(false, false, null, false),
            _ => new Facts(false, false, true, null),
        };
        var result = CheckAsSpecified(true, false, true, true, false, facts);
        // SI-04, P7 5.5: nicht lesbar gilt auch bei Ausgabe nie als sicherer Wert.
        Assert.NotEqual(Check.Focused, result);
    }

    private static Check CheckAsSpecified(bool sameBinding, bool secureDesktop, bool targetExists,
        bool uniqueIdentity, bool focusElsewhere, Facts facts)
    {
        if (!sameBinding) return Check.NotBound;
        if (secureDesktop) return Check.SecureDesktop;
        if (!targetExists) return Check.TargetGone;
        if (!uniqueIdentity) return Check.IdentityUnclear;
        if (focusElsewhere) return Check.FocusElsewhere;
        // P7 5.4 nach Korrektur N01: Sicherheitsmerkmale unmittelbar vor jedem Teil, nicht lesbar = unsicher.
        if (facts.IsPassword != false) return Check.PasswordField;
        if (facts.IsElevated != false) return Check.Elevated;
        if (facts.IsReadOnly != false || facts.IsEnabled != true) return Check.ReadOnly;
        return Check.Focused;
    }
}
