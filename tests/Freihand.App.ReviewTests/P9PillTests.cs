using Freihand.Adapters.Dictation;
using Freihand.Adapters.Recognition;
using Freihand.Core.Output;

namespace Freihand.App;

/// <summary>
/// D09 und D10: Pille je Stand der Steuerung, Ausblenden nach
/// „fertig“, Symbol im Infobereich, Ton ein und aus. Die Pille nimmt nie den Fokus: geprüft am Quelltext des Fensters.
/// </summary>
public sealed class P9PillTests
{
    private static ControllerUpdate Idle(int dictation = 0, int written = 0, int held = 0, ControllerMessage message = ControllerMessage.None,
        string? reason = null, int messageNumber = 0) =>
        new(ControllerPhase.Idle, RecognizerAvailability.Ready, null, null, string.Empty, [], message, reason,
            Dictation: dictation, MessageNumber: messageNumber, Written: written, HeldOpen: held);

    private static ControllerUpdate Running(
        ControllerPhase phase, TimeSpan? focusWait = null, bool toWindow = false, MicrophoneSignal signal = MicrophoneSignal.Ok) =>
        new(phase, RecognizerAvailability.Ready, "Word – Dokumenttext", null, string.Empty, [], ControllerMessage.None, null,
            FocusWait: focusWait, ToDictationWindow: toWindow, Dictation: 1, Microphone: "Headset", Signal: signal);

    // Bedienregel vom 19.11.2026: ein in Windows stumm geschaltetes Mikrofon fällt während der Aufnahme sofort auf
    [Fact]
    public void AMutedMicrophoneTurnsTheRecordingPillYellowWithAClearText()
    {
        var pill = new PillPresenter(sound: false);

        var muted = pill.Show(Running(ControllerPhase.Recording, signal: MicrophoneSignal.Muted));

        Assert.Equal((PillState.Recording, StateColor.Yellow, "Mikrofon ist stumm geschaltet"), (muted.State, muted.Color, muted.Text));
        Assert.Contains("Mikrofon: Headset", muted.Details);
        Assert.Equal(StateColor.Yellow, PillPresenter.TrayColor(Running(ControllerPhase.Recording, signal: MicrophoneSignal.Muted), 0));
        Assert.Equal("Freihand – Aufnahme, Mikrofon stumm",
            PillPresenter.TrayText(Running(ControllerPhase.Recording, signal: MicrophoneSignal.Muted), 0));
        Assert.Equal(StateColor.Red, pill.Show(Running(ControllerPhase.Recording)).Color);
    }

    [Fact]
    public void D09_NoReadyPillInNormalOperation()
    {
        var pill = new PillPresenter(sound: false);

        Assert.Equal(PillState.Hidden, pill.Show(Idle()).State);
        Assert.Equal(StateColor.Grey, PillPresenter.TrayColor(Idle(), offers: 0));
    }

    [Fact]
    public void D09_RecordingShowsARedDotAndDetails()
    {
        var pill = new PillPresenter(sound: false);

        var view = pill.Show(Running(ControllerPhase.Recording));

        Assert.Equal(PillState.Recording, view.State);
        Assert.Equal(StateColor.Red, view.Color);
        Assert.Contains("Ziel: Word – Dokumenttext", view.Details);
        Assert.Contains("Mikrofon: Headset", view.Details);
        Assert.Equal(StateColor.Red, PillPresenter.TrayColor(Running(ControllerPhase.Recording), 0));
        Assert.Equal("Aufnahme ins Diktatfenster", pill.Show(Running(ControllerPhase.Recording, toWindow: true)).Text);
        Assert.Equal(PillState.Starting, pill.Show(Running(ControllerPhase.Starting)).State);
        Assert.Equal(PillState.Finishing, pill.Show(Running(ControllerPhase.Stopping)).State);
    }

    [Fact]
    public void D09_FocusAwayShowsTheRemainingTimeInYellow()
    {
        var pill = new PillPresenter(sound: false);

        var view = pill.Show(Running(ControllerPhase.Recording, TimeSpan.FromSeconds(41.2)));

        Assert.Equal(PillState.FocusAway, view.State);
        Assert.Equal(StateColor.Yellow, view.Color);
        Assert.Equal("Zurück ins Feld klicken · 42 s", view.Text);
        Assert.Equal(PillState.FocusAway, pill.Show(Running(ControllerPhase.Stopping, TimeSpan.FromSeconds(3))).State);
    }

    [Fact]
    public void D09_DoneIsGreenAndHidesAfterAFewSecondsUntilTheNextDictation()
    {
        var pill = new PillPresenter(sound: false);
        var done = Idle(dictation: 1, written: 3);

        var view = pill.Show(done);
        Assert.Equal(PillState.Done, view.State);
        Assert.Equal(StateColor.Green, view.Color);
        Assert.Equal("3 Abschnitte geschrieben", view.Text);
        Assert.Equal(PillPresenter.DoneVisible, view.HideAfter);

        pill.Dismiss(done, PillState.Done);
        Assert.Equal(PillState.Hidden, pill.Show(done).State);
        Assert.Equal(PillState.Hidden, pill.Show(done with { Recognizer = RecognizerAvailability.Restarting }).State);
        Assert.Equal(PillState.Done, pill.Show(Idle(dictation: 2, written: 1)).State);
    }

    [Fact]
    public void D09_HeldIsYellowStaysAndOpensTheWindow()
    {
        var pill = new PillPresenter(sound: false);
        var held = Idle(dictation: 1, written: 1, held: 2);

        var view = pill.Show(held);

        Assert.Equal(PillState.Held, view.State);
        Assert.Equal(StateColor.Yellow, view.Color);
        Assert.Equal("2 Abschnitte gehalten – ins Diktatfenster holen", view.Text);
        Assert.Null(view.HideAfter);
        Assert.True(view.OpensDictationWindow);
        Assert.Equal(StateColor.Yellow, PillPresenter.TrayColor(held, 0));

        // Weggeklickt bleibt weg; mehr gehaltener Text erscheint wieder
        pill.Dismiss(held, PillState.Held);
        Assert.NotEqual(PillState.Held, pill.Show(held).State);
        Assert.Equal(PillState.Held, pill.Show(held with { HeldOpen = 3 }).State);
    }

    [Fact]
    public void D09_AProblemIsANoticeOnceAndAHintFromTheDictationIsNot()
    {
        var pill = new PillPresenter(sound: false);
        var refused = Idle(dictation: 1, message: ControllerMessage.BindRefused, reason: "PasswordField", messageNumber: 4);

        var view = pill.Show(refused);
        Assert.Equal(PillState.Notice, view.State);
        Assert.Equal("Feld nicht bindbar: PasswordField", view.Text);
        Assert.Equal(PillPresenter.NoticeVisible, view.HideAfter);

        pill.Dismiss(refused, PillState.Notice);
        Assert.Equal(PillState.Hidden, pill.Show(refused).State);
        Assert.Equal(PillState.Hidden, pill.Show(Idle(dictation: 1, message: ControllerMessage.CommandHint, messageNumber: 5)).State);
        Assert.Equal(StateColor.Grey, pill.Show(Idle(dictation: 2, message: ControllerMessage.Cancelled, messageNumber: 6)).Color);
    }

    [Fact]
    public void D09_DismissOnlyHidesTheStateThatWasShown()
    {
        var pill = new PillPresenter(sound: false);
        var held = Idle(dictation: 1, written: 2, held: 1);

        // Der Zeitgeber von „fertig“ läuft ab, inzwischen ist etwas gehalten: „gehalten“ bleibt stehen
        pill.Dismiss(held, PillState.Done);

        Assert.Equal(PillState.Held, pill.Show(held).State);
    }

    [Fact]
    public void D09_TrayIsYellowForOffersAndAnUnavailableRecognizer()
    {
        Assert.Equal(StateColor.Yellow, PillPresenter.TrayColor(Idle(), offers: 2));
        Assert.Equal("Freihand – 2 gerettete Texte", PillPresenter.TrayText(Idle(), offers: 2));
        var unavailable = Idle() with { Recognizer = RecognizerAvailability.Unavailable };
        Assert.Equal(StateColor.Yellow, PillPresenter.TrayColor(unavailable, 0));
        Assert.Equal("Freihand – bereit", PillPresenter.TrayText(Idle(), 0));
        Assert.True(PillPresenter.TrayText(Idle(held: 1), 0).Length <= 127);
    }

    // Vorstufe zur Beta: „Erkennung neu starten“ nur bei nicht verfügbarem Erkenner und ohne laufendes Diktat
    [Fact]
    public void RestartIsOfferedOnlyForAnUnavailableRecognizerWhileIdle()
    {
        var unavailable = Idle() with { Recognizer = RecognizerAvailability.Unavailable };

        Assert.True(PillPresenter.CanRestartRecognizer(unavailable));
        Assert.False(PillPresenter.CanRestartRecognizer(Idle()));
        Assert.False(PillPresenter.CanRestartRecognizer(Idle() with { Recognizer = RecognizerAvailability.Starting }));
        Assert.False(PillPresenter.CanRestartRecognizer(unavailable with { Phase = ControllerPhase.Stopping }));
        Assert.Contains("im Menü neu starten", PillPresenter.TrayText(unavailable, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void D10_ToneOnlyWhenSwitchedOnAtStartAndEnd()
    {
        var silent = new PillPresenter(sound: false);
        Assert.Null(silent.ToneFor(Running(ControllerPhase.Starting)));
        Assert.Null(silent.ToneFor(Running(ControllerPhase.Recording)));
        Assert.Null(silent.ToneFor(Idle(dictation: 1)));

        var sound = new PillPresenter(sound: true);
        Assert.Null(sound.ToneFor(Running(ControllerPhase.Starting)));
        Assert.Equal(Tone.Start, sound.ToneFor(Running(ControllerPhase.Recording)));
        Assert.Null(sound.ToneFor(Running(ControllerPhase.Recording)));
        Assert.Null(sound.ToneFor(Running(ControllerPhase.Stopping)));
        Assert.Equal(Tone.End, sound.ToneFor(Idle(dictation: 1)));

        // Ein Start, der vor der Aufnahme scheitert, klingt nicht
        Assert.Null(sound.ToneFor(Running(ControllerPhase.Starting)));
        Assert.Null(sound.ToneFor(Idle(dictation: 2)));

        sound.Sound = false;
        Assert.Null(sound.ToneFor(Running(ControllerPhase.Recording)));
    }

    [Fact]
    public void D09_ThePillNeverTakesTheFocus()
    {
        var root = RepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Freihand.App", "PillWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "Freihand.App", "PillWindow.xaml.cs"));

        Assert.Contains("ShowActivated=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Focusable\" Value=\"False\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("NoActivate = 0x08000000", code, StringComparison.Ordinal);
        Assert.Contains("| NoActivate", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Activate()", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".Focus()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void HandOverReasonsAreInPlainWords()
    {
        Assert.Equal("Nicht übernommen, Text bleibt im Diktatfenster: ein anderes Feld hat den Fokus",
            StatusText.Message(ControllerMessage.HandOverRefused, "NotMarkedField"));
        Assert.Equal("Nicht übernommen, Text bleibt im Diktatfenster: Zeilenumbrüche sind für dieses Feld noch nicht freigegeben",
            StatusText.Message(ControllerMessage.HandOverRefused, nameof(OutputReason.LineBreakRefused)));
        Assert.Equal("Sicherung gescheitert: WriteFailed", StatusText.Message(ControllerMessage.RecoveryFailed, "WriteFailed"));
    }

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Freihand.slnx")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("Freihand.slnx nicht gefunden");
    }

    [Fact]
    public void AFocusWaitDoesNotHideTheMuteWarningDuringRecording()
    {
        var pill = new PillPresenter(sound: false);
        var view = pill.Show(Running(ControllerPhase.Recording, focusWait: TimeSpan.FromSeconds(5), signal: MicrophoneSignal.Muted));

        Assert.Equal(StateColor.Yellow, view.Color);
        Assert.Contains("Mikrofon ist stumm geschaltet", view.Text + " " + view.Details, StringComparison.Ordinal);
    }

    // Codex-Befund PR #68: Der Countdown im Fenster überschreibt die Hauptzeile; er rechnet mit demselben Text
    [Fact]
    public void TheWindowCountdownKeepsTheMuteWarning()
    {
        var code = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Freihand.App", "PillWindow.xaml.cs"));
        var pill = new PillPresenter(sound: false);

        Assert.Contains("PillPresenter.FocusText(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Zurück ins Feld klicken", code, StringComparison.Ordinal);
        Assert.Equal("Zurück ins Feld klicken · 3 s · Mikrofon ist stumm geschaltet", PillPresenter.FocusText(TimeSpan.FromSeconds(2.5), true));
        Assert.Equal("Zurück ins Feld klicken · 0 s", PillPresenter.FocusText(TimeSpan.FromSeconds(-1), false));
        Assert.True(pill.Show(Running(ControllerPhase.Recording, focusWait: TimeSpan.FromSeconds(5), signal: MicrophoneSignal.Muted)).MicrophoneMuted);
        Assert.Equal("Zurück ins Feld klicken · 5 s",
            pill.Show(Running(ControllerPhase.Stopping, focusWait: TimeSpan.FromSeconds(5), signal: MicrophoneSignal.Muted)).Text);
    }
}
