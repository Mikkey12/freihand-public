using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Freihand.Adapters.Dictation;
using Freihand.Adapters.Recognition;
using Freihand.Core.Commands;
using Freihand.Core.Formatting;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;
using Freihand.Core.Recognition;
using Freihand.Core.Recovery;
using Freihand.Windows;
using Freihand.Windows.Audio;
using Freihand.Windows.Recognition;
using Freihand.Windows.Beta;
using Freihand.Windows.Recovery;

namespace Freihand.App;

/// <summary>
/// Zusammenbau des Durchstichs: Einstellungen, Erkenner, Windows-Teile,
/// Aufnahme und Steuerung; das Statusfenster zeigt nur, was die Steuerung meldet. Beenden gibt alles frei.
/// </summary>
/// <remarks>
/// Beenden (Kapitel 6, U35-08): Das Schliessen des Fensters wird einmal abgefangen; der Start läuft zu Ende, dann werden
/// Steuerung, Windows-Teile und Erkenner auf dem Threadpool in dieser Reihenfolge freigegeben, je höchstens 5 s, und erst
/// danach beendet <c>Shutdown</c> die App. Der UI-Thread wartet dabei nie synchron.
/// <para>
/// Regel für jedes Ende, nach dem der Prozess sofort enden darf (Codex-Prüfung PR #58): Vor der Rückkehr sind die
/// offenen Entwürfe gesichert. Beim bestätigten Sitzungsende (WM_ENDSESSION) läuft zudem der ganze Schliesspfad vor der
/// Rückkehr, mit Gesamtfrist für die Freigabe; nur dort wartet der UI-Thread, weil Windows auf diese Antwort wartet.
/// </para>
/// <para>
/// Diktatfenster mit Verlustschutz: Die Sicherung wird vor dem Erkenner geöffnet und
/// durchgesehen, damit gerettete Texte auch ohne Modell angeboten werden (BF-12). Freihand lebt im Infobereich; das
/// Statusfenster ist die Diagnoseansicht, und sein Schliessen blendet es nur aus. Beendet wird über das Menü; die
/// Sicherung wird zuletzt freigegeben, nach der Schreibfläche, damit nichts Ungesichertes bleibt.
/// </para>
/// </remarks>
public partial class App : Application
{
    private static readonly TimeSpan ReleaseLimit = TimeSpan.FromSeconds(5);

    /// <summary>Eigener DPAPI-Zusatzwert der Prüfaufnahmen, getrennt von der Wiederherstellung (P11-E1).</summary>
    private const string ReviewEntropy = "Freihand.Pruefaufnahmen.v1";

    /// <summary>
    /// Gesamtfrist der Freigabe beim Sitzungsende. Antwortet ein Programm nicht innert rund 5 s, zeigt Windows es als
    /// Programm, das das Herunterfahren aufhält; mit 2 s bleibt Platz fürs Sichern danach. Üblich sind Millisekunden.
    /// </summary>
    private static readonly TimeSpan SessionEndLimit = TimeSpan.FromSeconds(2);

    private RecognizerSupervisor? _recognizer;
    private WindowsTargets? _targets;
    private DictationController? _controller;
    private RecoveryStore? _recovery;
    private Shell? _shell;
    private InstanceSignal? _instance;
    private DailyLog? _log;
    private ReviewRecorder? _review;
    private Task _startup = Task.CompletedTask;
    private Task? _closing;
    private bool _closed;

    /// <summary>Schutz vor dem Beenden auf blosse Rückfrage von Windows (Befund vom 16.11.2026); null ohne Schutz.</summary>
    private SessionEndGuard? _sessionGuard;

    /// <summary>„freihand ende“ steht schon im Protokoll (Sitzungsende), OnExit schreibt es nicht nochmals.</summary>
    private bool _ended;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Zwischenablage ohne Verlauf und Cloud schon für das erste Fenster mit Textfeld; das Profil zieht StartAsync nach
        PrivateClipboard.Register(copyAllowed: true);

        // Beta B1: „--beenden“ bittet ein laufendes Freihand, sich regulär zu beenden (Update-Skript); nur eines je Sitzung
        if (e.Args.Contains("--beenden", StringComparer.OrdinalIgnoreCase))
        {
            InstanceSignal.RequestExit();
            Shutdown();
            return;
        }

        _instance = InstanceSignal.TryClaim();
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        // Erst schliessen, wenn Windows die Sitzung wirklich beendet, nicht schon auf die Rückfrage (SessionEndGuard)
        // Kündigt Windows das Ende an, startet kein Erkenner mehr; bricht es ab, läuft ein vorgemerkter Neustart (18.11.2026)
        _sessionGuard = SessionEndGuard.Attach(
            SessionEndGuard.ApplicationWindow(this), EndForSession, () => _recognizer?.HoldRestarts(), () => _recognizer?.ResumeRestarts());

        // Die Diagnoseansicht erscheint nur auf Wunsch (Menü des Infobereichs)
        var window = new MainWindow();
        MainWindow = window;
        window.Closing += OnClosing;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Freihand");
        var shell = new Shell(window, folder);
        _shell = shell;
        shell.ExitRequested += (_, _) => _closing ??= CloseAsync();
        _instance.ExitRequested += (_, _) => Dispatcher.BeginInvoke(() => _closing ??= CloseAsync());
        _startup = StartAsync(window, shell, folder);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Ohne den Schliesspfad (z. B. Rückfall ohne Schutz fürs Sitzungsende) nur anstossen, nie im UI-Thread auf die
        // Freigabe warten (C07); Fehler beobachtet. Nach der Rückkehr darf der Prozess enden: darum Schreibfläche
        // schliessen und die offenen Entwürfe sofort sichern (nur lokale Dateien, Codex-Prüfung PR #58). Die Ablage
        // bleibt bis nach der Freigabe offen, damit sie den letzten Stand der Steuerung noch aufnimmt (AU-12).
        if (!_ended)
        {
            _ended = true;
            _log?.Write("freihand ende");
        }

        _sessionGuard?.Dispose();
        _shell?.Dispose();
        var recovery = Interlocked.Exchange(ref _recovery, null);
        recovery?.Flush();
        _ = ReleaseAllAsync().ContinueWith(_ => recovery?.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        Interlocked.Exchange(ref _instance, null)?.Dispose();
        Interlocked.Exchange(ref _review, null)?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Windows beendet die Sitzung tatsächlich (WM_ENDSESSION mit „wahr“) und darf den Prozess nach der Rückkehr jederzeit
    /// beenden. Darum hier, vor der Rückkehr, der ganze Schliesspfad in seiner Reihenfolge: Steuerung, Windows-Teile und
    /// Erkenner freigeben, Schreibfläche schliessen, Entwürfe ein letztes Mal sichern (Codex-Prüfung PR #58). Auf die
    /// Freigabe wartet der UI-Thread höchstens <see cref="SessionEndLimit"/>; die Sicherung kommt danach in jedem Fall.
    /// </summary>
    private void EndForSession()
    {
        _ended = true;
        _log?.Write("freihand ende sitzungsende");
        try
        {
            ReleaseAllAsync().Wait(SessionEndLimit);
        }
        catch (AggregateException)
        {
            // ReleaseAsync fängt die Fehler je Schritt; was hier noch ankommt, darf das Sichern nicht aufhalten
        }

        _shell?.Dispose();
        Interlocked.Exchange(ref _recovery, null)?.Dispose();
        Interlocked.Exchange(ref _instance, null)?.Dispose();
        _closed = true;
        Shutdown();
    }

    /// <summary>Das Statusfenster ist Diagnoseansicht: Schliessen blendet es nur aus; beendet wird über den Infobereich.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        e.Cancel = true;
        if (sender is MainWindow window)
        {
            window.Hide();
        }
    }

    /// <summary>Einmaliger asynchroner Schliesspfad vor dem endgültigen <c>Shutdown</c>.</summary>
    private async Task CloseAsync()
    {
        await _startup;
        await ReleaseAllAsync();
        _ended = true;
        _log?.Write("freihand ende");

        // Erst die Schreibfläche sichern und die Fenster schliessen, dann die Sicherung freigeben (letztes Sichern)
        _shell?.Dispose();
        Interlocked.Exchange(ref _recovery, null)?.Dispose();
        Interlocked.Exchange(ref _instance, null)?.Dispose();
        _closed = true;
        Shutdown();
    }

    private async Task StartAsync(MainWindow window, Shell shell, string folder)
    {
        void Startup(string text)
        {
            window.ShowStartup(text);
            shell.ShowStartup(text);
        }

        try
        {
            // Beta B4/B5: Tagesprotokoll ohne Text, 14 Tage aufbewahrt; Marken mit dem letzten Zustand der Steuerung. Vor
            // allem anderen, damit jeder Startfehler, auch beim Lesen der Einstellungen, im Protokoll steht (Codex zu PR #69)
            var log = new DailyLog(Path.Combine(folder, "Protokoll"), TimeProvider.System);
            _log = log;
            log.DeleteOld();

            var settingsPath = Path.Combine(folder, "einstellungen.txt");
            var settings = Edition.Effective(PrototypeSettings.LoadOrCreate(settingsPath, Edition.Defaults(folder)), folder);
            var profile = BuiltInProfiles.Standard;
            log.Write("freihand start ausgabe=" + (Edition.Business ? "geschaeft" : "privat")
                + " sitzungsende=" + (_sessionGuard is null ? "ungeschuetzt" : "geschuetzt")
                + " modell=" + Path.GetFileName(settings.Model));
            shell.AttachProtocol(log, new ProblemMarker(log.Write, () => _controller?.Current.LogLine()));

            // Nach einem abgebrochenen Herunterfahren mit Standby kommt nicht immer ein WM_ENDSESSION: Aufwachen gibt frei
            shell.Resumed += (_, _) => _recognizer?.ResumeRestarts();

            // Verlustschutz vor allem anderen: gerettete Texte auch ohne Erkenner anbieten (BF-12, AU-12, D04)
            _recovery = new RecoveryStore(
                new PhysicalRecoveryFileSystem(Path.Combine(folder, "Wiederherstellung")), new WindowsDataProtector(), TimeProvider.System,
                new RecoveryOptions { Enabled = settings.Recovery, HoldPeriod = TimeSpan.FromMinutes(settings.HoldMinutes) });
            shell.AttachRecovery(_recovery, _recovery.Scan(), copyAllowed: profile.Strictness != ProfileStrictness.Strict);

            // Jedes Kopieren aus Textfeldern ohne Verlauf und Cloud, im strengen Profil gesperrt (wie die Knöpfe)
            PrivateClipboard.Register(copyAllowed: profile.Strictness != ProfileStrictness.Strict);

            // Prüfaufnahmen nur in der privaten Ausgabe (P11-E1); nach Ablauf löscht schon der Start alles
            var review = Edition.Business
                ? null
                : new ReviewRecorder(Path.Combine(folder, "Pruefaufnahmen"), new WindowsDataProtector(ReviewEntropy), TimeProvider.System);
            _review = review;
            if (review is not null)
            {
                review.Cleanup();
                shell.AttachReview(review);
            }

            // Beta: Menü, Schlüssel, Korrigieren und Autostart auch ohne Modell und Erkenner
            shell.ConfigureBeta(settings, settingsPath);

            // Wort lernen (P11 S4): nur Ersetzungen nach der Erkennung, lesbar in woerter.txt, auch in der Geschäftsausgabe
            var words = new WordStore(Path.Combine(folder, "woerter.txt"), log.Write);
            shell.AttachWords(words, null, BuiltInCommands.Pilot, null);

            if (!File.Exists(settings.Model) || !File.Exists(settings.Vad))
            {
                Startup(Edition.ModelMissing(settings, settingsPath, folder));
                return;
            }

            // Sollwerte aus dem Inventar, nicht aus der vorhandenen Datei; der Erkenner prüft die Datei dagegen (P8 Kapitel 5.4)
            if (KnownModels.Expected(settings.Model) is not { } modelHash || KnownModels.Expected(settings.Vad) is not { } vadHash)
            {
                Startup($"Modell oder VAD nicht freigegeben; erlaubt sind {string.Join(", ", KnownModels.Sha256.Keys)} (Inventar).");
                return;
            }

            // Nur bekannte Laufzeiten; ein Tippfehler fällt nicht still auf den Prozessor zurück
            var runtimes = settings.Runtimes.Select(Runtime).ToList();
            if (runtimes.Count == 0 || runtimes.Any(runtime => runtime is null))
            {
                Startup($"Laufzeiten in {settingsPath} prüfen: erlaubt sind cuda, vulkan, cpu.");
                return;
            }

            var configuration = new RecognizerConfiguration(
                settings.Model, modelHash, [.. runtimes.Select(runtime => runtime!.Value)], settings.Threads, settings.AudioContext,
                settings.Vad, vadHash, VadSettings.Default);
            _recognizer = new RecognizerSupervisor(
                RecognizerLauncher.ForExecutable(Path.Combine(AppContext.BaseDirectory, "Freihand.Recognizer.exe")),
                configuration, ClientTimeouts.Default, TimeProvider.System);
            _recognizer.Start();

            // Feld messen (21.11.2026): selbst gemessene Felder gelten vor den festen Zeilen und lassen sich ohne Neustart ergänzen
            var measuredFields = new MeasuredFieldStore(Path.Combine(folder, MeasuredFieldStore.FileName));
            var targetTable = new TargetRuleTable(MeasuredTargets);

            // Lädt die Liste; ist sie ungültig oder nicht lesbar, gelten die festen Zeilen und ein Hinweis nennt den Grund
            shell.AttachFieldMeasurement(measuredFields, targetTable);
            _targets = new WindowsTargets(
                targetTable,
                new OutputOptions(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(10), ControlKeySafety.NotApproved)
                {
                    // Nach einer Unterbrechung (z. B. Windows-Meldung) weiterschreiben, sobald der Fokus ins Feld zurückkehrt
                    FocusReturnLimit = TimeSpan.FromSeconds(60),
                });
            // Keine Hinweise an Whisper (P11 S4, Messung F): sie erfinden die Hinweiswörter in Geräuschen
            var recognition = new RecognitionOptions(settings.Language, []);
            var interpreting = new InterpreterOptions(null, TimeSpan.FromSeconds(5), new Dictionary<string, string>(),
                new SwissTextFormatter(SwissFormatOptions.Default, RegionalFormat.SwissDefault));
            var factory = new RecognizerDictationFactory(
                _recognizer, profile, settings.Device, LiveApprovals.None, recognition, Live(), BuiltInCommands.Pilot, interpreting)
            {
                Replacements = words.Load,
            };

            // Lernen hört mit derselben Kette, aber ohne Ersetzungen und ohne Formatter, und teilt das Mikrofon mit dem Diktat
            var audio = WasapiAudio.Create();
            var learning = factory.ForLearning();
            shell.AttachWords(words, new WordListener(learning, audio), BuiltInCommands.Pilot, learning.InterpreterOptions);
            var gestures = Edition.Gestures(settings, out var invalid, out var duplicates);
            _controller = new DictationController(
                _recognizer, factory, audio, _targets.Binding, _targets.Output, _targets.Guard, _targets.Hotkeys,
                new WpfClipboard(Dispatcher),
                new ControllerOptions(profile.Strictness, settings.Device, gestures) { SpaceAfterDictation = true },
#if GESCHAEFT
                // Sicherheitsprüfung, S1: in der Geschäftsausgabe nur das Tagesprotokoll; FREIHAND_MESSPROTOKOLL wirkt nicht
                line =>
                {
                    Debug.WriteLine(line);
                    log.Write(line);
                },
#else
                MeasurementLog.Create(System.Environment.GetEnvironmentVariable, line =>
                {
                    Debug.WriteLine(line);
                    log.Write(line);
                }),
#endif
                _recovery, shell.Pad, _targets.Marker, _targets.ForeignContent, review);
            _controller.Updated += (_, update) => Dispatcher.BeginInvoke(() =>
            {
                window.Show(update);
                shell.Show(update);
            });
            window.CopyHeld = id => _controller.CopyHeld(id);
            shell.Connect(_controller, settings, settingsPath);
            var conflicts = _controller.RegisterHotkeys();

            // Ungültige und doppelte Belegung wird gemeldet, nie still übernommen (D10)
            var problems = invalid.Select(action => $"{action} nicht lesbar")
                .Concat(duplicates.Select(duplicate => $"{duplicate.Action} wie {duplicate.SameAs}"))
                .Concat(conflicts.Where(entry => entry.Value != HotkeyConflict.None).Select(entry => $"{entry.Key} ({entry.Value})"))
                .ToList();
            window.ShowKeys(
                $"Aufnahme {settings.Hotkeys.Record} · Befehl {settings.Hotkeys.Command} · Abbrechen {settings.Hotkeys.Cancel}"
                + $" · Diktatfenster {settings.Hotkeys.DictationWindow}"
                + (Edition.Business ? string.Empty : $" · Korrigieren {settings.Hotkeys.Correct} · Glätten {settings.Hotkeys.Smooth}")
                + $" · Problem {settings.Hotkeys.Mark} · Ausgabe {Edition.Name}"
                + (problems.Count > 0 ? " · nicht angemeldet: " + string.Join(", ", problems) : string.Empty));
            window.Show(_controller.Current);
            shell.Show(_controller.Current);
            if (problems.Count > 0)
            {
                shell.ShowStartup("Taste nicht angemeldet: " + string.Join(", ", problems));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Erst alles bereits Gestartete freigeben, dann melden; nur der Typ, nie Inhalte (SI-01, U35-07). Ins Protokoll
            // zusätzlich die Stellen im Programm, damit ein Startfehler auch auf dem Zielgerät eingrenzbar ist
            _log?.Write("freihand start_gescheitert " + StartFailure.Describe(exception));
            await ReleaseAllAsync();
            Startup($"Start gescheitert: {exception.GetType().Name}");
        }
    }

    /// <summary>
    /// Gibt in umgekehrter Startreihenfolge frei, jeden Schritt trotz Fehlern der anderen, auf dem Threadpool. Die Felder
    /// werden dabei übernommen, so gibt kein zweiter Aufruf etwas doppelt frei (U35-07).
    /// </summary>
    private Task ReleaseAllAsync()
    {
        var controller = Interlocked.Exchange(ref _controller, null);
        var targets = Interlocked.Exchange(ref _targets, null);
        var recognizer = Interlocked.Exchange(ref _recognizer, null);
        return Task.Run(async () =>
        {
            if (controller is not null)
            {
                await ReleaseAsync(controller.DisposeAsync);
            }

            if (targets is not null)
            {
                await ReleaseAsync(() =>
                {
                    targets.Dispose();
                    return ValueTask.CompletedTask;
                });
            }

            if (recognizer is not null)
            {
                await ReleaseAsync(recognizer.DisposeAsync);
            }
        });
    }

    /// <summary>
    /// Zieltabelle mit gemessenen Zeilen (WM03/WM04): die grösste Teilgrösse, bei
    /// der zwei Durchgänge mit 2000 Zeichen zeichengetreu ankamen, ebenso alle kleineren. Der neue Editor von Windows 11
    /// verliert ab 4 Zeichen je Teil Zeichen, darum dort 1. Teams hat keine eigene Zeile: Seine RuntimeId wechselte in einem
    /// Durchgang. Alles andere gilt als unbekanntes Feld mit 1 Zeichen je Teil.
    /// <para>
    /// Umbrüche nach WM05: Word und der Nachrichtentext von Outlook (ein Word-Editor,
    /// auch im Format Nur-Text) nehmen U+000D ohne echte Taste als Umbruch an. Im Editor ginge es nur mit echter
    /// Umschalt+Enter; auf Produktregel vom 11.11.2026 bleibt er ohne echte Tasten. Edge und Chrome melden
    /// nicht, ob ein Webfeld einzeilig ist; ein Umbruch könnte ein Formular abschicken, darum dort weiter verweigert.
    /// </para>
    /// </summary>
    private static readonly TargetRule[] MeasuredTargets =
    [
        new("Notepad", "Document", "RichEditD2DPT", "Win32", null, FieldKinds.DocumentBody, "Editor – Dokumenttext",
            LineBreakMode.Refuse, 1, IdentityMode.RuntimeId),
        new("WINWORD", "Document", "_WwG", "Win32", null, FieldKinds.DocumentBody, "Word – Dokumenttext",
            LineBreakMode.UnicodeReturn, 20, IdentityMode.RuntimeId),
        // Nur mit 1 Zeichen je Teil gemessen (WM05); längere Teile sind für diesen Editor nicht nachgewiesen
        new("OUTLOOK", "Document", "_WwG", "Win32", null, FieldKinds.MessageBody, "Outlook – Nachrichtentext",
            LineBreakMode.UnicodeReturn, 1, IdentityMode.RuntimeId),
        new("OUTLOOK", "Edit", "RichEdit20WPT", "Win32", null, FieldKinds.MultiLine, "Outlook – Textfeld",
            LineBreakMode.Refuse, 20, IdentityMode.RuntimeId),
        // Die Adressleiste ist nicht mit 20 gemessen; die erste passende Zeile gilt, darum vor den Textfeldern
        new("msedge", "Edit", "OmniboxViewViews", "Chrome", null, FieldKinds.SingleLine, "Edge – Adressleiste",
            LineBreakMode.Refuse, 1, IdentityMode.RuntimeId),
        new("chrome", "Edit", "OmniboxViewViews", "Chrome", null, FieldKinds.SingleLine, "Chrome – Adressleiste",
            LineBreakMode.Refuse, 1, IdentityMode.RuntimeId),
        new("msedge", "Edit", null, "Chrome", null, FieldKinds.MultiLine, "Edge – Textfeld",
            LineBreakMode.Refuse, 20, IdentityMode.RuntimeId),
        new("chrome", "Edit", null, "Chrome", null, FieldKinds.MultiLine, "Chrome – Textfeld",
            LineBreakMode.Refuse, 20, IdentityMode.RuntimeId),
        // Claude-App (Electron), gemessen am 17.11.2026 (Nachtrag im Bericht p7-zielprobe): 20 Zeichen je Teil zeichengetreu.
        // Die Klasse wechselt mit dem Fokus („ProseMirror-focused“), darum ohne Klasse; Enter schickt ab, darum keine Umbrüche
        new("claude", "Edit", null, "Chrome", null, FieldKinds.ChatInput, "Claude-App – Eingabe",
            LineBreakMode.Refuse, 20, IdentityMode.RuntimeId),
        // Hermes (Electron), gemessen am 19.11.2026 (Nachtrag im Bericht p7-zielprobe): bis 100 Zeichen je Teil zeichengetreu,
        // ab 200 nicht mehr; 50 mit Abstand zur Grenze. Klasse aus vielen Stilnamen, darum ohne; Enter schickt ab
        new("Hermes", "Edit", null, "Chrome", null, FieldKinds.ChatInput, "Hermes – Eingabe",
            LineBreakMode.Refuse, 50, IdentityMode.RuntimeId),
    ];

    /// <summary>Startwerte von P3 Vertrag 4; den Modus setzt die Fabrik je Diktat (ER-19, ER-20).</summary>
    private static LiveOptions Live() => new(
        LiveMode.Sentence,
        Step: TimeSpan.FromSeconds(0.5),
        Window: TimeSpan.FromSeconds(20),
        PauseThreshold: TimeSpan.FromSeconds(0.6),
        MaxUtterance: TimeSpan.FromSeconds(20),
        CutGuard: TimeSpan.FromSeconds(0.3),
        BacklogWarning: TimeSpan.FromSeconds(10),
        BacklogLimit: TimeSpan.FromSeconds(120),
        Filter: new HallucinationFilter([], 0.5f))
    {
        // P11 S3 (Auswertung S2 vom 18.11.2026): 0,5 s Ton vor jedem Abschnitt nach einer Pause; Längengrenze 20 s, das
        // Höchste, was der Erkenner zulässt, damit seltener mitten im Sprechen geschnitten wird
        Lead = TimeSpan.FromSeconds(0.5),
    };

    private static RecognizerRuntime? Runtime(string name) => name.ToUpperInvariant() switch
    {
        "CUDA" => RecognizerRuntime.Cuda,
        "VULKAN" => RecognizerRuntime.Vulkan,
        "CPU" => RecognizerRuntime.Cpu,
        _ => null,
    };


    /// <summary>
    /// Ein Freigabeschritt mit Frist; nach Ablauf geht es weiter, der Rest wird beobachtet. Der ganze Aufruf läuft in einer
    /// eigenen Aufgabe, so gilt die Frist auch für einen synchron blockierenden Teil (U35-08).
    /// </summary>
    private static async Task ReleaseAsync(Func<ValueTask> release)
    {
        var task = Task.Run(async () => await release());
        try
        {
            await task.WaitAsync(ReleaseLimit);
        }
        catch (TimeoutException)
        {
            _ = task.ContinueWith(done => _ = done.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Weiter freigeben
        }
    }

    /// <summary>Zwischenablage auf dem UI-Thread (D4 b).</summary>
    private sealed class WpfClipboard(System.Windows.Threading.Dispatcher dispatcher) : IClipboard
    {
        public void SetText(string text) => dispatcher.Invoke(() => PrivateClipboard.SetText(text));
    }
}
