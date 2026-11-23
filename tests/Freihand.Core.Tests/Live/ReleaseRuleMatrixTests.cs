using System.Runtime.CompilerServices;
using Freihand.Core.Commands;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Routing;
using Freihand.Core.Segments;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>
/// T70: Freigaberegel aus Kapitel 5.4 als Matrix mit ausdrücklichem Abdeckungsinventar (Codex-Nachprüfung 5, Y03).
/// <para>Besitzer einer fremden Folge: Audio live, Audio der Engine, Folge der Engine, Quelle der Sitzung.</para>
/// <para>Regel: Reguläres Ende (Streamende, Aufnahmestopp, Ende der Engine) wartet auf die Freigabe; ihr Fehler ist der
/// Fehler des Laufs und beendet auch eine noch laufende andere Freigabe oder Arbeit. Fehler, Abbruch, Abbruchbefehl oder
/// vorzeitiges Ende des Lesers warten nicht; die Freigabe geschieht genau einmal im Hintergrund, ihr Fehler wird
/// beobachtet und ist nie Fehler des Laufs. Scheitert schon das Öffnen, ist nichts erworben und nichts freizugeben.</para>
/// <para>Jeder Fall prüft die genaue Ursache, die Zahl der Freigaben und dass kein Fehler unbeobachtet bleibt
/// (<see cref="UnobservedFailureLog"/>, vor dem ersten Test angemeldet). Fremde Ursachen werden an einer Kennung in ihrer
/// Meldung erkannt; Ursachen, die P3 selbst erzeugt (Format, Detektor-Zeitüberschreitung, Vertragsverstoss der Engine),
/// an ihrem Typ, beim Vertragsverstoss zusätzlich am Fehlen jeder fremden Kennung.</para>
/// Nicht anwendbar und darum nicht enthalten: Aufnahmestopp und Ende der Engine bei <c>LiveTranscription</c> (dort gibt
/// es weder Engine noch Aufnahmestopp, T31); Freigabefehler bei gescheitertem Öffnen (keine Freigabe).
/// </summary>
public sealed class ReleaseRuleMatrixTests
{
    // ---- Audio live und Audio der Engine ------------------------------------------------------------------------

    public enum Owner { LiveAudio, EngineAudio }

    public enum AudioEnd
    {
        /// <summary>Streamende; danach endet die Erkennung erst nach der Freigabe.</summary>
        Eof,

        /// <summary>Aufnahmestopp bei vollem Puffer (nur Engine).</summary>
        BufferFull,

        /// <summary>Streamende; die Engine endet regulär, während die Audiofreigabe läuft (W01, nur Engine).</summary>
        EofEngineEndsDuringRelease,

        /// <summary>Aufnahmestopp; die Engine endet, während die Audiofreigabe läuft (W01, nur Engine).</summary>
        BufferFullEngineEndsDuringRelease,

        /// <summary>Streamende; die Freigabe scheitert, während die Erkennung noch arbeitet (W02).</summary>
        EofReleaseFailsWhileRecognitionBusy,

        /// <summary>Das Öffnen der Audiofolge wirft; nichts ist erworben.</summary>
        OpenThrows,

        /// <summary>Lesen wirft.</summary>
        ReadThrows,

        /// <summary><c>Current</c> wirft.</summary>
        CurrentThrows,

        /// <summary>Ein Block mit falscher Abtastrate (Vertrag 1).</summary>
        BadFormat,

        /// <summary>Abbruch des Aufrufers, während ein Lesen hängt, das den Abbruch übergeht.</summary>
        CancelWhileReading,

        /// <summary>Abbruch des Aufrufers während der regulären Freigabe.</summary>
        CancelWhileReleasing,

        /// <summary>Die Erkennung scheitert, während ein Lesen hängt.</summary>
        RecognitionFailsWhileReading,

        /// <summary>Die Sprachbereichserkennung scheitert synchron in der Annahme (S03, T68).</summary>
        DetectorFailsInAcceptance,

        /// <summary>Die Engine endet regulär, während ein Lesen hängt (nur Engine).</summary>
        EngineEndsWhileReading,

        /// <summary>Der äussere Leser hört nach einem Ereignis auf, während ein Lesen hängt.</summary>
        ReaderStopsEarly,
    }

    private static bool Regular(AudioEnd end) => end is AudioEnd.Eof or AudioEnd.BufferFull
        or AudioEnd.EofEngineEndsDuringRelease or AudioEnd.BufferFullEngineEndsDuringRelease;

    public static TheoryData<Owner, AudioEnd, bool> AudioCases()
    {
        var cases = new TheoryData<Owner, AudioEnd, bool>();
        foreach (var end in Enum.GetValues<AudioEnd>())
        {
            foreach (var owner in Enum.GetValues<Owner>())
            {
                var engineOnly = end is AudioEnd.BufferFull or AudioEnd.EofEngineEndsDuringRelease
                    or AudioEnd.BufferFullEngineEndsDuringRelease or AudioEnd.EngineEndsWhileReading;
                if (engineOnly && owner == Owner.LiveAudio)
                {
                    continue;
                }

                foreach (var releaseFails in new[] { false, true })
                {
                    if ((end == AudioEnd.EofReleaseFailsWhileRecognitionBusy && !releaseFails)
                        || (end == AudioEnd.OpenThrows && releaseFails))
                    {
                        continue;
                    }

                    cases.Add(owner, end, releaseFails);
                }
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(AudioCases))]
    public async Task AudioSourceFollowsTheReleaseRule(Owner owner, AudioEnd end, bool releaseFails)
    {
        var read = end switch
        {
            AudioEnd.OpenThrows => AudioRead.OpenThrows,
            AudioEnd.ReadThrows => AudioRead.Throws,
            AudioEnd.CurrentThrows => AudioRead.CurrentThrows,
            AudioEnd.BadFormat => AudioRead.BadFormat,
            AudioEnd.CancelWhileReading or AudioEnd.RecognitionFailsWhileReading or AudioEnd.EngineEndsWhileReading
                or AudioEnd.DetectorFailsInAcceptance => AudioRead.Hold,
            AudioEnd.ReaderStopsEarly => AudioRead.GapThenHold,
            _ => AudioRead.Eof,
        };
        var full = end is AudioEnd.BufferFull or AudioEnd.BufferFullEngineEndsDuringRelease;
        var audio = new ScriptedAudio(full ? 120.5 : 0.5, read, releaseFails);
        var cause = new Cause();
        using var stop = new CancellationTokenSource();

        var decoderGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoder = end == AudioEnd.RecognitionFailsWhileReading
            ? new FakeHypothesisDecoder(_ => throw new InvalidOperationException(cause.Text("Decoderfehler")))
            : FakeHypothesisDecoder.Silent();
        if (end == AudioEnd.EofReleaseFailsWhileRecognitionBusy)
        {
            decoder.Hold = _ => decoderGate.Task;
        }

        var detector = FakeSpeechDetector.Speech(owner == Owner.LiveAudio ? (0, 1) : (0, 121));
        if (end == AudioEnd.DetectorFailsInAcceptance)
        {
            detector.FailAfter = Sec(0.2);
        }

        var engine = new FakeRecognitionEngine { FinishWithAudio = false };
        IRecognitionEngine usedEngine = end == AudioEnd.RecognitionFailsWhileReading ? new FailingEngine(cause) : engine;
        ITranscriptSource source = owner == Owner.LiveAudio
            ? new LiveTranscription(decoder, detector, DefaultRecognition, Options(LiveMode.Live))
            : new EngineTranscription(usedEngine, detector, DefaultRecognition, Sec(0.6));

        Exception? error = null;
        bool endedWhileHeld;
        if (end == AudioEnd.ReaderStopsEarly)
        {
            var reader = source.RunAsync(audio, stop.Token).GetAsyncEnumerator(stop.Token);
            try
            {
                Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
                await audio.ReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var dispose = reader.DisposeAsync().AsTask();
                endedWhileHeld = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10))) == dispose;
                await dispose;
            }
            finally
            {
                audio.OpenAll();
                engine.Finish();
                await stop.CancelAsync();
            }
        }
        else
        {
            var events = new Recorder<TranscriptEvent>(() => source.RunAsync(audio, stop.Token));
            try
            {
                switch (end)
                {
                    case AudioEnd.Eof or AudioEnd.BufferFull or AudioEnd.EofEngineEndsDuringRelease
                        or AudioEnd.BufferFullEngineEndsDuringRelease or AudioEnd.CancelWhileReleasing:
                        await audio.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        if (end is AudioEnd.EofEngineEndsDuringRelease or AudioEnd.BufferFullEngineEndsDuringRelease)
                        {
                            engine.Finish();
                        }

                        if (end == AudioEnd.CancelWhileReleasing)
                        {
                            await stop.CancelAsync();
                        }

                        break;

                    case AudioEnd.EofReleaseFailsWhileRecognitionBusy:
                        await audio.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        if (owner == Owner.LiveAudio)
                        {
                            await Until(() => decoder.Open > 0, "Decoder arbeitet noch");
                        }

                        audio.Release.TrySetResult();
                        break;

                    case AudioEnd.CancelWhileReading:
                        await audio.ReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        await stop.CancelAsync();
                        break;

                    case AudioEnd.EngineEndsWhileReading:
                        await audio.ReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        engine.Finish();
                        break;
                }

                if (Regular(end))
                {
                    endedWhileHeld = await Task.WhenAny(events.Completion, Task.Delay(300)) == events.Completion;
                    audio.Release.TrySetResult();
                    if (end is AudioEnd.Eof or AudioEnd.BufferFull)
                    {
                        engine.Finish();
                    }
                }
                else
                {
                    endedWhileHeld = await Task.WhenAny(events.Completion, Task.Delay(TimeSpan.FromSeconds(10))) == events.Completion;
                }

                error = await events.Finish();
            }
            finally
            {
                audio.OpenAll();
                decoderGate.TrySetResult();
                engine.Finish();
                await stop.CancelAsync();
                await events.Finish();
            }
        }

        if (Regular(end))
        {
            Assert.False(endedWhileHeld, "Reguläres Ende: Der Lauf wartet auf die Freigabe der Audioquelle.");
        }
        else
        {
            Assert.True(endedWhileHeld, "Der Lauf endet ohne Warten auf Lesen oder Freigabe.");
        }

        switch (end)
        {
            case AudioEnd.Eof or AudioEnd.BufferFull or AudioEnd.EofEngineEndsDuringRelease
                or AudioEnd.BufferFullEngineEndsDuringRelease or AudioEnd.EofReleaseFailsWhileRecognitionBusy:
                if (releaseFails)
                {
                    AssertCause(error, audio.ReleaseError);
                }
                else
                {
                    Assert.Null(error);
                }

                break;

            case AudioEnd.OpenThrows:
                AssertCause(error, audio.OpenError);
                break;

            case AudioEnd.ReadThrows:
                AssertCause(error, audio.ReadError);
                break;

            case AudioEnd.CurrentThrows:
                AssertCause(error, audio.CurrentError);
                break;

            case AudioEnd.BadFormat:
                Assert.IsType<ArgumentException>(error?.GetBaseException());
                break;

            case AudioEnd.RecognitionFailsWhileReading:
                AssertCause(error, cause.Text("Decoderfehler"), cause.Text("Erkennungsfehler"));
                break;

            case AudioEnd.DetectorFailsInAcceptance:
                Assert.IsType<TimeoutException>(error?.GetBaseException());
                break;

            case AudioEnd.CancelWhileReading or AudioEnd.CancelWhileReleasing:
                AssertCancelled(error);
                break;

            default:
                Assert.Null(error);
                break;
        }

        await AssertReleasedAsync(audio, end == AudioEnd.OpenThrows ? 0 : 1);
    }

    // ---- Folge der Engine ---------------------------------------------------------------------------------------

    public enum EngineEnd
    {
        /// <summary>Die Engine endet regulär; ihre Freigabe wird abgewartet.</summary>
        Regular,

        /// <summary>Beide Folgen regulär zu Ende, beide Freigaben laufen; die Audiofreigabe scheitert (Y01).</summary>
        AudioReleaseFailsWhileEngineReleaseRuns,

        /// <summary>Beide Folgen regulär zu Ende, beide Freigaben laufen; die Engine-Freigabe scheitert (Y01).</summary>
        EngineReleaseFailsWhileAudioReleaseRuns,

        /// <summary>Das Öffnen der Folge der Engine wirft; nichts ist erworben.</summary>
        OpenThrows,

        /// <summary>Lesen der Engine wirft.</summary>
        ReadThrows,

        /// <summary><c>Current</c> der Engine wirft.</summary>
        CurrentThrows,

        /// <summary>Ein Update verletzt den Vertrag (Kapitel 3, T66).</summary>
        InvalidUpdate,

        /// <summary>Abbruch des Aufrufers, während ein Lesen der Engine hängt, das den Abbruch übergeht.</summary>
        CancelWhileReading,

        /// <summary>Abbruch des Aufrufers während der regulären Freigabe der Engine.</summary>
        CancelWhileReleasing,

        /// <summary>Das Audio scheitert, während ein Lesen der Engine hängt.</summary>
        AudioFailsWhileReading,

        /// <summary>Der äussere Leser hört nach einem Ereignis auf, während ein Lesen der Engine hängt.</summary>
        ReaderStopsEarly,
    }

    public static TheoryData<EngineEnd, bool> EngineCases()
    {
        var cases = new TheoryData<EngineEnd, bool>();
        foreach (var end in Enum.GetValues<EngineEnd>())
        {
            foreach (var releaseFails in new[] { false, true })
            {
                // Y01-Fälle: Der Name legt fest, welche Freigabe scheitert; nur eine Zeile je Fall (6-24-02)
                if ((end == EngineEnd.OpenThrows && releaseFails)
                    || (end is EngineEnd.EngineReleaseFailsWhileAudioReleaseRuns or EngineEnd.AudioReleaseFailsWhileEngineReleaseRuns
                        && !releaseFails))
                {
                    continue;
                }

                cases.Add(end, releaseFails);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(EngineCases))]
    public async Task EngineSequenceFollowsTheReleaseRule(EngineEnd end, bool releaseFails)
    {
        var engineRead = end switch
        {
            EngineEnd.OpenThrows => EngineRead.OpenThrows,
            EngineEnd.ReadThrows => EngineRead.Throws,
            EngineEnd.CurrentThrows => EngineRead.CurrentThrows,
            EngineEnd.InvalidUpdate => EngineRead.InvalidUpdate,
            EngineEnd.CancelWhileReading or EngineEnd.AudioFailsWhileReading or EngineEnd.ReaderStopsEarly => EngineRead.Hold,
            _ => EngineRead.EndOnGate,
        };

        // Bei der Audiofreigabe scheitert nur der erste Y01-Fall; im zweiten scheitert die Engine
        var engine = new ScriptedEngine(engineRead, releaseFails && end != EngineEnd.AudioReleaseFailsWhileEngineReleaseRuns);
        var audioRead = end switch
        {
            EngineEnd.AudioFailsWhileReading => AudioRead.Throws,
            EngineEnd.ReaderStopsEarly => AudioRead.GapThenEof,
            _ => AudioRead.Eof,
        };

        // Y01: Die Audiofreigabe läuft noch und scheitert bzw. gelingt erst auf Anstoss
        var audioHeld = end is EngineEnd.AudioReleaseFailsWhileEngineReleaseRuns or EngineEnd.EngineReleaseFailsWhileAudioReleaseRuns;
        var audio = new ScriptedAudio(0.5, audioRead, releaseFails: end == EngineEnd.AudioReleaseFailsWhileEngineReleaseRuns);
        if (!audioHeld)
        {
            audio.Release.TrySetResult();
        }

        using var stop = new CancellationTokenSource();
        var source = new EngineTranscription(engine, FakeSpeechDetector.Speech((0, 2)), DefaultRecognition, Sec(0.6));
        Exception? error = null;
        bool endedWhileHeld;
        var regular = end == EngineEnd.Regular;
        if (end == EngineEnd.ReaderStopsEarly)
        {
            var reader = source.RunAsync(audio, stop.Token).GetAsyncEnumerator(stop.Token);
            try
            {
                Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
                await engine.ReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var dispose = reader.DisposeAsync().AsTask();
                endedWhileHeld = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10))) == dispose;
                await dispose;
            }
            finally
            {
                engine.OpenAll();
                audio.OpenAll();
                await stop.CancelAsync();
            }
        }
        else
        {
            var events = new Recorder<TranscriptEvent>(() => source.RunAsync(audio, stop.Token));
            try
            {
                switch (end)
                {
                    case EngineEnd.Regular or EngineEnd.CancelWhileReleasing:
                        engine.End.TrySetResult();
                        await engine.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        if (end == EngineEnd.CancelWhileReleasing)
                        {
                            await stop.CancelAsync();
                        }

                        break;

                    case EngineEnd.AudioReleaseFailsWhileEngineReleaseRuns or EngineEnd.EngineReleaseFailsWhileAudioReleaseRuns:
                        await audio.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        engine.End.TrySetResult();
                        await engine.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        Assert.False(events.Completion.IsCompleted, "Beide Freigaben laufen noch.");
                        (end == EngineEnd.AudioReleaseFailsWhileEngineReleaseRuns ? audio.Release : engine.Release).TrySetResult();
                        break;

                    case EngineEnd.CancelWhileReading:
                        await engine.ReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        await stop.CancelAsync();
                        break;
                }

                if (regular)
                {
                    endedWhileHeld = await Task.WhenAny(events.Completion, Task.Delay(300)) == events.Completion;
                    engine.Release.TrySetResult();
                }
                else
                {
                    endedWhileHeld = await Task.WhenAny(events.Completion, Task.Delay(TimeSpan.FromSeconds(10))) == events.Completion;
                }

                error = await events.Finish();
            }
            finally
            {
                engine.OpenAll();
                audio.OpenAll();
                await stop.CancelAsync();
                await events.Finish();
            }
        }

        if (regular)
        {
            Assert.False(endedWhileHeld, "Reguläres Ende: Der Lauf wartet auf die Freigabe der Engine.");
        }
        else
        {
            Assert.True(endedWhileHeld, "Der Lauf endet ohne Warten auf Lesen oder Freigabe der Engine.");
        }

        switch (end)
        {
            case EngineEnd.Regular:
                if (releaseFails)
                {
                    AssertCause(error, engine.ReleaseError);
                }
                else
                {
                    Assert.Null(error);
                }

                break;

            case EngineEnd.AudioReleaseFailsWhileEngineReleaseRuns:
                AssertCause(error, audio.ReleaseError);
                break;

            case EngineEnd.EngineReleaseFailsWhileAudioReleaseRuns:
                AssertCause(error, engine.ReleaseError);
                break;

            case EngineEnd.OpenThrows:
                AssertCause(error, engine.OpenError);
                break;

            case EngineEnd.ReadThrows:
                AssertCause(error, engine.ReadError);
                break;

            case EngineEnd.CurrentThrows:
                AssertCause(error, engine.CurrentError);
                break;

            case EngineEnd.InvalidUpdate:
                // Vertragsverstoss: P3 meldet ihn selbst, ohne fremde Ursache
                Assert.IsType<InvalidOperationException>(error);
                Assert.DoesNotContain(Messages(error), message => message.Contains(engine.Marker, StringComparison.Ordinal));
                break;

            case EngineEnd.AudioFailsWhileReading:
                AssertCause(error, audio.ReadError);
                break;

            case EngineEnd.CancelWhileReading or EngineEnd.CancelWhileReleasing:
                AssertCancelled(error);
                break;

            default:
                Assert.Null(error);
                break;
        }

        await AssertReleasedAsync(engine, end == EngineEnd.OpenThrows ? 0 : 1);
        await AssertReleasedAsync(audio, 1);
    }

    // ---- Quelle der Sitzung -------------------------------------------------------------------------------------

    public enum SessionEnd
    {
        /// <summary>Die Quelle endet regulär; ihre Freigabe wird abgewartet.</summary>
        Regular,

        /// <summary>Der Aufruf von <c>RunAsync</c> der Quelle wirft; nichts ist erworben.</summary>
        OpenThrows,

        /// <summary><c>GetAsyncEnumerator</c> der Folge wirft; nichts ist erworben (6-24-01).</summary>
        EnumeratorThrows,

        /// <summary><c>Current</c> der Quelle wirft (6-24-01).</summary>
        CurrentThrows,

        /// <summary>Lesen der Quelle wirft.</summary>
        ReadThrows,

        /// <summary>Die Verarbeitung in der Sitzung scheitert (Auswertung des Geltungsbereichs).</summary>
        ProcessingThrows,

        /// <summary>Abbruchbefehl „abbrechen“ nach der Befehlstaste (T17, T55).</summary>
        CancelCommand,

        /// <summary>Abbruch des Aufrufers, während ein Lesen hängt, das den Abbruch übergeht.</summary>
        CancelWhileReading,

        /// <summary>Abbruch des Aufrufers während der regulären Freigabe.</summary>
        CancelWhileReleasing,

        /// <summary>Der äussere Leser hört nach einer Ausgabe auf, während ein Lesen hängt.</summary>
        ReaderStopsEarly,
    }

    public static TheoryData<SessionEnd, bool> SessionCases()
    {
        var cases = new TheoryData<SessionEnd, bool>();
        foreach (var end in Enum.GetValues<SessionEnd>())
        {
            foreach (var releaseFails in new[] { false, true })
            {
                if (end is SessionEnd.OpenThrows or SessionEnd.EnumeratorThrows && releaseFails)
                {
                    continue;
                }

                cases.Add(end, releaseFails);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(SessionCases))]
    public async Task SessionSourceFollowsTheReleaseRule(SessionEnd end, bool releaseFails)
    {
        var script = end switch
        {
            SessionEnd.OpenThrows => SourceScript.OpenThrows,
            SessionEnd.EnumeratorThrows => SourceScript.EnumeratorThrows,
            SessionEnd.CurrentThrows => SourceScript.CurrentThrows,
            SessionEnd.ReadThrows => SourceScript.Throws,
            SessionEnd.ProcessingThrows => SourceScript.TextThenHold,
            SessionEnd.CancelCommand => SourceScript.CancelCommandThenHold,
            SessionEnd.CancelWhileReading => SourceScript.Hold,
            SessionEnd.ReaderStopsEarly => SourceScript.NoticeThenHold,
            _ => SourceScript.End,
        };
        using var stop = new CancellationTokenSource();
        var source = new HeldTranscriptSource(script, releaseFails);
        var interpreter = new DictationInterpreter(BuiltInCommands.Pilot, Interpreting());
        var scopeError = source.Text("Bereichsfehler");
        var session = new DictationSession(
            source,
            interpreter,
            end == SessionEnd.ProcessingThrows ? () => throw new InvalidDataException(scopeError) : () => CommandScope.DictationWindow);
        var audio = new ScriptedAudio(0, AudioRead.Eof, releaseFails: false);
        audio.Release.TrySetResult();

        bool endedWhileHeld;
        Exception? error = null;
        IReadOnlyList<DictationOutput> items = [];
        if (end == SessionEnd.ReaderStopsEarly)
        {
            var reader = session.RunAsync(audio, stop.Token).GetAsyncEnumerator(stop.Token);
            try
            {
                Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
                var dispose = reader.DisposeAsync().AsTask();
                endedWhileHeld = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10))) == dispose;
                await dispose;
            }
            finally
            {
                source.OpenAll();
                await stop.CancelAsync();
            }
        }
        else
        {
            var outputs = new Recorder<DictationOutput>(() => session.RunAsync(audio, stop.Token));
            try
            {
                switch (end)
                {
                    case SessionEnd.Regular or SessionEnd.CancelWhileReleasing:
                        await source.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        if (end == SessionEnd.CancelWhileReleasing)
                        {
                            await stop.CancelAsync();
                        }

                        break;

                    case SessionEnd.CancelWhileReading:
                        await source.ReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        await stop.CancelAsync();
                        break;

                    case SessionEnd.CancelCommand:
                        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        session.PressCommandKey();
                        source.Go.TrySetResult();
                        break;

                    case SessionEnd.ProcessingThrows:
                        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        source.Go.TrySetResult();
                        break;
                }

                if (end == SessionEnd.Regular)
                {
                    endedWhileHeld = await Task.WhenAny(outputs.Completion, Task.Delay(300)) == outputs.Completion;
                    source.Release.TrySetResult();
                }
                else
                {
                    endedWhileHeld = await Task.WhenAny(outputs.Completion, Task.Delay(TimeSpan.FromSeconds(10))) == outputs.Completion;
                }

                error = await outputs.Finish();
                items = outputs.Items;
            }
            finally
            {
                source.OpenAll();
                await stop.CancelAsync();
                await outputs.Finish();
            }
        }

        if (end == SessionEnd.Regular)
        {
            Assert.False(endedWhileHeld, "Reguläres Ende: Die Sitzung wartet auf die Freigabe der Quelle.");
        }
        else
        {
            Assert.True(endedWhileHeld, "Die Sitzung endet ohne Warten auf Lesen oder Freigabe der Quelle.");
        }

        switch (end)
        {
            case SessionEnd.Regular:
                if (releaseFails)
                {
                    AssertCause(error, source.ReleaseError);
                }
                else
                {
                    Assert.Null(error);
                }

                break;

            case SessionEnd.OpenThrows or SessionEnd.EnumeratorThrows:
                AssertCause(error, source.OpenError);
                break;

            case SessionEnd.CurrentThrows:
                AssertCause(error, source.CurrentError);
                Assert.Empty(items);
                break;

            case SessionEnd.ReadThrows:
                AssertCause(error, source.ReadError);
                break;

            case SessionEnd.ProcessingThrows:
                AssertCause(error, scopeError);
                break;

            case SessionEnd.CancelCommand:
                Assert.Null(error);
                Assert.Contains(items, output => output is CommandOutput);
                break;

            case SessionEnd.CancelWhileReading or SessionEnd.CancelWhileReleasing:
                AssertCancelled(error);
                break;

            default:
                Assert.Null(error);
                break;
        }

        await AssertReleasedAsync(source, end is SessionEnd.OpenThrows or SessionEnd.EnumeratorThrows ? 0 : 1);
    }

    // ---- Kontrollen der Beobachtung (Y02) -----------------------------------------------------------------------

    [Fact]
    public async Task ObserverReportsALostFailure()
    {
        var marker = Guid.NewGuid().ToString("N");
        LoseFailure(marker);
        await Assert.ThrowsAnyAsync<Exception>(() => UnobservedFailures.AssertNoneAsync(marker));
    }

    [Fact]
    public async Task ObserverIgnoresAnObservedFailure()
    {
        var marker = Guid.NewGuid().ToString("N");
        await ObserveFailure(marker);
        await UnobservedFailures.AssertNoneAsync(marker);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoseFailure(string marker) => _ = Task.Run(() => throw new IOException(marker));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ObserveFailure(string marker)
    {
        var failed = Task.Run(() => throw new IOException(marker));
        await Assert.ThrowsAsync<IOException>(() => failed);
    }

    // ---- Hilfen -------------------------------------------------------------------------------------------------

    /// <summary>Kein Fehler mit dieser Kennung bleibt unbeobachtet. Das Protokoll läuft seit dem Laden der Tests.</summary>
    private static class UnobservedFailures
    {
        public static async Task AssertNoneAsync(string marker)
        {
            Assert.False(await UnobservedFailureLog.SeenAsync(marker), $"Unbeobachteter Fehler mit Kennung {marker}.");
        }
    }

    private static IEnumerable<string> Messages(Exception? error)
    {
        var pending = new Stack<Exception>();
        if (error is not null)
        {
            pending.Push(error);
        }

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current.Message;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }

    /// <summary>Der Lauf endete mit einer der erwarteten Ursachen, erkannt an ihrer Meldung.</summary>
    private static void AssertCause(Exception? error, params string[] expected)
    {
        Assert.NotNull(error);
        var messages = Messages(error).ToList();
        Assert.True(
            messages.Any(message => expected.Contains(message, StringComparer.Ordinal)),
            $"Erwartete Ursache fehlt; gemeldet: {string.Join(" | ", messages)}");
    }

    /// <summary>Abbruch: kein Fehler oder genau der Abbruch, nie eine andere Ursache.</summary>
    private static void AssertCancelled(Exception? error)
    {
        if (error is not null)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        }
    }

    private static async Task AssertReleasedAsync(IReleaseProbe probe, int expected)
    {
        if (expected > 0)
        {
            await Until(() => probe.DisposeCount >= expected, "Folge freigegeben");
            await probe.Released.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await Task.Delay(50);
        Assert.Equal(expected, probe.DisposeCount);
        await UnobservedFailures.AssertNoneAsync(probe.Marker);
    }

    private interface IReleaseProbe
    {
        int DisposeCount { get; }

        TaskCompletionSource Released { get; }

        string Marker { get; }
    }

    /// <summary>Meldungen mit Kennung des Falls, damit genau die erwartete Ursache erkannt wird.</summary>
    private sealed class Cause
    {
        public string Marker { get; } = Guid.NewGuid().ToString("N");

        public string Text(string what) => $"synthetischer {what} {Marker}";
    }

    private enum AudioRead { Eof, OpenThrows, Throws, CurrentThrows, BadFormat, Hold, GapThenHold, GapThenEof }

    /// <summary>Audiofolge: ein Block (bei einer Lücke zwei), dann das jeweilige Ende; Freigabe gehalten.</summary>
    private sealed class ScriptedAudio(double seconds, AudioRead read, bool releaseFails)
        : IAsyncEnumerable<AudioChunk>, IAsyncEnumerator<AudioChunk>, IReleaseProbe
    {
        private readonly Cause _cause = new();
        private int _disposeCount;
        private int _step;

        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Marker => _cause.Marker;

        public string OpenError => _cause.Text("Öffnungsfehler");

        public string ReadError => _cause.Text("Lesefehler");

        public string CurrentError => _cause.Text("Current-Fehler");

        public string ReleaseError => _cause.Text("Freigabefehler");

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public AudioChunk Current => read switch
        {
            AudioRead.CurrentThrows => throw new InvalidDataException(CurrentError),
            AudioRead.BadFormat => TestAudio.Chunk(Sec(0), Sec(seconds)) with { SampleRate = 48_000 },
            AudioRead.GapThenHold or AudioRead.GapThenEof when _step == 2 => TestAudio.Chunk(Sec(1), Sec(1.5)),
            _ => TestAudio.Chunk(Sec(0), Sec(seconds)),
        };

        public void OpenAll()
        {
            ReadGate.TrySetResult();
            Release.TrySetResult();
        }

        public IAsyncEnumerator<AudioChunk> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            read == AudioRead.OpenThrows ? throw new InvalidDataException(OpenError) : this;

        public async ValueTask<bool> MoveNextAsync()
        {
            _step++;
            var blocks = read is AudioRead.GapThenHold or AudioRead.GapThenEof ? 2 : seconds > 0 ? 1 : 0;
            if (_step <= blocks)
            {
                return true;
            }

            switch (read)
            {
                case AudioRead.Throws:
                    throw new InvalidDataException(ReadError);
                case AudioRead.Hold or AudioRead.GapThenHold:
                    ReadHeld.TrySetResult();
                    await ReadGate.Task;
                    return false;
                default:
                    return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            Disposing.TrySetResult();
            try
            {
                await Release.Task;
                if (releaseFails)
                {
                    throw new IOException(ReleaseError);
                }
            }
            finally
            {
                Released.TrySetResult();
            }
        }
    }

    /// <summary>Erkennung, die beim ersten Audio scheitert.</summary>
    private sealed class FailingEngine(Cause cause) : IRecognitionEngine
    {
        public RecognitionRoute Route => RecognitionRoute.Device;

        public RecognitionCapabilities Capabilities { get; } = new FakeRecognitionEngine().Capabilities;

        public async IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(
            IAsyncEnumerable<AudioChunk> audio,
            RecognitionOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var _ in audio.WithCancellation(cancellationToken))
            {
                throw new InvalidOperationException(cause.Text("Erkennungsfehler"));
            }

            yield break;
        }
    }

    private enum EngineRead { EndOnGate, OpenThrows, Throws, CurrentThrows, InvalidUpdate, Hold }

    /// <summary>Folge der Engine mit gesteuertem Ende und gehaltener Freigabe.</summary>
    private sealed class ScriptedEngine(EngineRead read, bool releaseFails) : IRecognitionEngine, IReleaseProbe
    {
        private readonly Cause _cause = new();
        private int _disposeCount;

        public RecognitionRoute Route => RecognitionRoute.Device;

        public RecognitionCapabilities Capabilities { get; } = new FakeRecognitionEngine().Capabilities;

        public TaskCompletionSource End { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Marker => _cause.Marker;

        public string OpenError => _cause.Text("Öffnungsfehler der Engine");

        public string ReadError => _cause.Text("Lesefehler der Engine");

        public string CurrentError => _cause.Text("Current-Fehler der Engine");

        public string ReleaseError => _cause.Text("Freigabefehler der Engine");

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public EngineRead Read { get; } = read;

        public bool ReleaseFails { get; } = releaseFails;

        public void OpenAll()
        {
            End.TrySetResult();
            ReadGate.TrySetResult();
            Release.TrySetResult();
        }

        public IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(
            IAsyncEnumerable<AudioChunk> audio,
            RecognitionOptions options,
            CancellationToken cancellationToken = default) => new Updates(this, audio, cancellationToken);

        private sealed class Updates(ScriptedEngine owner, IAsyncEnumerable<AudioChunk> audio, CancellationToken token)
            : IAsyncEnumerable<RecognitionUpdate>, IAsyncEnumerator<RecognitionUpdate>
        {
            private bool _sent;

            public RecognitionUpdate Current => owner.Read switch
            {
                EngineRead.CurrentThrows => throw new InvalidDataException(owner.CurrentError),

                // Fester Abschnitt mit Ende vor dem Beginn: Vertragsverstoss (Kapitel 3, T66)
                _ => new RecognitionUpdate(
                    [new TextSegment(Guid.NewGuid(), "Rest", SegmentState.Fixed, Sec(0.4), Sec(0.2))], []),
            };

            public IAsyncEnumerator<RecognitionUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
                owner.Read == EngineRead.OpenThrows ? throw new InvalidDataException(owner.OpenError) : this;

            public async ValueTask<bool> MoveNextAsync()
            {
                switch (owner.Read)
                {
                    case EngineRead.Throws:
                        throw new InvalidDataException(owner.ReadError);
                    case EngineRead.CurrentThrows or EngineRead.InvalidUpdate when !_sent:
                        _sent = true;
                        return true;
                    case EngineRead.Hold:
                        owner.ReadHeld.TrySetResult();
                        await owner.ReadGate.Task;
                        return false;
                    case EngineRead.EndOnGate:
                        await foreach (var _ in audio.WithCancellation(token))
                        {
                        }

                        await owner.End.Task;
                        return false;
                    default:
                        return false;
                }
            }

            public async ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner._disposeCount);
                owner.Disposing.TrySetResult();
                try
                {
                    await owner.Release.Task;
                    if (owner.ReleaseFails)
                    {
                        throw new IOException(owner.ReleaseError);
                    }
                }
                finally
                {
                    owner.Released.TrySetResult();
                }
            }
        }
    }

    private enum SourceScript { End, OpenThrows, EnumeratorThrows, CurrentThrows, Throws, TextThenHold, CancelCommandThenHold, Hold, NoticeThenHold }

    /// <summary>Quelle der Sitzung mit gesteuertem Ablauf und gehaltener Freigabe.</summary>
    private sealed class HeldTranscriptSource(SourceScript script, bool releaseFails) : ITranscriptSource, IReleaseProbe
    {
        private readonly Cause _cause = new();
        private int _disposeCount;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Go { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Marker => _cause.Marker;

        public string OpenError => _cause.Text("Öffnungsfehler der Quelle");

        public string ReadError => _cause.Text("Lesefehler der Quelle");

        public string CurrentError => _cause.Text("Current-Fehler der Quelle");

        public string ReleaseError => _cause.Text("Freigabefehler der Quelle");

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public SourceScript Script { get; } = script;

        public bool ReleaseFails { get; } = releaseFails;

        public TimeSpan AcceptedEnd => TimeSpan.Zero;

        public TimeSpan Backlog => TimeSpan.Zero;

        public string Text(string what) => _cause.Text(what);

        public void MarkBoundary(TimeSpan at)
        {
        }

        public void OpenAll()
        {
            Go.TrySetResult();
            ReadGate.TrySetResult();
            Release.TrySetResult();
        }

        public IAsyncEnumerable<TranscriptEvent> RunAsync(IAsyncEnumerable<AudioChunk> audio, CancellationToken cancellationToken = default) =>
            Script == SourceScript.OpenThrows ? throw new InvalidDataException(OpenError) : new Events(this);

        private sealed class Events(HeldTranscriptSource owner) : IAsyncEnumerable<TranscriptEvent>, IAsyncEnumerator<TranscriptEvent>
        {
            private bool _sent;

            private TranscriptEvent _current = null!;

            public TranscriptEvent Current
            {
                get => owner.Script == SourceScript.CurrentThrows ? throw new InvalidDataException(owner.CurrentError) : _current;
                private set => _current = value;
            }

            public IAsyncEnumerator<TranscriptEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
                owner.Script == SourceScript.EnumeratorThrows ? throw new InvalidDataException(owner.OpenError) : this;

            public async ValueTask<bool> MoveNextAsync()
            {
                owner.Started.TrySetResult();
                if (!_sent)
                {
                    _sent = true;
                    switch (owner.Script)
                    {
                        case SourceScript.CurrentThrows:
                            return true;
                        case SourceScript.NoticeThenHold:
                            Current = new LiveNotice(LiveNoticeKind.SpeechWithoutText, Sec(0), Sec(1));
                            return true;
                        case SourceScript.TextThenHold:
                            await owner.Go.Task;
                            Current = new UtteranceCompleted(Utterance(0.5, "Hallo"));
                            return true;
                        case SourceScript.CancelCommandThenHold:
                            await owner.Go.Task;
                            Current = new UtteranceCompleted(Utterance(0.5, "abbrechen"));
                            return true;
                    }
                }

                switch (owner.Script)
                {
                    case SourceScript.Throws:
                        throw new InvalidDataException(owner.ReadError);
                    case SourceScript.End:
                        return false;
                    default:
                        owner.ReadHeld.TrySetResult();
                        await owner.ReadGate.Task;
                        return false;
                }
            }

            public async ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner._disposeCount);
                owner.Disposing.TrySetResult();
                try
                {
                    await owner.Release.Task;
                    if (owner.ReleaseFails)
                    {
                        throw new IOException(owner.ReleaseError);
                    }
                }
                finally
                {
                    owner.Released.TrySetResult();
                }
            }
        }
    }
}
