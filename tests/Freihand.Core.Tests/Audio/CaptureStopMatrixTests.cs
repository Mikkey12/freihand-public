using Freihand.Adapters.Audio;
using Freihand.Core.Platform;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Audio.Kit;
using static Freihand.Core.Tests.Audio.Kit.SignalAnalysis;

namespace Freihand.Core.Tests.Audio;

/// <summary>
/// Fehlerstopp als Matrix mit Abdeckungsinventar (P8-Konzept, Kapitel 5.2 und 5.3; erweitert R37–R41 und R49; Lehre
/// aus P3: jede Fehlerart über alle Kombinationen). Gerät 48 kHz Stereo Float32, Puffer zu 100 ms, Startwerte aus
/// <see cref="CaptureOptions.Default"/>, Testuhr.
/// <para><b>Achsen.</b> Auslöser je <see cref="CaptureStopReason"/>: Fabrik wirft <see cref="CaptureBackendException"/>,
/// <c>Start</c> wirft sie, die Attrappe meldet <c>Stopped(reason)</c> (für die Gerätefehler DeviceMissing,
/// DeviceRemoved, AccessDenied, DeviceInUse, Failure), eigene Erkennung der Sitzung (NoSignal nach 1 s ohne Daten,
/// QueueFull nach 5 s ungelesenem Audio, Discontinuity bei einem Aussetzer ohne <c>MissingFrames</c>) und
/// <c>Stop()</c> für Requested. Zeitpunkt: in Opening vor jeglichen Daten; in Recording, während der Leser laufend liest
/// (0,5 s Daten); während der Leser nicht liest und die Warteschlange teilweise gefüllt ist (0,9 s von 5 s, so dass ein Rest im Wandler liegt); nach einem
/// schon erfolgten <c>Stop()</c> (0,5 s Daten). Leser: liest laufend, liest erst nach dem Stopp, liest nie und gibt nur
/// frei. 98 Fälle.</para>
/// <para><b>Invarianten je Fall.</b> <c>Stopped</c> mit genau dem ersten Grund, nach <c>Stop()</c> also Requested; danach
/// lösen späte Ereignisse (Fehlermeldung der Attrappe, weiteres <c>Stop()</c>, 6 s auf der Uhr) keinen anderen Grund aus,
/// weder im Zustand noch in <c>StatusChanged</c>. Nie <c>Recording</c> vor Daten mit mindestens einem Sample, ohne Daten
/// auch keine <c>StartLatency</c>. Die Attrappe wurde genau einmal gestartet; sie hat genau einen Übergang in den
/// gestoppten Zustand, wenn der Start gelang (sonst keinen), höchstens einen Stop-Aufruf und genau einen, wenn die
/// Sitzung selbst stoppt (eigene Erkennung, Stop()); nach <c>DisposeAsync</c> ist sie genau einmal freigegeben. Die
/// Fabrik wurde genau einmal aufgerufen, mit dem gewählten Gerät. <c>ReadAsync</c> endet ohne Ausnahme. Die gelieferten
/// Blöcke gleichen bitgenau allem Umgewandelten samt Nachlauf (<see cref="ReferenceConversion"/>), ihre Sample-Zahl
/// stimmt auf ±16 mit den umgewandelten Geräteframes überein (kein aufgefülltes Audio, bei Discontinuity entfällt der
/// Puffer nach dem Aussetzer). <c>Unread</c> nach der Freigabe gleich dem ungelesenen Audio (alles oder nichts).
/// Kein unbeobachteter Fehler (<see cref="CaptureFailureWatch"/>).</para>
/// <para><b>Ausgelassen, mit Grund.</b> Fabrik und Start werfen nur beim Öffnen, also nur in Opening. Die Attrappe meldet
/// nur Gerätefehler; Requested, NoSignal, QueueFull und Discontinuity stellt die Sitzung selbst fest und kommen nicht als
/// <c>Stopped(reason)</c> vom Gerät. QueueFull nicht in Opening (keine Daten), nicht in Recording (der laufende Leser
/// leert die Warteschlange) und nicht nach Stop(). Discontinuity nicht nach Stop(), weil beide Daten nach dem Stopp
/// bräuchten und das Gerät nach Stop() nichts mehr liefert; nicht in Opening, weil das Flag auf dem allerersten Puffer
/// nach Kapitel 10.4 ohne Wirkung bleibt und damit keinen Stopp auslöst (geprüft in der Codex-Gegenprobe C05,
/// <see cref="Freihand.Core.Tests.Recognition.P8ReviewCounterTests"/>). Recording mit
/// spätem oder ohne Leser fällt mit „Warteschlange teilweise gefüllt“ zusammen; diese Zeile mit laufendem Leser ist ein
/// Widerspruch.</para>
/// <para><b>Rotnachweis: fehlende Produktregel (Kapitel 5.2/5.3) → rote Fälle.</b>
/// „Erster Grund bleibt“ → alle Fälle nach Stop() und die Nachzügler-Prüfung in jedem Fall.
/// „Recording erst mit Daten mit mindestens einem Sample; 0-Byte-Puffer zählen nicht“ → alle Opening-Fälle (StatusChanged)
/// und NoSignal in Opening (0-Byte-Puffer).
/// „NoSignal auch vor den ersten Daten“ → NoSignal in Opening.
/// „Fehler von Fabrik und Start ergeben Stopped(Grund), keine Ausnahme aus Open“ → alle FactoryThrows- und StartThrows-Fälle.
/// „Fehlerstopp schliesst das Mikrofon (Stop)“ → NoSignal, QueueFull, Discontinuity (genau ein Stop-Aufruf).
/// „DisposeAsync gibt den Zugang frei, auch nach gescheitertem Start“ → alle Fälle mit Zugang, besonders StartThrows.
/// „Schon Aufgenommenes bleibt nach jedem Stopp lesbar, samt Nachlauf (Flush)“ → alle Fälle mit Daten und Leser; ohne
/// Flush fehlt der Rest im Wandler (0,5 s und 0,9 s sind keine ganzen Blöcke).
/// „ReadAsync endet regulär nach dem letzten Block“ → alle Fälle mit Leser.
/// „Unread beim Schliessen“ → Fälle ohne Leser mit Daten.
/// „Warteschlange höchstens MaxQueuedAudio, dann QueueFull“ → QueueFull-Fälle.
/// „Aussetzer unbekannter Dauer stoppt, Puffer danach entfällt, nie zusammengeschoben“ → Discontinuity-Fälle.
/// „Kein anderes Gerät“ → alle Fälle (Fabrik genau einmal, mit dem gewählten Gerät).
/// „Ereignisse nach dem Stopp (spätes Stopped(null), späte Fehler) still hinnehmen“ → alle Fälle mit Stop(), über den
/// Fehlerwächter.</para>
/// </summary>
public sealed class CaptureStopMatrixTests
{
    public enum Trigger
    {
        /// <summary>Die Fabrik wirft <see cref="CaptureBackendException"/>.</summary>
        FactoryThrows,

        /// <summary><see cref="ICaptureBackend.Start"/> wirft <see cref="CaptureBackendException"/>.</summary>
        StartThrows,

        /// <summary>Die Attrappe meldet <c>Stopped(reason)</c>.</summary>
        BackendStopped,

        /// <summary>1 s keine Daten (in Opening: 3 s nur Puffer mit 0 Byte).</summary>
        NoSignal,

        /// <summary>Weiter Daten ohne Leser, bis 5 s in der Warteschlange liegen.</summary>
        QueueFull,

        /// <summary>Ein Puffer mit <c>Discontinuity</c> und <c>MissingFrames</c> null.</summary>
        Discontinuity,

        /// <summary><see cref="ICaptureSession.Stop"/>.</summary>
        Requested,
    }

    public enum Moment
    {
        /// <summary>Vor jeglichen Daten.</summary>
        Opening,

        /// <summary>Nach 0,5 s Daten, der Leser liest laufend.</summary>
        Recording,

        /// <summary>Nach 0,9 s Daten ohne Leser; die Warteschlange ist teilweise gefüllt, ein Rest liegt im Wandler.</summary>
        QueuePartial,

        /// <summary>Nach 0,5 s Daten und einem Stop().</summary>
        AfterStop,
    }

    public enum Reader
    {
        Continuous,
        Afterwards,
        Never,
    }

    private static readonly CaptureStopReason[] DeviceFaults =
    [
        CaptureStopReason.DeviceMissing,
        CaptureStopReason.DeviceRemoved,
        CaptureStopReason.AccessDenied,
        CaptureStopReason.DeviceInUse,
        CaptureStopReason.Failure,
    ];

    public static TheoryData<Trigger, CaptureStopReason, Moment, Reader> Cases()
    {
        var cases = new TheoryData<Trigger, CaptureStopReason, Moment, Reader>();
        foreach (var trigger in Enum.GetValues<Trigger>())
        {
            CaptureStopReason[] reasons = trigger switch
            {
                Trigger.FactoryThrows or Trigger.StartThrows or Trigger.BackendStopped => DeviceFaults,
                Trigger.NoSignal => [CaptureStopReason.NoSignal],
                Trigger.QueueFull => [CaptureStopReason.QueueFull],
                Trigger.Discontinuity => [CaptureStopReason.Discontinuity],
                _ => [CaptureStopReason.Requested],
            };
            Moment[] moments = trigger switch
            {
                Trigger.FactoryThrows or Trigger.StartThrows => [Moment.Opening],
                Trigger.QueueFull => [Moment.QueuePartial],
                Trigger.Discontinuity => [Moment.Recording, Moment.QueuePartial],
                _ => Enum.GetValues<Moment>(),
            };
            foreach (var reason in reasons)
            {
                foreach (var moment in moments)
                {
                    Reader[] readers = moment switch
                    {
                        Moment.Recording => [Reader.Continuous],
                        Moment.QueuePartial => [Reader.Afterwards, Reader.Never],
                        _ => Enum.GetValues<Reader>(),
                    };
                    foreach (var reader in readers)
                    {
                        cases.Add(trigger, reason, moment, reader);
                    }
                }
            }
        }

        return cases;
    }

    [Fact]
    public void R37_StopMatrixInventoryIsComplete()
    {
        var cases = Cases().Select(row => ((Trigger)row[0], (CaptureStopReason)row[1], (Moment)row[2], (Reader)row[3])).ToList();

        Assert.Equal(98, cases.Count);
        Assert.Equal(cases.Count, cases.Distinct().Count());

        // Jeder Grund kommt vor, jeder Zeitpunkt und jeder Leser
        Assert.Equal(Enum.GetValues<CaptureStopReason>().Order(), cases.Select(c => c.Item2).Distinct().Order());
        Assert.Equal(Enum.GetValues<Moment>().Order(), cases.Select(c => c.Item3).Distinct().Order());
        Assert.Equal(Enum.GetValues<Reader>().Order(), cases.Select(c => c.Item4).Distinct().Order());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task R37_StopMatrix_EveryReasonMomentAndReader(Trigger trigger, CaptureStopReason reason, Moment moment, Reader reader)
    {
        var context = $"{trigger}({reason}), {moment}, {reader}";
        await using var rig = CaptureRig.Open(configure: factory =>
        {
            factory.OpenFailure = trigger == Trigger.FactoryThrows ? reason : null;
            factory.StartFailure = trigger == Trigger.StartThrows ? reason : null;
        });
        var running = reader == Reader.Continuous ? new ChunkReader(rig.Session) : null;
        FakeCaptureBackend? backend = trigger switch
        {
            Trigger.FactoryThrows => null,
            Trigger.StartThrows => await rig.Factory.CreatedAsync(),
            _ => await rig.StartedAsync(),
        };

        // Zeitpunkt
        switch (moment)
        {
            case Moment.Recording:
                rig.FeedFor(TimeSpan.FromSeconds(0.5));
                await rig.WaitForAsync(status => status.Phase == CapturePhase.Recording, "Recording");
                break;
            case Moment.QueuePartial:
                rig.FeedFor(TimeSpan.FromSeconds(0.9));
                await rig.WaitForAsync(status => status.Phase == CapturePhase.Recording, "Recording");
                break;
            case Moment.AfterStop:
                rig.FeedFor(TimeSpan.FromSeconds(0.5));
                rig.Session.Stop();
                await rig.StoppedAsync();
                break;
        }

        // Auslöser
        switch (trigger)
        {
            case Trigger.BackendStopped:
                backend!.Fail(reason);
                break;
            case Trigger.NoSignal when moment == Moment.Opening:
                await rig.Clock.WaitForTimersAsync();
                for (var i = 0; i < (int)(rig.Options.FirstDataTimeout / CaptureRig.BufferLength); i++)
                {
                    rig.Clock.Advance(CaptureRig.BufferLength);
                    backend!.Deliver([]);
                }

                break;
            case Trigger.NoSignal when moment == Moment.AfterStop:
                rig.Clock.Advance(rig.Options.NoSignalTimeout);
                break;
            case Trigger.NoSignal:
                await rig.Clock.WaitForTimersAsync();
                rig.Clock.Advance(rig.Options.NoSignalTimeout);
                break;
            case Trigger.QueueFull:
                var fed = TimeSpan.FromSeconds(0.9);
                while (rig.Session.Status.Phase != CapturePhase.Stopped && fed < TimeSpan.FromSeconds(6) && rig.Feed())
                {
                    fed += CaptureRig.BufferLength;
                }

                Assert.True(
                    fed >= TimeSpan.FromSeconds(5) && fed <= TimeSpan.FromSeconds(5.1),
                    $"{context}: QueueFull nach {fed.TotalSeconds} s Audio statt nach 5 s (höchstens ein Puffer später).");
                break;
            case Trigger.Discontinuity:
                Assert.True(
                    rig.Feed(rig.Signal.Next(CaptureRig.BufferLength), CaptureRig.BufferLength, discontinuity: true, missingFrames: null),
                    $"{context}: Aussetzer-Puffer nicht geliefert.");
                break;
            case Trigger.Requested:
                rig.Session.Stop();
                break;
        }

        var expected = moment == Moment.AfterStop ? CaptureStopReason.Requested : reason;
        var stopped = await rig.StoppedAsync();
        Assert.True(stopped.StopReason == expected, $"{context}: Stopp mit {stopped.StopReason} statt {expected}.");

        // Nachzügler: spätere Gründe überschreiben den ersten nicht
        backend?.Fail(reason == CaptureStopReason.Failure ? CaptureStopReason.DeviceRemoved : CaptureStopReason.Failure);
        rig.Session.Stop();
        rig.Clock.Advance(rig.Options.NoSignalTimeout + rig.Options.SilentSignalHint);
        Assert.True(rig.Session.Status.StopReason == expected, $"{context}: Grund nach Nachzüglern {rig.Session.Status.StopReason}.");

        // Leser
        IReadOnlyList<AudioChunk>? read = reader switch
        {
            Reader.Continuous => await running!.CompleteAsync(),
            Reader.Afterwards => await ChunkReader.ReadAllAsync(rig.Session),
            _ => null,
        };
        await rig.CloseAsync();

        // Zustand
        var status = rig.Session.Status;
        Assert.True(status.Phase == CapturePhase.Stopped, $"{context}: Phase {status.Phase} nach der Freigabe.");
        Assert.True(status.StopReason == expected, $"{context}: Grund {status.StopReason} nach der Freigabe.");
        rig.Statuses.AssertConsistent(expected, context);
        if (rig.Factory.FramesDelivered == 0)
        {
            Assert.False(rig.Statuses.SawRecording, $"{context}: Recording ohne Daten.");
            Assert.True(status.StartLatency is null, $"{context}: StartLatency {status.StartLatency} ohne Daten.");
        }

        // Fabrik und Attrappe
        Assert.True(rig.Factory.Devices.SequenceEqual([CaptureRig.Headset]), $"{context}: Fabrik {rig.Factory.Calls}-mal aufgerufen.");
        if (backend is not null)
        {
            var started = trigger != Trigger.StartThrows;
            var sessionStops = trigger is Trigger.NoSignal or Trigger.QueueFull or Trigger.Discontinuity or Trigger.Requested
                || moment == Moment.AfterStop;
            Assert.True(backend.StartCalls == 1, $"{context}: {backend.StartCalls} Starts.");
            Assert.False(backend.IsRunning, $"{context}: Mikrofon noch offen.");
            Assert.True(backend.StopTransitions == (started ? 1 : 0), $"{context}: {backend.StopTransitions} Stopps.");
            Assert.True(
                sessionStops ? backend.StopCalls == 1 : backend.StopCalls <= 1,
                $"{context}: {backend.StopCalls} Stop-Aufrufe.");
            Assert.True(backend.DisposeCalls == 1, $"{context}: {backend.DisposeCalls}-mal freigegeben.");
        }

        // Audio: alles Umgewandelte samt Nachlauf, nichts aufgefüllt; Unread gleich dem Ungelesenen
        var reference = rig.Reference();
        var converted = ReferenceConversion.ConvertedFrames(
            rig.Format, rig.Factory.Backends.SelectMany(fake => fake.Delivered));
        var nominal = converted * SampleRate / rig.Format.SampleRate;
        Assert.True(
            Math.Abs(TotalSamples(reference) - nominal) <= CaptureConverterTests.SampleTolerance,
            $"{context}: {TotalSamples(reference)} Samples aus {converted} Geräteframes.");
        if (trigger == Trigger.Discontinuity)
        {
            Assert.True(converted < rig.Factory.FramesDelivered, $"{context}: Puffer nach dem Aussetzer wurde gezählt.");
        }

        if (read is not null)
        {
            AssertSameAudio(reference, read, context);
            if (read.Count > 0)
            {
                AssertBlocks(read, context);
            }

            Assert.True(status.Unread == TimeSpan.Zero, $"{context}: Unread {status.Unread} nach vollständigem Lesen.");
        }
        else
        {
            Assert.True(
                status.Unread == Duration(TotalSamples(reference)),
                $"{context}: Unread {status.Unread} statt {Duration(TotalSamples(reference))}.");
        }

        await CaptureFailureWatch.AssertNoneAsync();
    }
}
