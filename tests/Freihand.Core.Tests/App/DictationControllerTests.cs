using Freihand.Adapters.Dictation;
using Freihand.Adapters.Recognition;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests.App;

/// <summary>
/// P9-Konzept, Kapitel 5, Stufe A, X01–X16: der <see cref="DictationController"/> mit
/// Attrappen. Gewartet wird nur mit Zeitgrenze (<see cref="ControllerRig.Timeout"/>) auf beobachtbare Zustände, nie mit
/// festen Pausen als Beweis. Erfundene Texte.
/// </summary>
public sealed class DictationControllerTests
{
    private const string FirstText = "Der Hafenmeister prüft die Liste";
    private const string SecondText = "Die Laterne bleibt bis morgen an";
    private const string LateText = "Am Ende kommt noch ein Satz";

    // ---- X01 ----------------------------------------------------------------------------------------------------

    // BE-01, ER-17, ER-19: Binden, Wächter, Erkennersitzung, Mikrofon offen, dann Lesen über den Lauf.
    [Theory]
    [InlineData(ProfileStrictness.Standard)]
    [InlineData(ProfileStrictness.Strict)]
    public async Task X01_StartsInTheOrderBindArmSessionOpenRead(ProfileStrictness strictness)
    {
        await using var rig = new ControllerRig(ControllerRig.Options(strictness));

        var run = await rig.StartAsync();

        var log = rig.Log;
        var bind = log.IndexOf($"Binding.BindFocused:{strictness}");
        var arm = log.IndexOf("Guard.Arm");
        var factory = log.IndexOf("Factory.StartAsync");
        var open = log.IndexOf($"Capture.Open:{FakeAudioCapture.MicA.Id}");
        var read = log.IndexOf("Capture.ReadAsync");
        var runAsync = log.IndexOf("Run.RunAsync");
        Assert.True(bind >= 0, "BindFocused muss mit der Strenge des Profils aufgerufen werden");
        Assert.True(arm > bind, "Der Wächter wird erst nach dem Binden scharf");
        Assert.True(factory > arm, "Die Erkennersitzung startet erst, wenn der Wächter scharf ist");
        Assert.True(open > factory, "Das Mikrofon öffnet erst nach der Erkennersitzung");
        Assert.True(read > open, "Gelesen wird erst von der geöffneten Aufnahme");
        Assert.True(runAsync > open, "Der Lauf bekommt das Audio der geöffneten Aufnahme");
        Assert.True(run.AudioReceived, "RunAsync muss das Audio der Aufnahme erhalten");
        Assert.Equal(1, rig.Guard.ArmCount);
        Assert.Equal(1, rig.Factory.StartCount);
        Assert.Single(rig.Audio.Sessions);
    }

    // P8 Kapitel 5.2: „aufnehmend“ erst ab CapturePhase.Recording; Ziel und Modus stehen in der Anzeige.
    [Fact]
    public async Task X01_RecordingPhaseOnlyFromCaptureRecording()
    {
        var mode = new ModeDecision(LiveMode.Sentence, RecognitionMode.Live, ModeReason.NotApproved);
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Factory.Mode = mode;
            r.Binding.Next = new BindResult(FakeBinding.NewTarget("Testeditor Dokumenttext"), null);
        });

        var run = await rig.StartAsync();

        await rig.WaitForPhase(ControllerPhase.Starting);
        Assert.DoesNotContain(rig.Updates, update => update.Phase == ControllerPhase.Recording);
        Assert.Equal(mode, run.Mode);

        rig.Audio.Session!.SetRecording();
        await rig.WaitForPhase(ControllerPhase.Recording);

        var current = rig.Controller.Current;
        Assert.Equal("Testeditor Dokumenttext", current.Target);
        Assert.Equal(run.Mode, current.Mode);
        Assert.Equal(RecognizerAvailability.Ready, current.Recognizer);
    }

    // ER-17: Gewähltes Gerät aus den Optionen, sonst das Standardgerät.
    [Theory]
    [InlineData(null, "mikro-a")]
    [InlineData("mikro-b", "mikro-b")]
    public async Task X01_OpensTheChosenOrTheDefaultDevice(string? deviceId, string expectedDevice)
    {
        await using var rig = new ControllerRig(ControllerRig.Options(ProfileStrictness.Standard, deviceId));

        await rig.StartAsync();

        Assert.True(rig.Log.Contains($"Capture.Open:{expectedDevice}"), $"Geöffnet werden muss das Gerät {expectedDevice}");
        Assert.Equal(1, rig.Log.CountStarting("Capture.Open:"));
    }

    // D5 a: Jeder Diktatstart bindet neu; das zweite Diktat schreibt in die neue Bindung.
    [Fact]
    public async Task X01_EachDictationBindsAgain()
    {
        await using var rig = new ControllerRig();
        var first = await rig.StartRecordingAsync();
        await rig.EndAsync(first);

        var secondTarget = FakeBinding.NewTarget("Zweites Zielfeld");
        rig.Binding.Next = new BindResult(secondTarget, null);
        var second = await rig.StartRecordingAsync();
        var segment = Fixed(FirstText);
        second.Emit(Output(segment));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "Schreiben im zweiten Diktat");
        await rig.EndAsync(second);

        Assert.Equal(2, rig.Binding.BindCount);
        Assert.Equal(secondTarget.Display, rig.Controller.Current.Target);
        Assert.Equal(secondTarget, Assert.Single(rig.Output.Calls).Target);
        Assert.Equal(2, rig.Audio.Sessions.Count);
        Assert.All(rig.Audio.Sessions, session => Assert.Equal(1, session.DisposeCount));
    }

    // ---- X02 ----------------------------------------------------------------------------------------------------

    // ER-13: Erkenner startet oder startet neu: Meldung, nichts angefasst.
    [Theory]
    [InlineData(RecognizerAvailability.Starting)]
    [InlineData(RecognizerAvailability.Restarting)]
    public async Task X02_RecognizerNotReadyMeansNoStart(RecognizerAvailability availability)
    {
        await using var rig = new ControllerRig(arrange: r => r.Host.Preset(availability));

        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Empty(rig.Log.Snapshot());
        Assert.Contains(rig.Updates, update => update.Message == ControllerMessage.RecognizerNotReady);
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
    }

    // ER-13: Unavailable meldet den Grund (LastError) als Code-Name.
    [Theory]
    [InlineData(RecognizerErrorCode.ModelMissing)]
    [InlineData(RecognizerErrorCode.ModelChecksum)]
    [InlineData(RecognizerErrorCode.RuntimeUnavailable)]
    [InlineData(RecognizerErrorCode.InvalidConfiguration)]
    public async Task X02_RecognizerUnavailableReportsTheLastError(RecognizerErrorCode lastError)
    {
        await using var rig = new ControllerRig(arrange: r => r.Host.Preset(RecognizerAvailability.Unavailable, lastError));

        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Empty(rig.Log.Snapshot());
        Assert.Contains(rig.Updates, update =>
            update.Message == ControllerMessage.RecognizerUnavailable && update.Reason == lastError.ToString());
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
    }

    // Die Anzeige spiegelt den Zustand des Erkenners, anfangs und bei jeder Änderung (Kapitel 2, Start der App).
    [Theory]
    [InlineData(RecognizerAvailability.Starting)]
    [InlineData(RecognizerAvailability.Ready)]
    [InlineData(RecognizerAvailability.Restarting)]
    [InlineData(RecognizerAvailability.Unavailable)]
    public async Task X02_UpdateMirrorsTheRecognizerAvailability(RecognizerAvailability initial)
    {
        await using var rig = new ControllerRig(arrange: r => r.Host.Preset(initial));
        Assert.Equal(initial, rig.Controller.Current.Recognizer);

        var next = initial == RecognizerAvailability.Ready ? RecognizerAvailability.Restarting : RecognizerAvailability.Ready;
        rig.Host.Set(next);

        await rig.WaitUntil(() => rig.Controller.Current.Recognizer == next, "Anzeige des neuen Erkennerzustands");
        Assert.Contains(rig.Updates, update => update.Recognizer == next);
    }

    // Fabrik scheitert nach dem Scharfschalten (Erkenner inzwischen weg oder lehnt die Sitzung ab): Wächter aus, kein Mikrofon.
    [Theory]
    [InlineData("unavailable")]
    [InlineData("rejected")]
    public async Task X02_FactoryFailureReleasesTheGuardWithoutMicrophone(string kind)
    {
        Exception failure = kind == "unavailable"
            ? new RecognizerUnavailableException()
            : new RecognizerException(RecognizerErrorCode.UnsupportedLanguage);
        await using var rig = new ControllerRig(arrange: r => r.Factory.Failure = failure);

        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Equal(0, rig.Log.CountStarting("Capture.Open:"));
        Assert.Equal(1, rig.Guard.DisarmCount);
        Assert.Contains(rig.Updates, update => update.Message != ControllerMessage.None);
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
    }

    // ---- X03 ----------------------------------------------------------------------------------------------------

    public static TheoryData<BindRefusal> AllRefusals() => [.. Enum.GetValues<BindRefusal>()];

    // AU-10, SI-04: Bindung abgewiesen: Meldung mit Grund, kein Wächter, keine Erkennersitzung, kein Mikrofon.
    [Theory]
    [MemberData(nameof(AllRefusals))]
    public async Task X03_BindRefusalStopsTheStartWithReason(BindRefusal refusal)
    {
        await using var rig = new ControllerRig(arrange: r => r.Binding.Next = new BindResult(null, refusal));

        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Equal(["Binding.BindFocused:Standard"], rig.Log.Snapshot());
        Assert.Contains(rig.Updates, update =>
            update.Message == ControllerMessage.BindRefused && update.Reason == refusal.ToString());
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
    }

    // ---- X04 ----------------------------------------------------------------------------------------------------

    // SI-04: Der Wächter meldet schon beim Scharfschalten StopRequired: kein Mikrofon, Wächter wieder aus.
    [Theory]
    [InlineData(GuardReason.PasswordField)]
    [InlineData(GuardReason.SessionLocked)]
    [InlineData(GuardReason.SessionDisconnected)]
    [InlineData(GuardReason.SecureDesktop)]
    public async Task X04_GuardStopDuringArmEndsTheStartWithoutMicrophone(GuardReason reason)
    {
        await using var rig = new ControllerRig(arrange: r => r.Guard.RaiseOnArm = reason);

        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Equal(["Binding.BindFocused:Standard", "Guard.Arm", "Guard.Disarm", "Binding.Release"], rig.Log.Snapshot());
        Assert.Contains(rig.Updates, update =>
            update.Message == ControllerMessage.GuardStopped && update.Reason == reason.ToString());
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
    }

    // Scharfschalten scheitert mit Ausnahme: Meldung, weder Erkennersitzung noch Mikrofon.
    [Fact]
    public async Task X04_ArmFailureReportsGuardFailedWithoutMicrophone()
    {
        await using var rig = new ControllerRig(arrange: r => r.Guard.ArmFailure = new InvalidOperationException("erfundener Fehler"));

        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Equal(0, rig.Factory.StartCount);
        Assert.Equal(0, rig.Log.CountStarting("Capture.Open:"));
        Assert.Contains(rig.Updates, update => update.Message == ControllerMessage.GuardFailed);
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);

        // Codex zu PR #47: auch dieser Startabbruch löst die Bindung
        Assert.True(rig.Log.Contains("Binding.Release"), "Bindung nach gescheitertem Scharfschalten gelöst");
        Assert.Null(rig.Binding.Bound);
    }

    // ---- X05 ----------------------------------------------------------------------------------------------------

    // BE-01: Feste Abschnitte gehen in der Reihenfolge mit dem gebundenen Ziel an WriteAsync.
    [Fact]
    public async Task X05_FixedSegmentsGoToWriteAsyncInOrderWithTheBoundTarget()
    {
        var target = FakeBinding.NewTarget("Testeditor Dokumenttext");
        await using var rig = new ControllerRig(arrange: r => r.Binding.Next = new BindResult(target, null));
        var run = await rig.StartRecordingAsync();
        var a = Fixed(FirstText);
        var b = Fixed(SecondText);
        var c = Fixed(LateText);

        run.Emit(Output(a, b));
        run.Emit(Output(c));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 3, "drei Schreibaufrufe");
        await rig.EndAsync(run);

        Assert.Equal([FirstText, SecondText, LateText], rig.Output.Texts);
        Assert.Equal([a.Id, b.Id, c.Id], rig.Output.Calls.Select(call => call.Segment.Id));
        Assert.All(rig.Output.Calls, call => Assert.Equal(target, call.Target));
        Assert.All(rig.Output.Calls, call => Assert.Equal(SegmentState.Fixed, call.Segment.State));
        var entries = rig.Controller.Current.Entries;
        Assert.Equal(
            [
                new OutputEntry(a.Id, EntryStatus.Written, OutputReason.None, FirstText, ""),
                new OutputEntry(b.Id, EntryStatus.Written, OutputReason.None, SecondText, ""),
                new OutputEntry(c.Id, EntryStatus.Written, OutputReason.None, LateText, ""),
            ],
            entries);
    }

    // BE-01: Vorläufiger Text erscheint nur in der Anzeige, nie an WriteAsync.
    [Fact]
    public async Task X05_ProvisionalTextIsShownButNeverWritten()
    {
        const string provisional = "Das wird noch geändert";
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();

        run.Emit(new TextOutput(new RecognitionUpdate([], [Provisional(provisional)])));
        await rig.WaitUntil(() => rig.Updates.Any(update => update.Provisional == provisional), "Anzeige des vorläufigen Texts");
        run.Emit(Output(Fixed(FirstText)));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "Schreiben des festen Texts");
        await rig.EndAsync(run);

        Assert.Equal([FirstText], rig.Output.Texts);
        Assert.DoesNotContain(rig.Output.Calls, call => call.Segment.State == SegmentState.Provisional);
        Assert.DoesNotContain(rig.Controller.Current.Entries, entry => entry.Written.Contains(provisional, StringComparison.Ordinal));
    }

    // ---- X06 ----------------------------------------------------------------------------------------------------

    // AU-10: Gehaltener oder verweigerter Abschnitt bleibt mit Grund stehen; alle folgenden gehen weiter an WriteAsync
    // (P7 hält sie selbst mit PreviousHeld) und stehen ebenso in der Anzeige.
    [Theory]
    [InlineData(OutputResult.HeldInDictationWindow, OutputReason.FocusElsewhere, EntryStatus.Held)]
    [InlineData(OutputResult.HeldInDictationWindow, OutputReason.TargetGone, EntryStatus.Held)]
    [InlineData(OutputResult.HeldInDictationWindow, OutputReason.KeysHeld, EntryStatus.Held)]
    [InlineData(OutputResult.Refused, OutputReason.PasswordField, EntryStatus.Refused)]
    [InlineData(OutputResult.Refused, OutputReason.LineBreakRefused, EntryStatus.Refused)]
    public async Task X06_HeldOrRefusedSegmentStaysVisibleAndTheFollowingOnesToo(
        OutputResult result, OutputReason reason, EntryStatus expected)
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var a = Fixed(FirstText);
        var b = Fixed(SecondText);
        var c = Fixed(LateText);
        rig.Output.SetReport(a.Id, new OutputReport(result, "", FirstText, reason));
        rig.Output.SetReport(b.Id, new OutputReport(OutputResult.HeldInDictationWindow, "", SecondText, OutputReason.PreviousHeld));
        rig.Output.SetReport(c.Id, new OutputReport(OutputResult.HeldInDictationWindow, "", LateText, OutputReason.PreviousHeld));

        run.Emit(Output(a));
        run.Emit(Output(b));
        run.Emit(Output(c));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 3, "alle drei Abschnitte an WriteAsync");
        await rig.EndAsync(run);

        Assert.Equal([FirstText, SecondText, LateText], rig.Output.Texts);
        Assert.Equal(
            [
                new OutputEntry(a.Id, expected, reason, "", FirstText),
                new OutputEntry(b.Id, EntryStatus.Held, OutputReason.PreviousHeld, "", SecondText),
                new OutputEntry(c.Id, EntryStatus.Held, OutputReason.PreviousHeld, "", LateText),
            ],
            rig.Controller.Current.Entries);
    }

    // Teilweise geschrieben: Eintrag trägt den geschriebenen und den gehaltenen Teil.
    [Fact]
    public async Task X06_PartiallyWrittenSegmentKeepsBothParts()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var a = Fixed("Der Anfang und der Rest");
        rig.Output.SetReport(a.Id, new OutputReport(OutputResult.HeldInDictationWindow, "Der Anfang", " und der Rest", OutputReason.FocusElsewhere));

        run.Emit(Output(a));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "Schreibaufruf");
        await rig.EndAsync(run);

        var entry = Assert.Single(rig.Controller.Current.Entries);
        Assert.Equal(new OutputEntry(a.Id, EntryStatus.Held, OutputReason.FocusElsewhere, "Der Anfang", " und der Rest"), entry);
    }

    // ---- X07 ----------------------------------------------------------------------------------------------------

    // BE-03, ER-17: Zweiter Tastendruck: Stopp, die danach noch gelieferten festen Abschnitte werden geschrieben, dann
    // Wächter aus, Sitzung und Aufnahme frei; die Bindung bleibt (D5).
    [Fact]
    public async Task X07_SecondPressStopsAndWritesTheRemainingFixedText()
    {
        await using var rig = new ControllerRig();
        Assert.True(rig.Controller.Completion.IsCompleted, "Ohne Diktat ist Completion erledigt");
        var run = await rig.StartRecordingAsync();
        var session = rig.Audio.Session!;
        Assert.False(rig.Controller.Completion.IsCompleted, "Während des Diktats ist Completion offen");
        var first = Fixed(FirstText);
        run.Emit(Output(first));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "erster Schreibaufruf");

        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.WaitForPhase(ControllerPhase.Stopping);
        Assert.Equal(1, session.StopCount);
        Assert.False(rig.Controller.Completion.IsCompleted, "Das Diktat ist noch nicht freigegeben");

        // Nach dem Stopp noch gelieferter fester Text; sein Schreiben ist angehalten, damit die Reihenfolge zählt
        var late = Fixed(LateText);
        rig.Output.HoldWritesOf(LateText);
        run.Emit(Output(late));
        run.Complete();
        await rig.WaitUntil(() => rig.Output.Calls.Count == 2, "Schreiben des späten Abschnitts");
        await Task.Delay(50); // Gelegenheit für ein vorzeitiges Disarm; bewiesen wird die Reihenfolge im Protokoll
        rig.Output.ReleaseHold();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        AssertEndsLikeSecondPress(rig, run, session, LateText, expectStop: true);
        Assert.Equal([FirstText, LateText], rig.Output.Texts);
        // Gegencheck vom 10.11.2026: Nach dem letzten Schreibauftrag ist die Bindung gelöst; das nächste Diktat bindet neu (D5 a)
        Assert.True(rig.Log.IndexOf("Capture.Dispose") < rig.Log.IndexOf("Binding.Release"), "Bindung zuletzt gelöst");
        Assert.Null(rig.Binding.Bound);
    }

    // Kapitel 2: Die Aufnahmetaste beim Stoppen ist ohne Wirkung (kein zweiter Stopp, kein neues Diktat).
    [Fact]
    public async Task X07_PressWhileStoppingHasNoEffect()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var session = rig.Audio.Session!;

        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.WaitForPhase(ControllerPhase.Stopping);
        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Equal(1, session.StopCount);
        Assert.Equal(1, rig.Factory.StartCount);
        Assert.Single(rig.Audio.Sessions);
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
    }

    // ---- X08 ----------------------------------------------------------------------------------------------------

    // BE-06: Abbrechen bricht den Token von Lauf und Ausgabe ab; danach beginnt kein Schreiben mehr, auch wenn eine
    // Quelle den Abbruch übergeht und weiterliefert. Freigabe wie beim Stopp.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task X08_CancelCancelsTheTokenAndNothingMoreIsWritten(bool runIgnoresCancellation)
    {
        await using var rig = new ControllerRig(arrange: r => r.Factory.Configure = run => run.IgnoreCancellation = runIgnoresCancellation);
        var run = await rig.StartRecordingAsync();
        var session = rig.Audio.Session!;
        rig.Output.HoldWritesOf(FirstText);
        run.Emit(Output(Fixed(FirstText)));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "laufender Schreibaufruf");
        var runningToken = rig.Output.Calls[0].Token;

        await rig.Controller.CancelAsync().WaitAsync(ControllerRig.Timeout);
        await rig.WaitUntil(() => runningToken.IsCancellationRequested, "abgebrochener Token der Ausgabe");
        run.Emit(Output(Fixed(SecondText)));
        run.Emit(Output(Fixed(LateText)));
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.True(run.ReceivedToken.IsCancellationRequested, "Der Token des Laufs muss abgebrochen sein");
        Assert.Equal([FirstText], rig.Output.Texts);
        Assert.Equal(1, session.StopCount);
        Assert.Equal(1, rig.Guard.DisarmCount);
        Assert.Equal(1, run.DisposeCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.Contains(rig.Updates, update => update.Message == ControllerMessage.Cancelled);
        Assert.DoesNotContain(rig.Updates, update => update.Message == ControllerMessage.DictationFailed);
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
    }

    // ---- X09 ----------------------------------------------------------------------------------------------------

    // SI-04: Wächter meldet StopRequired während der Aufnahme: Ende wie beim zweiten Tastendruck, mit Grund.
    [Theory]
    [InlineData(GuardReason.PasswordField)]
    [InlineData(GuardReason.SessionLocked)]
    [InlineData(GuardReason.SessionDisconnected)]
    [InlineData(GuardReason.SecureDesktop)]
    public async Task X09_GuardStopRequiredEndsLikeSecondPressWithReason(GuardReason reason)
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var session = rig.Audio.Session!;
        run.Emit(Output(Fixed(FirstText)));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "erster Schreibaufruf");

        rig.Guard.Raise(reason);
        await rig.WaitForPhase(ControllerPhase.Stopping);
        run.Emit(Output(Fixed(LateText)));
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        AssertEndsLikeSecondPress(rig, run, session, LateText, expectStop: true);
        Assert.Contains(rig.Updates, update =>
            update.Message == ControllerMessage.GuardStopped && update.Reason == reason.ToString());
    }

    // ---- X10 ----------------------------------------------------------------------------------------------------

    // ER-05, R51: Endet RunAsync mit Fehler, ruft die Steuerung sofort Stop und DisposeAsync; schon fester Text bleibt.
    [Theory]
    [InlineData("decoder")]
    [InlineData("unavailable")]
    [InlineData("other")]
    public async Task X10_RunFailureStopsAndReleasesImmediatelyAndReportsFailure(string kind)
    {
        Exception error = kind switch
        {
            "decoder" => new RecognizerException(RecognizerErrorCode.DecoderError),
            "unavailable" => new RecognizerUnavailableException(),
            _ => new InvalidOperationException("erfundener Fehler"),
        };
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var session = rig.Audio.Session!;
        var first = Fixed(FirstText);
        run.Emit(Output(first));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "erster Schreibaufruf");

        run.Fail(error);
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Equal(1, session.StopCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(1, run.DisposeCount);
        Assert.Equal(1, rig.Guard.DisarmCount);
        Assert.True(rig.Log.IndexOf("Capture.Stop") < rig.Log.IndexOf("Capture.Dispose"), "Stop kommt vor der Freigabe der Aufnahme");
        Assert.Contains(rig.Updates, update => update.Message == ControllerMessage.DictationFailed);
        Assert.Equal([FirstText], rig.Output.Texts);
        Assert.Equal(
            new OutputEntry(first.Id, EntryStatus.Written, OutputReason.None, FirstText, ""),
            Assert.Single(rig.Controller.Current.Entries));
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
    }

    // ---- X11 ----------------------------------------------------------------------------------------------------

    public static TheoryData<CaptureStopReason> AbnormalStopReasons() =>
        [.. Enum.GetValues<CaptureStopReason>().Where(reason => reason != CaptureStopReason.Requested)];

    // ER-13: Die Aufnahme endet von sich aus: Ende mit Grund, schon Aufgenommenes wird noch geschrieben.
    [Theory]
    [MemberData(nameof(AbnormalStopReasons))]
    public async Task X11_CaptureStoppedByItselfEndsTheDictationWithReason(CaptureStopReason reason)
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var session = rig.Audio.Session!;
        run.Emit(Output(Fixed(FirstText)));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "erster Schreibaufruf");

        session.Fail(reason);
        run.Emit(Output(Fixed(LateText)));
        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        AssertEndsLikeSecondPress(rig, run, session, LateText, expectStop: false);
        Assert.Contains(rig.Updates, update =>
            update.Message == ControllerMessage.CaptureStopped && update.Reason == reason.ToString());
    }

    // ER-13: Mikrofon öffnen scheitert: Meldung, Wächter aus, Erkennersitzung frei, kein Lesen.
    [Theory]
    [InlineData("invalid")]
    [InlineData("other")]
    public async Task X11_OpenFailureReleasesTheSessionAndTheGuard(string kind)
    {
        Exception failure = kind == "invalid"
            ? new InvalidOperationException("erfundener Fehler: frühere Sitzung nicht freigegeben")
            : new IOException("erfundener Fehler: Gerät nicht lesbar");
        await using var rig = new ControllerRig(arrange: r => r.Audio.OpenFailure = failure);

        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Contains(rig.Updates, update => update.Message == ControllerMessage.CaptureFailed);
        Assert.Equal(1, rig.Guard.DisarmCount);
        var run = Assert.Single(rig.Factory.Runs);
        Assert.Equal(1, run.DisposeCount);
        Assert.False(rig.Log.Contains("Capture.ReadAsync"), "Ohne geöffnete Aufnahme wird nicht gelesen");
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
    }

    // ER-13: Kein passendes Gerät: Meldung, kein Mikrofon, Wächter aus, eine gestartete Sitzung frei.
    [Theory]
    [InlineData("mikro-fehlt", false)]
    [InlineData(null, true)]
    public async Task X11_NoMatchingDeviceReportsNoDevice(string? deviceId, bool defaultMissing)
    {
        await using var rig = new ControllerRig(
            ControllerRig.Options(ProfileStrictness.Standard, deviceId),
            r => r.Audio.DefaultDevice = defaultMissing ? null : FakeAudioCapture.MicA);

        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);

        Assert.Contains(rig.Updates, update => update.Message == ControllerMessage.NoDevice);
        Assert.Equal(0, rig.Log.CountStarting("Capture.Open:"));
        Assert.Equal(rig.Guard.ArmCount, rig.Guard.DisarmCount);
        Assert.All(rig.Factory.Runs, run => Assert.Equal(1, run.DisposeCount));
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
    }

    // ER-13: Die Aufnahmetaste während des Startens startet kein zweites Diktat und stoppt das erste nicht.
    [Fact]
    public async Task X11_PressWhileStartingStartsNoSecondDictation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new ControllerRig(arrange: r => r.Factory.Gate = gate);

        var firstPress = rig.Controller.RecordAsync();
        await rig.WaitUntil(() => rig.Factory.StartCount == 1, "Start der Erkennersitzung");
        await rig.WaitForPhase(ControllerPhase.Starting);
        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        Assert.Equal(1, rig.Factory.StartCount);

        gate.SetResult();
        await firstPress.WaitAsync(ControllerRig.Timeout);
        var run = await rig.StartAsync();
        rig.Audio.Session!.SetRecording();
        await rig.WaitForPhase(ControllerPhase.Recording);
        await rig.EndAsync(run);

        Assert.Equal(1, rig.Factory.StartCount);
        Assert.Single(rig.Audio.Sessions);
        Assert.Equal(1, rig.Audio.Sessions[0].StopCount);
        Assert.Equal(1, rig.Guard.ArmCount);
    }

    // ---- X12 ----------------------------------------------------------------------------------------------------

    // BE-19: Die Befehlstaste ruft PressCommandKey nur in der Phase Recording.
    [Fact]
    public async Task X12_CommandKeyReachesTheRunOnlyWhileRecording()
    {
        await using var rig = new ControllerRig();

        rig.Controller.CommandKey();
        Assert.Equal(0, rig.Factory.StartCount);

        var run = await rig.StartAsync();
        await rig.WaitForPhase(ControllerPhase.Starting);
        rig.Controller.CommandKey();
        Assert.Equal(0, run.CommandKeyCount);

        rig.Audio.Session!.SetRecording();
        await rig.WaitForPhase(ControllerPhase.Recording);
        rig.Controller.CommandKey();
        Assert.Equal(1, run.CommandKeyCount);
        rig.Controller.CommandKey();
        Assert.Equal(2, run.CommandKeyCount);

        await rig.Controller.RecordAsync().WaitAsync(ControllerRig.Timeout);
        await rig.WaitForPhase(ControllerPhase.Stopping);
        rig.Controller.CommandKey();
        Assert.Equal(2, run.CommandKeyCount);

        run.Complete();
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        rig.Controller.CommandKey();
        Assert.Equal(2, run.CommandKeyCount);
    }

    // ---- X13 ----------------------------------------------------------------------------------------------------

    private static readonly RecognizerIdentity PrivateIdentity = new("ggml-large-v3-turbo.bin", KnownRuntimes.Cuda);

    private static LiveApprovals PrivateApproval() =>
        new([new LiveApproval(KnownDevices.ReferenceDesktop, "de-CH", "ggml-large-v3-turbo.bin", KnownRuntimes.Cuda)]);

    private static LiveOptions BaseLive(LiveMode mode) => new(
        mode,
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMilliseconds(600),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(120),
        new HallucinationFilter(["Vielen Dank fürs Zuschauen"], 0.5f));

    // ER-19, ER-20: Mit der Identität der Erkennersitzung und einer Abnahme, die genau sie abdeckt, gilt live.
    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public void X13_ApprovedIdentityGivesLiveAndSetsTheLiveOptions(LiveMode inputMode)
    {
        var live = BaseLive(inputMode);

        var (mode, options) = RecognizerDictationFactory.Options(
            BuiltInProfiles.Standard, KnownDevices.ReferenceDesktop, PrivateIdentity, PrivateApproval(), live);

        Assert.Equal(new ModeDecision(LiveMode.Live, RecognitionMode.Live, ModeReason.ProfileChoice), mode);
        Assert.Equal(live with { Mode = LiveMode.Live }, options);
    }

    // ER-19: Ohne Abnahme satzweise, mit Grund NotApproved; die P3-Optionen tragen denselben Modus.
    [Theory]
    [InlineData(LiveMode.Live)]
    [InlineData(LiveMode.Sentence)]
    public void X13_WithoutApprovalSentenceWithReasonNotApproved(LiveMode inputMode)
    {
        var live = BaseLive(inputMode);

        var (mode, options) = RecognizerDictationFactory.Options(
            BuiltInProfiles.Standard, KnownDevices.ReferenceDesktop, PrivateIdentity, LiveApprovals.None, live);

        Assert.Equal(new ModeDecision(LiveMode.Sentence, RecognitionMode.Live, ModeReason.NotApproved), mode);
        Assert.True(mode.OverridesProfile, "Die Anzeige muss sagen, dass das Profil live wollte");
        Assert.Equal(live with { Mode = LiveMode.Sentence }, options);
    }

    // P8 Kapitel 5.9: Die Identität des tatsächlich geladenen Erkenners entscheidet, nicht die gewünschte.
    [Theory]
    [InlineData("ggml-large-v3-turbo.bin", KnownRuntimes.Cpu)]
    [InlineData("ggml-base.bin", KnownRuntimes.Cuda)]
    public void X13_OtherIdentityThanApprovedIsNotApproved(string model, string runtime)
    {
        var (mode, options) = RecognizerDictationFactory.Options(
            BuiltInProfiles.Standard, KnownDevices.ReferenceDesktop, new RecognizerIdentity(model, runtime), PrivateApproval(), BaseLive(LiveMode.Live));

        Assert.Equal(new ModeDecision(LiveMode.Sentence, RecognitionMode.Live, ModeReason.NotApproved), mode);
        Assert.Equal(LiveMode.Sentence, options.Mode);
    }

    // Ein Profil mit Modus „satzweise“ bleibt satzweise (ProfileChoice), auch bei vorhandener Abnahme.
    [Fact]
    public void X13_SentenceProfileStaysSentenceWithProfileChoice()
    {
        var profile = BuiltInProfiles.Standard with { RecognitionMode = RecognitionMode.Sentence };

        var (mode, options) = RecognizerDictationFactory.Options(
            profile, KnownDevices.ReferenceDesktop, PrivateIdentity, PrivateApproval(), BaseLive(LiveMode.Live));

        Assert.Equal(new ModeDecision(LiveMode.Sentence, RecognitionMode.Sentence, ModeReason.ProfileChoice), mode);
        Assert.Equal(LiveMode.Sentence, options.Mode);
    }

    // Fail-safe: Unbekannter Erkenner oder unbekanntes Gerät führt zu satzweise.
    [Fact]
    public void X13_UnknownRecognizerOrDeviceIsSentence()
    {
        var (unknownRecognizer, recognizerOptions) = RecognizerDictationFactory.Options(
            BuiltInProfiles.Standard, KnownDevices.ReferenceDesktop, null, PrivateApproval(), BaseLive(LiveMode.Live));
        var (unknownDevice, deviceOptions) = RecognizerDictationFactory.Options(
            BuiltInProfiles.Standard, null, PrivateIdentity, PrivateApproval(), BaseLive(LiveMode.Live));

        Assert.Equal(new ModeDecision(LiveMode.Sentence, RecognitionMode.Live, ModeReason.RecognizerUnknown), unknownRecognizer);
        Assert.Equal(LiveMode.Sentence, recognizerOptions.Mode);
        Assert.Equal(new ModeDecision(LiveMode.Sentence, RecognitionMode.Live, ModeReason.DeviceUnknown), unknownDevice);
        Assert.Equal(LiveMode.Sentence, deviceOptions.Mode);
    }

    // ER-19: Die Anzeige zeigt den Modus des Laufs samt Grund (satzweise ohne Abnahme).
    [Theory]
    [InlineData(LiveMode.Sentence, ModeReason.NotApproved)]
    [InlineData(LiveMode.Live, ModeReason.ProfileChoice)]
    public async Task X13_UpdateShowsTheModeOfTheRun(LiveMode liveMode, ModeReason reason)
    {
        var decision = new ModeDecision(liveMode, RecognitionMode.Live, reason);
        await using var rig = new ControllerRig(arrange: r => r.Factory.Mode = decision);

        await rig.StartRecordingAsync();

        Assert.Equal(decision, rig.Controller.Current.Mode);
    }

    // ---- X14 ----------------------------------------------------------------------------------------------------

    private const string TextMarker = "GEHEIM-4711";
    private const string TargetMarker = "GEHEIM-4712";

    // SI-01: Weder die Protokollzeilen der Steuerung noch ControllerUpdate.LogLine() tragen Text oder Zielanzeige.
    [Fact]
    public async Task X14_NoTextInLogLines()
    {
        await using var rig = new ControllerRig(arrange: r =>
            r.Binding.Next = new BindResult(FakeBinding.NewTarget(TargetMarker), null));
        var run = await rig.StartRecordingAsync();
        var written = Fixed($"{TextMarker} fest geschrieben");
        var held = Fixed($"{TextMarker} gehalten");
        rig.Output.SetReport(held.Id, new OutputReport(OutputResult.HeldInDictationWindow, "", held.Text, OutputReason.FocusElsewhere));

        run.Emit(new TextOutput(new RecognitionUpdate([], [Provisional($"{TextMarker} vorläufig")])));
        await rig.WaitUntil(() => rig.Updates.Any(update => update.Provisional.Contains(TextMarker, StringComparison.Ordinal)), "vorläufiger Text in der Anzeige");
        run.Emit(Output(written));
        run.Emit(Output(held));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 2, "beide Schreibaufrufe");
        await rig.EndAsync(run);

        // Die Anzeige trägt die Marker wirklich, sonst wäre die Prüfung leer
        var updates = rig.Updates;
        Assert.Contains(updates, update => update.Provisional.Contains(TextMarker, StringComparison.Ordinal));
        Assert.Contains(updates, update => update.Target == TargetMarker);
        Assert.Contains(updates, update => update.Entries.Any(entry => entry.Written.Contains(TextMarker, StringComparison.Ordinal)));
        Assert.Contains(updates, update => update.Entries.Any(entry => entry.Held.Contains(TextMarker, StringComparison.Ordinal)));

        foreach (var line in rig.LogLines)
        {
            AssertNoMarker(line, "Protokollzeile der Steuerung");
        }

        foreach (var update in updates)
        {
            AssertNoMarker(update.LogLine(), "ControllerUpdate.LogLine()");
        }
    }

    // SI-01: LogLine allein, ohne Steuerung, trägt weder Vorschau, Einträge noch Zielanzeige.
    [Fact]
    public void X14_LogLineCarriesNoTextOrTargetDisplay()
    {
        var update = new ControllerUpdate(
            ControllerPhase.Recording,
            RecognizerAvailability.Ready,
            TargetMarker,
            new ModeDecision(LiveMode.Sentence, RecognitionMode.Live, ModeReason.NotApproved),
            $"{TextMarker} vorläufig",
            [
                new OutputEntry(Guid.NewGuid(), EntryStatus.Written, OutputReason.None, $"{TextMarker} geschrieben", ""),
                new OutputEntry(Guid.NewGuid(), EntryStatus.Held, OutputReason.FocusElsewhere, "", $"{TextMarker} gehalten"),
            ],
            ControllerMessage.BindRefused,
            nameof(BindRefusal.PasswordField));

        AssertNoMarker(update.LogLine(), "ControllerUpdate.LogLine()");
    }

    // ---- X15 ----------------------------------------------------------------------------------------------------

    // AU-03, D4 b: Im Profil „Standard“ geht auf ausdrücklichen Befehl genau der gehaltene Text in die Zwischenablage.
    [Theory]
    [InlineData(OutputResult.HeldInDictationWindow, OutputReason.FocusElsewhere, "")]
    [InlineData(OutputResult.HeldInDictationWindow, OutputReason.FocusElsewhere, "Der Anfang ")]
    [InlineData(OutputResult.Refused, OutputReason.LineBreakRefused, "")]
    public async Task X15_CopyHeldPutsExactlyTheHeldTextOnTheClipboardInStandard(
        OutputResult result, OutputReason reason, string alreadyWritten)
    {
        const string held = "der gehaltene Rest";
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var segment = Fixed($"{alreadyWritten}{held}");
        rig.Output.SetReport(segment.Id, new OutputReport(result, alreadyWritten, held, reason));
        run.Emit(Output(segment));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "Schreibaufruf");
        await rig.EndAsync(run);

        var copied = rig.Controller.CopyHeld(segment.Id);

        Assert.True(copied, "CopyHeld muss für einen gehaltenen Abschnitt wahr liefern");
        Assert.Equal([held], rig.Clipboard.Texts);
    }

    // AU-03: Geschriebene Abschnitte und unbekannte Kennungen kommen nie in die Zwischenablage.
    [Fact]
    public async Task X15_CopyHeldRefusesWrittenAndUnknownSegments()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var written = Fixed(FirstText);
        run.Emit(Output(written));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "Schreibaufruf");
        await rig.EndAsync(run);

        Assert.False(rig.Controller.CopyHeld(written.Id), "Ein geschriebener Abschnitt wird nicht kopiert");
        Assert.False(rig.Controller.CopyHeld(Guid.NewGuid()), "Eine unbekannte Kennung wird nicht kopiert");
        Assert.Empty(rig.Clipboard.Texts);
    }

    // AU-03: Im Profil „Streng“ keine Zwischenablage, auch nicht für gehaltenen Text; Meldung ClipboardRefused.
    [Fact]
    public async Task X15_CopyHeldIsRefusedInStrictWithoutClipboardAccess()
    {
        await using var rig = new ControllerRig(ControllerRig.Options(ProfileStrictness.Strict));
        var run = await rig.StartRecordingAsync();
        var segment = Fixed(FirstText);
        rig.Output.SetReport(segment.Id, new OutputReport(OutputResult.HeldInDictationWindow, "", FirstText, OutputReason.FocusElsewhere));
        run.Emit(Output(segment));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 1, "Schreibaufruf");
        await rig.EndAsync(run);

        var copied = rig.Controller.CopyHeld(segment.Id);

        Assert.False(copied, "Im Profil „Streng“ wird nichts kopiert");
        Assert.Empty(rig.Clipboard.Texts);
        Assert.Contains(rig.Updates, update => update.Message == ControllerMessage.ClipboardRefused);
    }

    // ---- X16 ----------------------------------------------------------------------------------------------------

    // BF-02: Gültige Belegung: alle Tasten mit ihren Gesten angemeldet, ohne Konflikt; jede Taste wirkt. Seit P9
    // Diktatfenster sind es vier (Strg+Alt+D), seit Beta B9 sechs (Strg+Alt+F, Strg+Alt+G), seit B5 sieben (Strg+Alt+B).
    [Fact]
    public async Task X16_RegistersAllActionsFromTheOptionsAndTheKeysWork()
    {
        await using var rig = new ControllerRig();

        var result = rig.Controller.RegisterHotkeys();

        Assert.Equal(7, result.Count);
        Assert.All(Enum.GetValues<HotkeyAction>(), action => Assert.Equal(HotkeyConflict.None, result[action]));
        var registered = rig.Hotkeys.Registered;
        Assert.Equal(7, registered.Count);
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var entry = Assert.Single(registered, item => item.Action == action);
            Assert.Equal(ControllerRig.DefaultHotkeys[action], entry.Gesture);
            Assert.False(entry.TrackRelease, "Halten bleibt aus (BE-03): Ein/Aus ohne Loslassen");
        }

        Assert.DoesNotContain(rig.Updates, update => update.Message == ControllerMessage.HotkeyConflict);

        rig.Hotkeys.Press(HotkeyAction.Record);
        await rig.WaitUntilOpenAsync(1);
        var run = rig.Factory.LastRun!;
        rig.Audio.Session!.SetRecording();
        await rig.WaitForPhase(ControllerPhase.Recording);
        rig.Hotkeys.Press(HotkeyAction.Command);
        await rig.WaitUntil(() => run.CommandKeyCount == 1, "Befehlstaste am Lauf");
        rig.Hotkeys.Press(HotkeyAction.Cancel);
        await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
        Assert.Contains(rig.Updates, update => update.Message == ControllerMessage.Cancelled);
    }

    // BF-02, D2: Ein Konflikt wird mit Grund gemeldet und zurückgegeben; die übrigen Tasten wirken.
    [Theory]
    [InlineData(HotkeyAction.Record, HotkeyConflict.AltGrCharacter)]
    [InlineData(HotkeyAction.Command, HotkeyConflict.TakenBySystemOrOtherApp)]
    [InlineData(HotkeyAction.Cancel, HotkeyConflict.Reserved)]
    public async Task X16_ConflictIsReportedWithReasonAndTheOtherKeysStillWork(HotkeyAction conflicted, HotkeyConflict conflict)
    {
        await using var rig = new ControllerRig(arrange: r => r.Hotkeys.Conflicts[conflicted] = conflict);

        var result = rig.Controller.RegisterHotkeys();

        Assert.Equal(7, result.Count);
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            Assert.Equal(action == conflicted ? conflict : HotkeyConflict.None, result[action]);
        }

        Assert.Contains(rig.Updates, update =>
            update.Message == ControllerMessage.HotkeyConflict && update.Reason == conflict.ToString());

        FakeDictationRun run;
        if (conflicted == HotkeyAction.Record)
        {
            run = await rig.StartRecordingAsync();
        }
        else
        {
            rig.Hotkeys.Press(HotkeyAction.Record);
            await rig.WaitUntilOpenAsync(1);
            run = rig.Factory.LastRun!;
            rig.Audio.Session!.SetRecording();
            await rig.WaitForPhase(ControllerPhase.Recording);
        }

        if (conflicted != HotkeyAction.Command)
        {
            rig.Hotkeys.Press(HotkeyAction.Command);
            await rig.WaitUntil(() => run.CommandKeyCount == 1, "Befehlstaste am Lauf");
        }

        if (conflicted != HotkeyAction.Cancel)
        {
            rig.Hotkeys.Press(HotkeyAction.Cancel);
            await rig.Controller.Completion.WaitAsync(ControllerRig.Timeout);
            Assert.Contains(rig.Updates, update => update.Message == ControllerMessage.Cancelled);
        }
        else
        {
            await rig.EndAsync(run);
        }

        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
    }

    // BF-02: Mehrere Konflikte: jeder wird mit seinem Grund gemeldet, die App läuft weiter.
    [Fact]
    public async Task X16_SeveralConflictsAreAllReported()
    {
        await using var rig = new ControllerRig(arrange: r =>
        {
            r.Hotkeys.Conflicts[HotkeyAction.Record] = HotkeyConflict.TypingKey;
            r.Hotkeys.Conflicts[HotkeyAction.Cancel] = HotkeyConflict.OwnDuplicate;
        });

        var result = rig.Controller.RegisterHotkeys();

        Assert.Equal(HotkeyConflict.TypingKey, result[HotkeyAction.Record]);
        Assert.Equal(HotkeyConflict.None, result[HotkeyAction.Command]);
        Assert.Equal(HotkeyConflict.OwnDuplicate, result[HotkeyAction.Cancel]);
        var reasons = rig.Updates.Where(update => update.Message == ControllerMessage.HotkeyConflict).Select(update => update.Reason).ToList();
        Assert.Contains(nameof(HotkeyConflict.TypingKey), reasons);
        Assert.Contains(nameof(HotkeyConflict.OwnDuplicate), reasons);
        Assert.DoesNotContain(nameof(HotkeyConflict.None), reasons);
    }

    // ---- Hilfen -------------------------------------------------------------------------------------------------

    // T78: Jede Stelle bleibt in der Liste sichtbar; ein späterer Hinweis überschreibt den früheren nicht.
    [Fact]
    public async Task T78_EachLengthCutMarksItsOwnRow()
    {
        await using var rig = new ControllerRig();
        var run = await rig.StartRecordingAsync();
        var first = new TextSegment(Guid.NewGuid(), FirstText, SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(9));
        var second = new TextSegment(Guid.NewGuid(), SecondText, SegmentState.Fixed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(18));
        var last = new TextSegment(Guid.NewGuid(), LateText, SegmentState.Fixed, TimeSpan.FromSeconds(18), TimeSpan.FromSeconds(20));

        run.Emit(new HintOutput(null, new LiveNotice(LiveNoticeKind.LengthCutUncertain, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10))));
        run.Emit(Output(first));
        run.Emit(new HintOutput(null, new LiveNotice(LiveNoticeKind.LengthCutUncertain, TimeSpan.FromSeconds(18), TimeSpan.FromSeconds(19))));
        run.Emit(Output(second));
        run.Emit(Output(last));
        await rig.WaitUntil(() => rig.Output.Calls.Count == 3, "drei Schreibaufrufe");
        await rig.EndAsync(run);

        Assert.Equal([true, true, false], rig.Controller.Current.Entries.Select(entry => entry.CheckEnd));
    }

    private static TextSegment Fixed(string text) =>
        new(Guid.NewGuid(), text, SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1));

    private static TextSegment Provisional(string text) =>
        new(Guid.NewGuid(), text, SegmentState.Provisional, TimeSpan.Zero, TimeSpan.FromSeconds(1));

    private static TextOutput Output(params TextSegment[] fixedSegments) => new(new RecognitionUpdate(fixedSegments, []));

    private static void AssertNoMarker(string line, string source)
    {
        Assert.DoesNotContain(TextMarker, line, StringComparison.Ordinal);
        Assert.DoesNotContain(TargetMarker, line, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(line), $"{source}: eine leere Zeile ist kein Protokoll");
    }

    /// <summary>
    /// Ende wie beim zweiten Tastendruck (X07): Stopp, die späten festen Abschnitte werden geschrieben, danach Wächter aus
    /// und Freigabe von Sitzung und Aufnahme; Phase Idle, Completion erledigt.
    /// </summary>
    private static void AssertEndsLikeSecondPress(
        ControllerRig rig, FakeDictationRun run, FakeCaptureSession session, string lateText, bool expectStop)
    {
        var log = rig.Log;
        var stop = log.IndexOf("Capture.Stop");
        var lateStart = log.IndexOf($"Output.Write:{lateText}");
        var lateDone = log.IndexOf($"Output.Done:{lateText}");
        var disarm = log.IndexOf("Guard.Disarm");
        var runDispose = log.IndexOf("Run.Dispose");
        var captureDispose = log.IndexOf("Capture.Dispose");

        if (expectStop)
        {
            Assert.Equal(1, session.StopCount);
            Assert.True(stop >= 0 && stop < lateStart, "Der Stopp kommt vor dem Schreiben des späten Abschnitts");
        }

        Assert.True(lateStart >= 0, "Der nach dem Stopp gelieferte feste Abschnitt muss geschrieben werden");
        Assert.True(lateDone > lateStart, "Das Schreiben ist beendet");
        Assert.Equal(1, rig.Guard.DisarmCount);
        Assert.True(disarm > lateDone, "Der Wächter wird erst nach dem letzten Schreiben ausgeschaltet");
        Assert.Equal(1, run.DisposeCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.True(runDispose > disarm, "Die Erkennersitzung wird nach dem Wächter freigegeben");
        Assert.True(captureDispose > disarm, "Die Aufnahme wird nach dem Wächter freigegeben");
        Assert.Contains(rig.Controller.Current.Entries, entry => entry.Written == lateText && entry.Status == EntryStatus.Written);
        Assert.Equal(ControllerPhase.Idle, rig.Controller.Current.Phase);
        Assert.True(rig.Controller.Completion.IsCompleted, "Completion ist nach der Freigabe erledigt");
    }
}

/// <summary>Zusammenbau der Steuerung mit allen Attrappen und Warte-Hilfen mit Zeitgrenze.</summary>
internal sealed class ControllerRig : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Die Belegung des Konzepts (D2 a): Strg+Alt+Y, Strg+Alt+X, Strg+Alt+Q; dazu Strg+Alt+D (P9 Diktatfenster) sowie
    /// Strg+Alt+F und Strg+Alt+G (Beta B9) und Strg+Alt+B (Beta B5).
    /// </summary>
    public static readonly IReadOnlyDictionary<HotkeyAction, HotkeyGesture> DefaultHotkeys = new Dictionary<HotkeyAction, HotkeyGesture>
    {
        [HotkeyAction.Record] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x59),
        [HotkeyAction.Command] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x58),
        [HotkeyAction.Cancel] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x51),
        [HotkeyAction.DictationWindow] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x44),
        [HotkeyAction.Correct] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x46),
        [HotkeyAction.Smooth] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x47),
        [HotkeyAction.Mark] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x42),
    };

    private readonly Lock _gate = new();
    private readonly List<ControllerUpdate> _updates = [];
    private readonly List<string> _logLines = [];
    private int _started;

    public ControllerRig(ControllerOptions? options = null, Action<ControllerRig>? arrange = null)
    {
        Log = new EventLog();
        Host = new FakeRecognizerHost();
        Factory = new FakeDictationFactory(Log);
        Audio = new FakeAudioCapture(Log);
        Binding = new FakeBinding(Log);
        Output = new FakeTextOutput(Log);
        Guard = new FakeGuard(Log);
        Hotkeys = new FakeHotkeys(Log);
        Clipboard = new FakeClipboard(Log);
        Foreign = new FakeForeignContent(Log);
        arrange?.Invoke(this);

        Controller = new DictationController(
            Host, Factory, Audio, Binding, Output, Guard, Hotkeys, Clipboard, options ?? Options(ProfileStrictness.Standard), line =>
            {
                lock (_gate)
                {
                    _logLines.Add(line);
                }
            },
            Recovery, Pad, Binding, Foreign, Review);
        Controller.Updated += (_, update) =>
        {
            lock (_gate)
            {
                _updates.Add(update);
            }
        };
    }

    public EventLog Log { get; }

    public FakeRecognizerHost Host { get; }

    public FakeDictationFactory Factory { get; }

    public FakeAudioCapture Audio { get; }

    public FakeBinding Binding { get; }

    public FakeTextOutput Output { get; }

    public FakeGuard Guard { get; }

    public FakeHotkeys Hotkeys { get; }

    public FakeClipboard Clipboard { get; }

    /// <summary>Markierung im fremden Feld (Beta B9).</summary>
    public FakeForeignContent Foreign { get; }

    /// <summary>Verlustschutz (P9 Diktatfenster); in <c>arrange</c> zu setzen, sonst ohne Sicherung.</summary>
    public Freihand.Core.Recovery.RecoveryStore? Recovery { get; set; }

    /// <summary>Prüfaufnahmen (P11-E1); in <c>arrange</c> zu setzen, sonst ohne.</summary>
    public IReviewRecorder? Review { get; set; }

    /// <summary>Schreibfläche des Diktatfensters; in <c>arrange</c> zu setzen, sonst ohne Diktatfenster.</summary>
    public FakePad? Pad { get; set; }

    public DictationController Controller { get; }

    /// <summary>Alle Updates, die die Steuerung gemeldet hat, in der Reihenfolge.</summary>
    public IReadOnlyList<ControllerUpdate> Updates
    {
        get
        {
            lock (_gate)
            {
                return [.. _updates];
            }
        }
    }

    /// <summary>Alle Zeilen, die die Steuerung an den log-Delegaten gab.</summary>
    public IReadOnlyList<string> LogLines
    {
        get
        {
            lock (_gate)
            {
                return [.. _logLines];
            }
        }
    }

    public static ControllerOptions Options(ProfileStrictness strictness, string? deviceId = null) =>
        new(strictness, deviceId, DefaultHotkeys);

    /// <summary>Wartet mit Zeitgrenze auf einen beobachtbaren Zustand (Polling, kein fester Schlaf als Beweis).</summary>
    public async Task WaitUntil(Func<bool> condition, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Timeout)
            {
                throw new Xunit.Sdk.XunitException($"Zeitgrenze überschritten beim Warten auf: {what}");
            }

            await Task.Delay(5);
        }
    }

    public Task WaitForPhase(ControllerPhase phase) =>
        WaitUntil(() => Controller.Current.Phase == phase, $"Phase {phase}");

    /// <summary>Wartet, bis das n-te Diktat Erkennersitzung, Mikrofon und Lesen erreicht hat.</summary>
    public Task WaitUntilOpenAsync(int dictations) =>
        WaitUntil(
            () => Factory.Runs.Count >= dictations
                && Audio.Sessions.Count >= dictations
                && Log.CountStarting("Capture.ReadAsync") >= dictations,
            $"Diktat {dictations}: Erkennersitzung, Mikrofon offen, Lesen");

    /// <summary>Aufnahmetaste im Leerlauf; kehrt zurück, wenn Sitzung und Mikrofon offen sind (Phase Starting).</summary>
    public async Task<FakeDictationRun> StartAsync()
    {
        var number = Interlocked.Increment(ref _started);
        await Controller.RecordAsync().WaitAsync(Timeout);
        await WaitUntilOpenAsync(number);
        return Factory.Runs[number - 1];
    }

    /// <summary>Wie <see cref="StartAsync"/>, dann meldet die Aufnahme Recording.</summary>
    public async Task<FakeDictationRun> StartRecordingAsync()
    {
        var run = await StartAsync();
        Audio.Sessions[Factory.Runs.ToList().IndexOf(run)].SetRecording();
        await WaitForPhase(ControllerPhase.Recording);
        return run;
    }

    /// <summary>Zweiter Tastendruck, der Lauf endet, die Steuerung gibt alles frei.</summary>
    public async Task EndAsync(FakeDictationRun run)
    {
        await Controller.RecordAsync().WaitAsync(Timeout);
        run.Complete();
        await Controller.Completion.WaitAsync(Timeout);
    }

    public async ValueTask DisposeAsync()
    {
        Output.ReleaseHold();
        foreach (var run in Factory.Runs)
        {
            run.Complete();
        }

        await Controller.DisposeAsync().AsTask().WaitAsync(Timeout);
    }
}
