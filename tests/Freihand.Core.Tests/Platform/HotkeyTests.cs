using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Tests.Platform.Fakes;

namespace Freihand.Core.Tests.Platform;

/// <summary>
/// P7 Stufe A, W01–W06 (Kapitel 7.1): Prüfregeln, Anmeldung und Halten der eigenen Tastenkombinationen sowie die
/// Aufnahmetaste. Alles mit Attrappen und manueller Uhr, ohne Windows.
/// </summary>
public sealed class HotkeyTests
{
    private const int KeyY = 0x59;
    private const int KeyX = 0x58;
    private const int KeyQ = 0x51;
    private const int KeyW = 0x57;
    private const int KeyF8 = 0x77;
    private const int Ctrl = 0x11;
    private const int Alt = 0x12;
    private const int Shift = 0x10;
    private const int WinLeft = 0x5B;
    private const int WinRight = 0x5C;

    private const HotkeyModifiers CtrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(15);
    private static readonly HotkeyGesture CtrlAltY = new(CtrlAlt, KeyY);
    private static readonly HotkeyGesture CtrlAltX = new(CtrlAlt, KeyX);
    private static readonly HotkeyGesture CtrlAltQ = new(CtrlAlt, KeyQ);
    private static readonly HotkeyGesture CtrlAltW = new(CtrlAlt, KeyW);
    private static readonly HotkeyGesture WinShiftF8 = new(HotkeyModifiers.Windows | HotkeyModifiers.Shift, KeyF8);

    // ---- W01: HotkeyRules ---------------------------------------------------------------------------------------

    private static HotkeyConflict Check(HotkeyAction action, HotkeyModifiers modifiers, int virtualKey) =>
        HotkeyRules.Check(
            action,
            new HotkeyGesture(modifiers, virtualKey),
            new Dictionary<HotkeyAction, HotkeyGesture> { [HotkeyAction.Record] = CtrlAltY },
            new FakeLayout());

    [Fact]
    public void W01_FreeCombinationOnTheCommandKeyIsAccepted()
    {
        Assert.Equal(HotkeyConflict.None, Check(HotkeyAction.Command, CtrlAlt, KeyX));
    }

    [Fact]
    public void W01_SameCombinationAsAnotherOwnActionIsOwnDuplicate()
    {
        Assert.Equal(HotkeyConflict.OwnDuplicate, Check(HotkeyAction.Command, CtrlAlt, KeyY));
    }

    [Theory]
    [InlineData(HotkeyModifiers.None, 0x59)] // Y
    [InlineData(HotkeyModifiers.None, 0x20)] // Leertaste
    [InlineData(HotkeyModifiers.None, 0x0D)] // Enter
    [InlineData(HotkeyModifiers.None, 0x09)] // Tab
    [InlineData(HotkeyModifiers.None, 0x08)] // Rücktaste
    [InlineData(HotkeyModifiers.None, 0x1B)] // Escape
    [InlineData(HotkeyModifiers.Shift, 0x59)] // Umschalt+Y
    public void W01_TypingKeyWithoutModifierOrWithShiftOnlyIsTypingKey(HotkeyModifiers modifiers, int virtualKey)
    {
        Assert.Equal(HotkeyConflict.TypingKey, Check(HotkeyAction.Command, modifiers, virtualKey));
    }

    [Theory]
    [InlineData(HotkeyModifiers.None, 0x77)] // F8
    [InlineData(HotkeyModifiers.None, 0x13)] // Pause
    [InlineData(HotkeyModifiers.None, 0x7C)] // F13
    public void W01_SingleNonTypingKeysAreAccepted(HotkeyModifiers modifiers, int virtualKey)
    {
        Assert.Equal(HotkeyConflict.None, Check(HotkeyAction.Command, modifiers, virtualKey));
    }

    [Theory]
    [InlineData(HotkeyModifiers.None)] // F12 allein
    [InlineData(HotkeyModifiers.Control)] // Strg+F12
    public void W01_F12IsReservedWithOrWithoutModifiers(HotkeyModifiers modifiers)
    {
        Assert.Equal(HotkeyConflict.Reserved, Check(HotkeyAction.Command, modifiers, 0x7B));
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    [InlineData(0xA0)]
    [InlineData(0xA1)]
    [InlineData(0xA2)]
    [InlineData(0xA3)]
    [InlineData(0xA4)]
    [InlineData(0xA5)]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    [InlineData(0x01)]
    [InlineData(0x02)]
    [InlineData(0x04)]
    public void W01_ModifierMouseButtonOrOutOfRangeCodeAsKeyIsInvalid(int virtualKey)
    {
        Assert.Equal(HotkeyConflict.Invalid, Check(HotkeyAction.Command, HotkeyModifiers.None, virtualKey));
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x32, HotkeyConflict.AltGrCharacter)] // Strg+Alt+2 → „@“
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x45, HotkeyConflict.AltGrCharacter)] // Strg+Alt+E → „€“
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x37, HotkeyConflict.AltGrCharacter)] // Strg+Alt+7 → „|“
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x32, HotkeyConflict.None)] // Umschalt: kein Zeichen
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows, 0x32, HotkeyConflict.None)] // Windows: kein AltGr
    public void W01_CtrlAltKeyThatProducesACharacterIsAltGrCharacter(HotkeyModifiers modifiers, int virtualKey, HotkeyConflict expected)
    {
        Assert.Equal(expected, Check(HotkeyAction.Cancel, modifiers, virtualKey));
    }

    [Fact]
    public void W01_CheckNeverReportsTakenBySystemOrOtherApp()
    {
        // Check kennt keine Windows-API: über alle Codes und Modifikatorkombinationen nie TakenBySystemOrOtherApp.
        foreach (var action in new[] { HotkeyAction.Record, HotkeyAction.Command, HotkeyAction.Cancel })
        {
            for (var modifiers = 0; modifiers <= 0xF; modifiers++)
            {
                for (var virtualKey = 0; virtualKey <= 0xFF; virtualKey++)
                {
                    var result = Check(action, (HotkeyModifiers)modifiers, virtualKey);
                    Assert.NotEqual(HotkeyConflict.TakenBySystemOrOtherApp, result);
                }
            }
        }
    }

    // W01, ergänzend aus Kapitel 5.1 Regel 3: weitere Schreibtasten und der Ausschluss durch andere Modifikatoren.

    [Theory]
    [InlineData(0x41)] // A
    [InlineData(0x5A)] // Z
    [InlineData(0x30)] // 0
    [InlineData(0x39)] // 9
    [InlineData(0x60)] // Ziffernblock 0
    [InlineData(0x6F)] // Ziffernblock /
    [InlineData(0xBA)]
    [InlineData(0xC0)]
    [InlineData(0xDB)]
    [InlineData(0xDF)]
    [InlineData(0xE2)]
    public void W01_OtherTypingKeysAreTypingKeyWithoutModifier(int virtualKey)
    {
        Assert.Equal(HotkeyConflict.TypingKey, Check(HotkeyAction.Command, HotkeyModifiers.None, virtualKey));
        Assert.Equal(HotkeyConflict.TypingKey, Check(HotkeyAction.Command, HotkeyModifiers.Shift, virtualKey));
    }

    [Theory]
    [InlineData(0x70)] // F1
    [InlineData(0x7A)] // F11
    [InlineData(0x87)] // F24
    public void W01_FunctionKeysAreAcceptedAlone(int virtualKey)
    {
        Assert.Equal(HotkeyConflict.None, Check(HotkeyAction.Command, HotkeyModifiers.None, virtualKey));
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control)]
    [InlineData(HotkeyModifiers.Alt)]
    [InlineData(HotkeyModifiers.Windows)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift)]
    public void W01_TypingKeyWithAnotherModifierIsAccepted(HotkeyModifiers modifiers)
    {
        Assert.Equal(HotkeyConflict.None, Check(HotkeyAction.Command, modifiers, KeyW));
    }

    // ---- W02: HotkeyService, Anmeldung --------------------------------------------------------------------------

    [Theory]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, KeyY, 0x4003u)]
    [InlineData(HotkeyModifiers.None, KeyF8, 0x4000u)]
    [InlineData(HotkeyModifiers.Alt, KeyF8, 0x4001u)]
    [InlineData(HotkeyModifiers.Control, KeyF8, 0x4002u)]
    [InlineData(HotkeyModifiers.Shift, KeyF8, 0x4004u)]
    [InlineData(HotkeyModifiers.Windows, KeyF8, 0x4008u)]
    [InlineData(HotkeyModifiers.Windows | HotkeyModifiers.Shift, KeyF8, 0x400Cu)]
    public void W02_EveryRegistrationCarriesNoRepeatPlusTheModifierBits(HotkeyModifiers modifiers, int virtualKey, uint expected)
    {
        using var rig = new Rig();

        Assert.Equal(HotkeyConflict.None, rig.Service.Register(HotkeyAction.Record, new HotkeyGesture(modifiers, virtualKey)));

        var registration = Assert.Single(rig.Api.Registered);
        Assert.Equal(expected, registration.Modifiers);
        Assert.Equal(virtualKey, registration.VirtualKey);
    }

    [Fact]
    public void W02_AllThreeActionsAreRegisteredWithNoRepeatAndSeparateIds()
    {
        using var rig = new Rig();

        Assert.Equal(HotkeyConflict.None, rig.Service.Register(HotkeyAction.Record, CtrlAltY));
        Assert.Equal(HotkeyConflict.None, rig.Service.Register(HotkeyAction.Command, CtrlAltX));
        Assert.Equal(HotkeyConflict.None, rig.Service.Register(HotkeyAction.Cancel, CtrlAltQ));

        Assert.Equal(3, rig.Api.Registered.Count);
        Assert.All(rig.Api.Registered, entry => Assert.Equal(0x4003u, entry.Modifiers));
        Assert.Equal(3, rig.Api.Registered.Select(entry => entry.Id).Distinct().Count());
    }

    [Fact]
    public void W02_MessageWithTheCommandIdRaisesExactlyOnePressedWithTheGivenTimestamp()
    {
        using var rig = new Rig();
        rig.Register(HotkeyAction.Record, CtrlAltY);
        rig.Register(HotkeyAction.Command, CtrlAltX);
        rig.Register(HotkeyAction.Cancel, CtrlAltQ);

        rig.Service.OnHotkeyMessage(rig.Api.IdOf(KeyX), 123_456);

        var pressed = Assert.Single(rig.Pressed);
        Assert.Equal(new HotkeyEvent(HotkeyAction.Command, 123_456), pressed);
        Assert.Empty(rig.Released);
    }

    [Fact]
    public void W02_RejectingApiYieldsTakenBySystemOrOtherApp()
    {
        using var rig = new Rig();
        rig.Api.Accept = (_, _, _) => false;

        var result = rig.Service.Register(HotkeyAction.Record, CtrlAltY);

        Assert.Equal(HotkeyConflict.TakenBySystemOrOtherApp, result);
        Assert.Empty(rig.Api.Registered);
    }

    [Theory]
    [InlineData(HotkeyConflict.TypingKey, HotkeyModifiers.None, KeyY)]
    [InlineData(HotkeyConflict.Invalid, HotkeyModifiers.None, Ctrl)]
    [InlineData(HotkeyConflict.Reserved, HotkeyModifiers.None, 0x7B)]
    [InlineData(HotkeyConflict.OwnDuplicate, HotkeyModifiers.Control | HotkeyModifiers.Alt, KeyY)]
    [InlineData(HotkeyConflict.AltGrCharacter, HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x32)]
    public void W02_ConflictFromCheckDoesNotCallTheApi(HotkeyConflict expected, HotkeyModifiers modifiers, int virtualKey)
    {
        using var rig = new Rig();
        rig.Register(HotkeyAction.Record, CtrlAltY);
        var callsBefore = rig.Api.Calls.ToArray();

        var result = rig.Service.Register(HotkeyAction.Command, new HotkeyGesture(modifiers, virtualKey));

        Assert.Equal(expected, result);
        Assert.Equal(callsBefore, rig.Api.Calls);
    }

    [Fact]
    public void W02_ReplacingRegistersTheNewCombinationFirstAndUnregistersTheOldOneAfterwards()
    {
        using var rig = new Rig();
        rig.Register(HotkeyAction.Record, CtrlAltY);
        var oldId = rig.Api.IdOf(KeyY);

        var result = rig.Service.Register(HotkeyAction.Record, CtrlAltW);

        Assert.Equal(HotkeyConflict.None, result);
        var newId = rig.Api.IdOf(KeyW);
        Assert.NotEqual(oldId, newId);
        Assert.Equal(
            new[] { $"R {oldId} 0x4003 0x59", $"R {newId} 0x4003 0x57", $"U {oldId}" },
            rig.Api.Calls);

        rig.Service.OnHotkeyMessage(newId, 7);
        Assert.Equal(new HotkeyEvent(HotkeyAction.Record, 7), Assert.Single(rig.Pressed));
    }

    [Fact]
    public void W02_WhenTheApiRejectsTheNewCombinationTheOldOneStaysRegisteredAndFires()
    {
        using var rig = new Rig();
        rig.Register(HotkeyAction.Record, CtrlAltY);
        var oldId = rig.Api.IdOf(KeyY);
        rig.Api.Accept = (_, _, virtualKey) => virtualKey != KeyW;

        var result = rig.Service.Register(HotkeyAction.Record, CtrlAltW);

        Assert.Equal(HotkeyConflict.TakenBySystemOrOtherApp, result);
        Assert.Empty(rig.Api.Unregistered);
        Assert.DoesNotContain(rig.Api.Calls, call => call.StartsWith('U'));
        Assert.Equal((oldId, 0x4003u, KeyY), Assert.Single(rig.Api.Registered));

        rig.Service.OnHotkeyMessage(oldId, 99);
        Assert.Equal(new HotkeyEvent(HotkeyAction.Record, 99), Assert.Single(rig.Pressed));
    }

    // ---- W03: Halten --------------------------------------------------------------------------------------------

    /// <summary>Aufnahme mit <c>trackRelease</c> angemeldet, Haupttaste gedrückt, Kombination ausgelöst (Zeit 0).</summary>
    private static Rig HoldRig(HotkeyGesture gesture)
    {
        var rig = new Rig();
        rig.Register(HotkeyAction.Record, gesture, trackRelease: true);
        rig.Keys.Press(gesture.VirtualKey);
        rig.Service.OnHotkeyMessage(rig.Api.IdOf(gesture.VirtualKey), rig.Clock.GetTimestamp());
        return rig;
    }

    [Fact]
    public void W03_NoReleaseWhileTheMainKeyStaysDownForASecond()
    {
        using var rig = HoldRig(CtrlAltY);

        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(rig.Released);
        Assert.NotEmpty(rig.Keys.Queried);
    }

    [Fact]
    public void W03_ReleaseComesOnTheSecondUpPollWithItsTimestamp()
    {
        using var rig = HoldRig(CtrlAltY);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(rig.Released);
        var queriesBefore = rig.Keys.Queried.Count;

        rig.Keys.Lift(KeyY);
        rig.Clock.Advance(Poll);
        Assert.Empty(rig.Released); // erste Abfrage, die oben meldet
        Assert.Equal(queriesBefore + 1, rig.Keys.Queried.Count);

        rig.Clock.Advance(Poll);
        var (released, timestampAtPoll) = Assert.Single(rig.Released);
        Assert.Equal(HotkeyAction.Record, released.Action);
        Assert.Equal(timestampAtPoll, released.Timestamp); // Zeitstempel der Abfrage, nicht später und nicht früher
        Assert.True(released.Timestamp > TimeSpan.FromSeconds(1).Ticks);
        Assert.Equal(queriesBefore + 2, rig.Keys.Queried.Count);
    }

    [Fact]
    public void W03_AnUpPollBetweenDownPollsDoesNotCountBecauseItMustBeTwoInARow()
    {
        using var rig = HoldRig(CtrlAltY);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(300));

        rig.Keys.Lift(KeyY);
        rig.Clock.Advance(Poll); // oben
        rig.Keys.Press(KeyY);
        rig.Clock.Advance(Poll); // unten
        rig.Keys.Lift(KeyY);
        rig.Clock.Advance(Poll); // oben, aber erst die erste in Folge
        Assert.Empty(rig.Released);

        rig.Clock.Advance(Poll); // zweite in Folge
        Assert.Single(rig.Released);
    }

    [Fact]
    public void W03_PollingEndsAfterTheRelease()
    {
        using var rig = HoldRig(CtrlAltY);
        rig.Keys.Lift(KeyY);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(30));
        Assert.Single(rig.Released);
        var queries = rig.Keys.Queried.Count;

        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(queries, rig.Keys.Queried.Count);
        Assert.Single(rig.Released);
    }

    [Fact]
    public void W03_OnlyTheCodeOfTheMainKeyIsQueried()
    {
        using var rig = HoldRig(CtrlAltY);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Keys.Lift(KeyY);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(30));

        Assert.NotEmpty(rig.Keys.Queried);
        Assert.All(rig.Keys.Queried, code => Assert.Equal(KeyY, code));
    }

    [Fact]
    public void W03_WithoutTrackReleaseThereIsNotASingleQuery()
    {
        using var rig = new Rig();
        rig.Register(HotkeyAction.Record, CtrlAltY);
        rig.Keys.Press(KeyY);
        rig.Service.OnHotkeyMessage(rig.Api.IdOf(KeyY), 1);

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Keys.Lift(KeyY);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(rig.Keys.Queried);
        Assert.Empty(rig.Released);
    }

    [Fact]
    public void W03_UnregisteringDuringTheHoldRaisesExactlyOneReleasedAndStopsPolling()
    {
        using var rig = HoldRig(CtrlAltY);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Empty(rig.Released);

        rig.Service.Unregister(HotkeyAction.Record);

        var (released, _) = Assert.Single(rig.Released);
        Assert.Equal(HotkeyAction.Record, released.Action);
        var queries = rig.Keys.Queried.Count;

        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(queries, rig.Keys.Queried.Count);
        Assert.Single(rig.Released);
    }

    [Fact]
    public void W03_DisposingTheServiceDuringTheHoldRaisesExactlyOneReleasedAndStopsPolling()
    {
        using var rig = HoldRig(CtrlAltY);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Empty(rig.Released);

        rig.DisposeService();

        var (released, _) = Assert.Single(rig.Released);
        Assert.Equal(HotkeyAction.Record, released.Action);
        var queries = rig.Keys.Queried.Count;

        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(queries, rig.Keys.Queried.Count);
        Assert.Single(rig.Released);
    }

    // ---- W04: Halten mit Einrastfunktion ------------------------------------------------------------------------

    [Theory]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, KeyY)]
    [InlineData(HotkeyModifiers.Windows | HotkeyModifiers.Shift, KeyF8)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Windows, KeyY)]
    public void W04_MainKeyDownAndModifiersUpGivesNoReleaseAndModifiersAreNeverQueried(HotkeyModifiers modifiers, int virtualKey)
    {
        using var rig = HoldRig(new HotkeyGesture(modifiers, virtualKey));

        rig.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(rig.Released);
        Assert.NotEmpty(rig.Keys.Queried);
        Assert.All(rig.Keys.Queried, code => Assert.Equal(virtualKey, code));
        Assert.DoesNotContain(rig.Keys.Queried, code => code is Ctrl or Alt or Shift or WinLeft or WinRight);
    }

    // ---- W05: OwnKeysReleased -----------------------------------------------------------------------------------

    /// <summary>Aufnahme Strg+Alt+Y und Befehl Strg+Alt+X angemeldet, ohne Halten.</summary>
    private static Rig OwnKeysRig()
    {
        var rig = new Rig();
        rig.Register(HotkeyAction.Record, CtrlAltY);
        rig.Register(HotkeyAction.Command, CtrlAltX);
        return rig;
    }

    [Fact]
    public void W05_BeforeAnyTriggerItIsTrueWithoutAQuery()
    {
        using var rig = OwnKeysRig();
        rig.Keys.Press(Ctrl, Alt, KeyY, KeyX);

        Assert.True(rig.Service.OwnKeysReleased());
        Assert.Empty(rig.Keys.Queried);
    }

    [Theory]
    [InlineData(Ctrl)]
    [InlineData(Alt)]
    [InlineData(KeyY)]
    public void W05_AfterTheTriggerAnyKeyOfTheCombinationStillDownGivesFalse(int keyDown)
    {
        using var rig = OwnKeysRig();
        rig.Service.OnHotkeyMessage(rig.Api.IdOf(KeyY), 1);
        rig.Keys.Press(keyDown);

        Assert.False(rig.Service.OwnKeysReleased());
    }

    [Fact]
    public void W05_AllKeysUpGivesTrueAndThenTrueWithoutFurtherQueries()
    {
        using var rig = OwnKeysRig();
        rig.Service.OnHotkeyMessage(rig.Api.IdOf(KeyY), 1);
        rig.Keys.Press(Ctrl);
        Assert.False(rig.Service.OwnKeysReleased());

        rig.Keys.Lift(Ctrl, Alt, KeyY);
        Assert.True(rig.Service.OwnKeysReleased());
        var queries = rig.Keys.Queried.Count;

        rig.Keys.Press(Ctrl, Alt, KeyY);
        Assert.True(rig.Service.OwnKeysReleased());
        Assert.Equal(queries, rig.Keys.Queried.Count);
        Assert.All(rig.Keys.Queried, code => Assert.Contains(code, new[] { Ctrl, Alt, KeyY }));
    }

    [Fact]
    public void W05_OnlyTheKeysOfTheLastTriggeredCombinationAreQueried()
    {
        using var rig = OwnKeysRig();
        rig.Keys.Press(Shift, WinLeft, WinRight, KeyX); // gehören nicht zu Strg+Alt+Y
        rig.Service.OnHotkeyMessage(rig.Api.IdOf(KeyY), 1);

        Assert.True(rig.Service.OwnKeysReleased());
        Assert.NotEmpty(rig.Keys.Queried);
        Assert.All(rig.Keys.Queried, code => Assert.Contains(code, new[] { Ctrl, Alt, KeyY }));
    }

    [Fact]
    public void W05_ANewTriggerArmsTheCheckAgainForThatCombination()
    {
        using var rig = OwnKeysRig();
        rig.Service.OnHotkeyMessage(rig.Api.IdOf(KeyY), 1);
        Assert.True(rig.Service.OwnKeysReleased());
        rig.Keys.Press(Ctrl, KeyY);
        Assert.True(rig.Service.OwnKeysReleased()); // schon einmal alle oben gemeldet

        rig.Service.OnHotkeyMessage(rig.Api.IdOf(KeyX), 2); // Strg ist noch unten
        Assert.False(rig.Service.OwnKeysReleased());

        rig.Keys.Lift(Ctrl); // Y bleibt unten, gehört aber nicht zur Befehlstaste
        Assert.True(rig.Service.OwnKeysReleased());
    }

    [Theory]
    [InlineData(Shift)]
    [InlineData(WinLeft)]
    [InlineData(WinRight)]
    public void W05_ShiftAndWindowsModifiersOfTheCombinationAreQueriedToo(int keyDown)
    {
        using var rig = new Rig();
        rig.Register(HotkeyAction.Record, WinShiftF8);
        rig.Service.OnHotkeyMessage(rig.Api.IdOf(KeyF8), 1);
        rig.Keys.Press(keyDown);

        Assert.False(rig.Service.OwnKeysReleased());

        rig.Keys.Lift(keyDown);
        Assert.True(rig.Service.OwnKeysReleased());
    }

    // ---- W06: RecordKeyInterpreter ------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void W06_ToggleStartsAndStopsOnPressAndIgnoresRelease(bool holdApproved)
    {
        var interpreter = new RecordKeyInterpreter(RecordingMode.Toggle, holdApproved);

        Assert.Equal(RecordCommand.Start, interpreter.OnPressed(false));
        Assert.Equal(RecordCommand.Stop, interpreter.OnPressed(true));
        Assert.Null(interpreter.OnReleased(false));
        Assert.Null(interpreter.OnReleased(true));
        Assert.False(interpreter.HoldUnavailable);
    }

    [Fact]
    public void W06_ApprovedHoldStartsOnPressAndStopsOnRelease()
    {
        var interpreter = new RecordKeyInterpreter(RecordingMode.Hold, holdApproved: true);

        Assert.Equal(RecordCommand.Start, interpreter.OnPressed(false));
        Assert.Equal(RecordCommand.Stop, interpreter.OnReleased(true));
        Assert.False(interpreter.HoldUnavailable);
    }

    [Fact]
    public void W06_HoldWithoutApprovalActsAsToggleAndIsMarkedUnavailable()
    {
        var interpreter = new RecordKeyInterpreter(RecordingMode.Hold, holdApproved: false);

        Assert.True(interpreter.HoldUnavailable);
        Assert.Equal(RecordCommand.Start, interpreter.OnPressed(false));
        Assert.Equal(RecordCommand.Stop, interpreter.OnPressed(true));
        Assert.Null(interpreter.OnReleased(false));
        Assert.Null(interpreter.OnReleased(true));
    }

    // ---- Hilfen -------------------------------------------------------------------------------------------------

    /// <summary>Dienst mit allen Attrappen, Startwert <c>PollInterval</c> 15 ms; sammelt die Ereignisse.</summary>
    private sealed class Rig : IDisposable
    {
        private bool _serviceDisposed;

        public Rig()
        {
            Service = new HotkeyService(Api, Keys, Layout, new HoldOptions(PollInterval: Poll), Clock);
            Service.Pressed += (_, e) => Pressed.Add(e);
            Service.Released += (_, e) => Released.Add((e, Clock.GetTimestamp()));
        }

        public ManualClock Clock { get; } = new();

        public FakeHotkeyApi Api { get; } = new();

        public FakeKeyStateProbe Keys { get; } = new();

        public FakeLayout Layout { get; } = new();

        public HotkeyService Service { get; }

        public List<HotkeyEvent> Pressed { get; } = [];

        /// <summary>Jedes <c>Released</c> mit dem Zeitstempel der Uhr im Augenblick des Ereignisses.</summary>
        public List<(HotkeyEvent Event, long TimestampNow)> Released { get; } = [];

        public void Register(HotkeyAction action, HotkeyGesture gesture, bool trackRelease = false) =>
            Assert.Equal(HotkeyConflict.None, Service.Register(action, gesture, trackRelease));

        public void DisposeService()
        {
            _serviceDisposed = true;
            Service.Dispose();
        }

        public void Dispose()
        {
            if (!_serviceDisposed)
            {
                Service.Dispose();
            }
        }
    }
}
