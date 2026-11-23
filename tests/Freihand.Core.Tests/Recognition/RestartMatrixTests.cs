using Freihand.Adapters.Recognition;
using Freihand.Core.Tests.Live;
using Freihand.Core.Tests.Recognition.Kit;
using static Freihand.Core.Tests.Recognition.Kit.RecognitionTestKit;

namespace Freihand.Core.Tests.Recognition;

/// <summary>
/// Wiederanlauf des Supervisors als Matrix mit ausdrücklichem Abdeckungsinventar (P8-Konzept, Kapitel 5.4, BE-18):
/// Ereignisart × Zeitmuster → sofortiger Neustart oder <c>Unavailable</c>, Zahl der Starts, Kill des noch laufenden
/// Erkenners, kein Start nach <c>Unavailable</c> bis <c>RestartAsync</c>; bleibende Fehlercodes × Zeitpunkt →
/// <c>Unavailable</c> ohne Neustart mit <c>LastError</c>.
/// <para><b>Auslegung „innerhalb von 5 Minuten“:</b> Das vierte Ereignis setzt <c>Unavailable</c>, wenn seit dem ersten
/// der letzten vier Ereignisse <b>höchstens</b> 5 Minuten vergangen sind (Grenze eingeschlossen, gleitendes Fenster);
/// genau 5:00,0000000 zählt also noch als innerhalb, 5 Minuten plus ein Tick nicht mehr. Gemessen wird mit der Uhr des
/// Supervisors zum Zeitpunkt, an dem er das Ereignis bemerkt (bei Zeitgrenzen: deren Ablauf).</para>
/// <para><b>Ereignisse:</b> Prozessende (Absturz, ohne Kill), harter Abbruch der Verbindung <c>decode</c> bzw.
/// <c>detect</c> (ohne laufende Anfrage, der Prozess läuft weiter), Ablauf der Decode-Zeitgrenze (35 s bei 1 s Audio)
/// bzw. der Detect-Zeitgrenze (2 s). Bei allen ausser dem Prozessende läuft der Erkenner noch; der Supervisor muss ihn
/// beenden (Kill), auch beim vierten Ereignis, das <c>Unavailable</c> setzt („beendet den Prozess, falls er noch
/// läuft“). Nach <c>Unavailable</c> läuft die Uhr 10 Minuten weiter, ohne dass ein Start folgt; erst
/// <c>RestartAsync</c> startet wieder.</para>
/// <para><b>Rotnachweis:</b> Fehlt der Neustart nach einem Ereignis (Kapitel 5.4, Wiederanlauf), werden alle Fälle von
/// <see cref="TransientEventsRestartUntilTheFourthWithinFiveMinutes"/> rot; fehlt nur die Erkennung einer Ereignisart
/// (Prozessende, Abbruch je Kanal, Zeitgrenze je Kanal), genau deren Zeilen. Fehlt die Grenze „viertes Ereignis innerhalb
/// von 5 Minuten“, werden <c>FourthJustInside</c>, <c>FourthAtExactlyFive</c> und <c>SlidingWindow</c> rot; zählt die
/// Umsetzung ohne Fenster (alle Ereignisse seit dem Start), <c>FourthJustAfter</c>, <c>SixMinutesApart</c> und
/// <c>SlidingWindow</c>; liest sie die Grenze ausschliessend (unter 5 Minuten), <c>FourthAtExactlyFive</c> und
/// <c>SlidingWindow</c>. Fehlt der Kill eines noch laufenden Erkenners, werden alle Zeilen ausser <c>ProcessExit</c> rot.
/// Fehlt die Sonderbehandlung bleibender Fehler (Kapitel 5.4, Bleibender Fehler), werden alle Fälle von
/// <see cref="PermanentErrorsAreUnavailableWithoutRestart"/> rot; fehlt nur ein Code, dessen Zeilen.</para>
/// <para><b>Nicht enthalten, und warum:</b> bleibende Fehler × Zeitmuster (sie wirken beim ersten Auftreten, ein
/// Zeitmuster ändert nichts; geprüft sind „erster Start“ und „Neustart nach einem Absturz“); <c>failed</c> mit
/// <c>VadError</c> als Ereignis: Er zählt nach Kapitel 10.4 zum Fenster, geprüft mit vier Fehlern bis
/// <c>Unavailable</c>, <c>LastError</c> null und Neustart von Hand in der Codex-Gegenprobe C04
/// (<see cref="P8ReviewCounterTests"/>); fremde Prozesskennung (nur protokolliert, R30).
/// Gemischte Ereignisarten prüft <see cref="MixedEventsShareOneWindow"/>.</para>
/// </summary>
public sealed class RestartMatrixTests
{
    public enum Event
    {
        ProcessExit,
        DecodeAbort,
        DetectAbort,
        DecodeTimeout,
        DetectTimeout,
    }

    public enum Pattern
    {
        /// <summary>0, 2, 4 min: drei Neustarts.</summary>
        ThreeWithinFourMinutes,

        /// <summary>0, 1, 2 min, 5 min − 1 Tick: das vierte setzt Unavailable.</summary>
        FourthJustInside,

        /// <summary>0, 1, 2, 5 min: Grenze eingeschlossen, das vierte setzt Unavailable.</summary>
        FourthAtExactlyFive,

        /// <summary>0, 1, 2 min, 5 min + 1 Tick: das vierte startet neu.</summary>
        FourthJustAfter,

        /// <summary>0, 6, 12, 18, 24 min: immer Neustart.</summary>
        SixMinutesApart,

        /// <summary>0, 1, 2 min, 5 min + 1 Tick, 6 min: das vierte startet neu, das fünfte (1 bis 6 min) setzt Unavailable.</summary>
        SlidingWindow,
    }

    public enum When
    {
        /// <summary>Schon der erste Start scheitert.</summary>
        FirstStart,

        /// <summary>Der Neustart nach einem Absturz scheitert.</summary>
        AfterCrash,
    }

    public static TheoryData<Event, Pattern> TransientCases()
    {
        var cases = new TheoryData<Event, Pattern>();
        foreach (var kind in Enum.GetValues<Event>())
        {
            foreach (var pattern in Enum.GetValues<Pattern>())
            {
                cases.Add(kind, pattern);
            }
        }

        return cases;
    }

    public static TheoryData<RecognizerErrorCode, When> PermanentCases()
    {
        var cases = new TheoryData<RecognizerErrorCode, When>();
        foreach (var code in new[]
        {
            RecognizerErrorCode.ModelMissing, RecognizerErrorCode.ModelChecksum, RecognizerErrorCode.RuntimeUnavailable,
            RecognizerErrorCode.InvalidConfiguration, RecognizerErrorCode.ProtocolVersion,
        })
        {
            foreach (var when in Enum.GetValues<When>())
            {
                cases.Add(code, when);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(TransientCases))]
    public async Task TransientEventsRestartUntilTheFourthWithinFiveMinutes(Event kind, Pattern pattern)
    {
        var (offsets, unavailableAtLast) = Timeline(pattern);
        await RunAsync(offsets.Select(offset => (kind, offset)).ToList(), unavailableAtLast);
    }

    /// <summary>Alle Ereignisarten zählen in dasselbe Fenster.</summary>
    [Fact]
    public async Task MixedEventsShareOneWindow()
    {
        await RunAsync(
            [
                (Event.ProcessExit, TimeSpan.Zero),
                (Event.DecodeAbort, TimeSpan.FromMinutes(1)),
                (Event.DetectTimeout, TimeSpan.FromMinutes(2)),
                (Event.DecodeTimeout, TimeSpan.FromMinutes(4)),
            ],
            unavailableAtLast: true);
    }

    [Theory]
    [MemberData(nameof(PermanentCases))]
    public async Task PermanentErrorsAreUnavailableWithoutRestart(RecognizerErrorCode code, When when)
    {
        var faultyLaunch = when == When.FirstStart ? 1 : 2;
        var broken = true;
        await using var harness = SupervisorHarness.Create(number =>
        {
            if (!broken || number != faultyLaunch)
            {
                return LaunchPlan.Service();
            }

            return code switch
            {
                RecognizerErrorCode.RuntimeUnavailable or RecognizerErrorCode.InvalidConfiguration =>
                    LaunchPlan.Service(loader => loader.Throws = code),
                RecognizerErrorCode.ProtocolVersion => LaunchPlan.Scripted(async scripted =>
                {
                    var decode = scripted.Connect();
                    await decode.SendAsync(Messages.Hello(2, "decode"));
                    await decode.ReadToEndOrBreakAsync();
                }),
                _ => LaunchPlan.Service(),
            };
        });

        void BreakFiles()
        {
            if (code == RecognizerErrorCode.ModelMissing)
            {
                harness.Files.DeleteModel();
            }
            else if (code == RecognizerErrorCode.ModelChecksum)
            {
                harness.Files.CorruptModelKeepingMetadata();
            }
        }

        if (when == When.FirstStart)
        {
            BreakFiles();
            await harness.StartAsync();
        }
        else
        {
            await harness.StartAsync();
            await harness.WaitReadyAsync(1);
            BreakFiles();
            harness.Launcher.Current.Crash();
        }

        await harness.WaitAvailabilityAsync(RecognizerAvailability.Unavailable);
        Assert.Equal(code, harness.Supervisor.LastError);
        Assert.Equal(faultyLaunch, harness.Launcher.Launches);
        if (code is RecognizerErrorCode.ModelMissing or RecognizerErrorCode.ModelChecksum)
        {
            Assert.Empty(harness.Launcher.Current.Loader.Calls);
        }

        await AssertNoStartUntilRestartAsync(harness, faultyLaunch);
        Assert.Equal(when == When.AfterCrash ? 1 : 0, harness.Transitions.Count(state => state == RecognizerAvailability.Restarting));

        // Ursache behoben: erst RestartAsync startet wieder
        broken = false;
        harness.Files.RestoreModel();
        await harness.Supervisor.RestartAsync().WaitAsync(Guard);
        await harness.WaitReadyAsync(faultyLaunch + 1);
        Assert.Null(harness.Supervisor.LastError);
        Assert.False(await UnobservedFailureLog.SeenAsync(harness.Launcher.Marker), $"Unbeobachteter Fehler mit Kennung {harness.Launcher.Marker}.");
    }

    private static (TimeSpan[] Offsets, bool UnavailableAtLast) Timeline(Pattern pattern)
    {
        static TimeSpan Min(double minutes) => TimeSpan.FromMinutes(minutes);
        return pattern switch
        {
            Pattern.ThreeWithinFourMinutes => ([Min(0), Min(2), Min(4)], false),
            Pattern.FourthJustInside => ([Min(0), Min(1), Min(2), Min(5) - Tick], true),
            Pattern.FourthAtExactlyFive => ([Min(0), Min(1), Min(2), Min(5)], true),
            Pattern.FourthJustAfter => ([Min(0), Min(1), Min(2), Min(5) + Tick], false),
            Pattern.SixMinutesApart => ([Min(0), Min(6), Min(12), Min(18), Min(24)], false),
            Pattern.SlidingWindow => ([Min(0), Min(1), Min(2), Min(5) + Tick, Min(6)], true),
            _ => throw new ArgumentOutOfRangeException(nameof(pattern)),
        };
    }

    /// <summary>
    /// Spielt die Ereignisse ab: das erste bei 1 min Uhrzeit, jedes weitere um seinen Abstand später. Vor jedem Ereignis
    /// ist der Supervisor <c>Ready</c> mit genau einem Start mehr als Ereignisse bisher; nach jedem ausser einem
    /// abschliessenden <c>Unavailable</c> startet er sofort neu (ohne Uhrvorlauf).
    /// </summary>
    private static async Task RunAsync(IReadOnlyList<(Event Kind, TimeSpan Offset)> events, bool unavailableAtLast)
    {
        await using var harness = await SupervisorHarness.StartedAsync();
        var first = TimeSpan.FromMinutes(1);
        for (var index = 0; index < events.Count; index++)
        {
            var (kind, offset) = events[index];
            var last = index == events.Count - 1;
            await harness.WaitReadyAsync(index + 1);
            var process = harness.Launcher.Current;
            await TriggerAsync(harness, process, kind, first + offset);

            if (kind != Event.ProcessExit)
            {
                await process.Killed.WaitAsync(Guard);
            }

            if (last && unavailableAtLast)
            {
                await harness.WaitAvailabilityAsync(RecognizerAvailability.Unavailable);
                Assert.Equal(index + 1, harness.Launcher.Launches);
                await AssertNoStartUntilRestartAsync(harness, index + 1);
                await harness.Supervisor.RestartAsync().WaitAsync(Guard);
                await harness.WaitReadyAsync(index + 2);
                return;
            }

            // sofortiger Neustart: ohne die Uhr vorzustellen
            await harness.WaitReadyAsync(index + 2);
            Assert.Null(harness.Supervisor.LastError);
        }

        Assert.Equal(events.Count + 1, harness.Launcher.Launches);
        Assert.Contains(RecognizerAvailability.Restarting, harness.Transitions);
        Assert.False(await UnobservedFailureLog.SeenAsync(harness.Launcher.Marker), $"Unbeobachteter Fehler mit Kennung {harness.Launcher.Marker}.");
    }

    /// <summary>Löst das Ereignis so aus, dass der Supervisor es genau zur Uhrzeit <paramref name="at"/> bemerkt.</summary>
    private static async Task TriggerAsync(SupervisorHarness harness, FakeRecognizerProcess process, Event kind, TimeSpan at)
    {
        var lead = kind switch
        {
            Event.DecodeTimeout => harness.Timeouts.DecodeBase + TimeSpan.FromSeconds(5),
            Event.DetectTimeout => harness.Timeouts.Detect,
            _ => TimeSpan.Zero,
        };
        var start = at - lead;
        Assert.True(start >= harness.Clock.Elapsed, "Zeitmuster lässt keinen Raum für die Zeitgrenze.");
        harness.Clock.Advance(start - harness.Clock.Elapsed);

        switch (kind)
        {
            case Event.ProcessExit:
                process.Crash();
                return;
            case Event.DecodeAbort:
                process.SeverDecode();
                return;
            case Event.DetectAbort:
                process.SeverDetect();
                return;
        }

        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await harness.SessionAsync();
        Task call;
        if (kind == Event.DecodeTimeout)
        {
            process.Whisper.Behavior = process.Whisper.BlocksUntil(never.Task);
            call = session.Decoder.DecodeAsync(Samples(16_000), Sec(1), German());
            await process.Whisper.StartedAsync(1);
        }
        else
        {
            process.Vad.Behavior = process.Vad.BlocksUntil(never.Task);
            call = DetectAsync(session.Detector, Samples(16_000), Sec(1));
            await process.Vad.StartedAsync(1);
        }

        await harness.Clock.WaitForTimersAsync(1, lead);
        harness.Clock.Advance(lead);
        Assert.Equal(at, harness.Clock.Elapsed);
        await Throws<RecognizerUnavailableException>(call);
    }

    /// <summary>Nach <c>Unavailable</c>: 10 Minuten ohne Start, Sitzungen werden sofort abgewiesen.</summary>
    private static async Task AssertNoStartUntilRestartAsync(SupervisorHarness harness, int launches)
    {
        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        await Settle();
        Assert.Equal(launches, harness.Launcher.Launches);
        Assert.Equal(RecognizerAvailability.Unavailable, harness.Supervisor.Availability);
        await Throws<RecognizerUnavailableException>(harness.Supervisor.StartSessionAsync(German(), LiveDefaults()));
    }
}
