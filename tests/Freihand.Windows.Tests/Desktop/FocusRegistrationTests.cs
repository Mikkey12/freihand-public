using Freihand.Core.Output;
using Freihand.Core.Platform;

namespace Freihand.Windows.Tests.Desktop;

/// <summary>
/// Befund der ersten Probe: Jede neue UIA-Fokusanmeldung liefert allen
/// Fokusempfängern des Prozesses ein veraltetes Ereignis nach. Scharfschalten des Wächters darf deshalb keine Anmeldung
/// auslösen, sonst gilt die eben gebundene Ausgabe sofort als verlassen. Ohne Fenster, Fokuswechsel oder Eingabe; braucht
/// aber eine Desktopsitzung mit UI Automation.
/// </summary>
[Trait("Kategorie", "Desktop")]
[Collection("Desktop")]
public sealed class FocusRegistrationTests
{
    [Fact]
    public void ArmingTheGuardDeliversNoFocusEventToTheBinding()
    {
        using var targets = new WindowsTargets(
            new TargetRuleTable([]),
            new OutputOptions(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(10), ControlKeySafety.NotApproved));
        var events = 0;
        EventHandler listener = (_, _) => Interlocked.Increment(ref events);
        targets.Inspector.FocusChanged += listener;
        try
        {
            targets.Guard.Arm();
            Thread.Sleep(700);
            targets.Guard.Disarm();
            targets.Guard.Arm();
            Thread.Sleep(700);
            targets.Guard.Disarm();

            Assert.Equal(0, Volatile.Read(ref events));
        }
        finally
        {
            targets.Inspector.FocusChanged -= listener;
        }
    }
}
