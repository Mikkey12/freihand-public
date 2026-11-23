using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Freihand.Adapters.Dictation;
using Freihand.Core.Recovery;

namespace Freihand.App;

/// <summary>
/// Erscheinung von Freihand: Symbol im Infobereich, schwebende Pille,
/// Diktatfenster und das bisherige Statusfenster als Diagnoseansicht. Verbindet die Fenster mit der Steuerung; alles auf
/// dem UI-Thread. Was angezeigt wird, entscheidet <see cref="PillPresenter"/>.
/// </summary>
internal sealed class Shell : IDisposable
{
    private readonly MainWindow _diagnostics;
    private readonly string _folder;
    private readonly PillWindow _pill = new();
    private readonly DictationWindow _pad = new();
    private readonly SuggestionWindow _suggestion = new();
#if GESCHAEFT
#pragma warning disable CS0649 // Geschäftsausgabe: ohne Online-Dienst bleibt der Korrekturdienst leer (GE-02)
#endif
    private Adapters.Transform.CorrectionService? _corrector;
#if GESCHAEFT
#pragma warning restore CS0649
#endif
    private string _route = string.Empty;
    private HotkeyTexts _keyTexts = new();
    private readonly PillPresenter _presenter = new(sound: false);
    private TrayIcon? _tray;
    private DictationController? _controller;
    private WordStore? _words;
    private WordListener? _listener;
    private LearnWindow? _learn;
    private Core.Commands.CommandSet _commands = Core.Commands.BuiltInCommands.Pilot;
    private Core.Live.InterpreterOptions? _interpreting;
    private ControllerUpdate? _last;
    private bool _ownNotice;
    private bool _disposed;

    public Shell(MainWindow diagnostics, string folder)
    {
        _diagnostics = diagnostics;
        _folder = folder;
        _keys = new Windows.Beta.ApiKeyStore(Path.Combine(folder, "schluessel.bin"));
        _pill.Place(LoadPlace());
        _pill.Moved += (_, point) => SavePlace(point);
        _pill.HideElapsed += (_, state) =>
        {
            if (_ownNotice)
            {
                _ownNotice = false;
            }
            else if (_last is { } last)
            {
                _presenter.Dismiss(last, state);
            }

            Render();
        };
        _pill.WindowRequested += (_, held) => OpenPad(held);
        _pill.CancelRequested += (_, _) => _ = _controller?.CancelAsync();
        _pad.OffersChanged += (_, _) => RenderTray();
        _diagnostics.TakeToWindow = id =>
        {
            var text = _controller?.TakeHeld([id]) ?? string.Empty;
            _pad.Open(null);
            _pad.Append(text);
        };
    }

    /// <summary>Beenden über das Menü des Infobereichs.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Die Schreibfläche als Ziel der Steuerung (E2).</summary>
    public IDictationPad Pad => _pad;

    /// <summary>Sicherung der Schreibfläche und Angebot nach dem Start (AU-12, BF-12): auch ohne Erkenner.</summary>
    public void AttachRecovery(RecoveryStore store, RecoveryScan scan, bool copyAllowed)
    {
        _pad.AttachRecovery(store, copyAllowed);
        _pad.Offer(scan);
        // Nur Gerettetes ruft nach dir; Übernommenes bleibt still in der Liste (Handtest vom 11.11.2026)
        if (DraftOffers.NeedsAttention(scan))
        {
            // Nur anbieten, nie einfügen: öffnen, kopieren oder löschen entscheidest du; dabei nie den Fokus nehmen
            _pad.Open(null, activate: false);
            _pad.ShowHint(DraftOffers.Rescued(scan) > 0
                ? "Nach dem letzten Beenden sind Texte gerettet worden. Öffnen, kopieren oder löschen."
                : "Einige Sicherungen sind nicht lesbar.");
        }
    }

    /// <summary>
    /// Was ohne Erkenner gehen muss, früh beim Start (Beta): Symbol und Menü, Korrigieren und Glätten mit dem gespeicherten
    /// Schlüssel (B8), Autostart (B2). So bleiben Menü, gerettete Texte und Schlüssel auch ohne Modell erreichbar.
    /// </summary>
    public void ConfigureBeta(PrototypeSettings settings, string settingsPath)
    {
        _tray = new TrayIcon(settings.Sound, online: !Edition.Business);
        _tray.SetKeys(settings.Hotkeys.DictationWindow, settings.Hotkeys.Mark);
        _tray.ProblemRequested += (_, _) => MarkProblem("menü");
        _tray.LogFolderRequested += (_, _) => OpenLogFolder();
        _tray.MenuOpening += (_, _) =>
        {
            RefreshMicrophones();
            RefreshModels();
            if (_review is { } review)
            {
                _tray?.SetReview(review.ActiveUntil);
            }
        };
        _tray.ReviewToggled += (_, _) => ToggleReview();
        if (_review is { } shown)
        {
            _tray.SetReview(shown.ActiveUntil);
        }
        _tray.MicrophoneChosen += (_, id) => ChooseMicrophone(id, settingsPath, settings);
        _runningModel = settings.Model;
        _savedModel = settings.Model;
        RefreshModels();
        _tray.ModelChosen += (_, entry) => ChooseModel(entry, settingsPath, settings);
        _tray.WindowRequested += (_, _) => OpenPad(held: false);
        _tray.OffersRequested += (_, _) => OpenPad(held: false);
        _tray.DiagnosticsRequested += (_, _) => _diagnostics.Show();
        _tray.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _tray.StatusRequested += (_, _) => ShowStatus();
        _tray.RestartRequested += (_, _) => _ = _controller?.RestartRecognizerAsync();
        _tray.SettingsRequested += (_, _) => OpenSettings(settingsPath);
        _tray.LearnRequested += (_, _) => OpenLearn();
        _tray.FieldMeasureRequested += (_, _) => OpenFieldMeasure();
        _tray.WordListRequested += (_, _) =>
        {
            if (_words is { } words)
            {
                LearnWindow.OpenList(words);
            }
        };
        _tray.SoundChanged += (_, on) =>
        {
            _presenter.Sound = on;
            if (!SaveSound(settingsPath, settings, on))
            {
                // Wie bei Mikrofon und Modell: ein Speicherfehler wird gesagt (Codex zu PR #69)
                ShowNotice($"Ton {(on ? "ein" : "aus")} – nur bis zum Beenden (nicht gespeichert)", StateColor.Yellow, TimeSpan.FromSeconds(4));
            }
        };

#if !GESCHAEFT
        // Korrigieren und Glätten: Dienst je Schlüssel, nur auf Knopfdruck (B8, Entscheide B-E6 und B-E8); nie in der
        // Geschäftsausgabe (GE-02)
        var formatter = new Core.Formatting.SwissTextFormatter(
            Core.Formatting.SwissFormatOptions.Default, Core.Formatting.RegionalFormat.SwissDefault);
        _corrector = new Adapters.Transform.CorrectionService(() => Transformer(settings.CorrectionModel), formatter.Format,
            Adapters.Transform.CorrectionService.DefaultTimeout);
        _route = $"online · {settings.CorrectionModel}";
        _pad.AttachCorrector(_corrector, _route);
        _tray.KeyRequested += (_, _) =>
        {
            var window = new KeyWindow(_keys);
            window.KeyChanged += (_, _) => DropTransformer();
            window.Show();
            window.Activate();
        };
#endif

        // Autostart nur aus dem Programmordner (B2, BT02)
        var autostart = new Windows.Beta.Autostart(new Windows.Beta.CurrentUserRunKey(), ProgramFolder, Environment.ProcessPath ?? string.Empty);
        _tray.SetAutostart(autostart.IsEnabled, autostart.CanEnable);
        _tray.AutostartChanged += (_, on) =>
        {
            try
            {
                if (on)
                {
                    autostart.Enable();
                }
                else
                {
                    autostart.Disable();
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // Der Haken zeigt danach den tatsächlichen Stand
            }

            _tray.SetAutostart(autostart.IsEnabled, autostart.CanEnable);
        };
    }

    /// <summary>Wörterliste (P11 S4), früh beim Start; das Zuhören kommt mit dem Erkenner dazu.</summary>
    /// <param name="commands">Befehle des Diktats; Vorschläge beim Lernen werden damit geprüft.</param>
    /// <param name="interpreting">Auslegung des Diktats (ohne Formatter); null heisst Standardauslegung.</param>
    public void AttachWords(
        WordStore words, WordListener? listener, Core.Commands.CommandSet commands, Core.Live.InterpreterOptions? interpreting)
    {
        _words = words;
        _listener = listener ?? _listener;
        _commands = commands;
        _interpreting = interpreting ?? _interpreting;
    }

    /// <summary>Ein Lernfenster zur Zeit; Diktat und Lernen teilen das Mikrofon.</summary>
    private void OpenLearn()
    {
        if (_words is not { } words)
        {
            return;
        }

        if (_learn is { } open)
        {
            open.Activate();
            return;
        }

        _learn = new LearnWindow(words, _listener, _commands, _interpreting, () => _controller?.DeviceId,
            () => _controller is null || _controller.Current.Phase == ControllerPhase.Idle, line => _log?.Write(line));
        _learn.Closed += (_, _) => _learn = null;
        _learn.Show();
        _learn.Activate();
    }

    private MeasuredFieldStore? _measuredFields;
    private Core.Platform.TargetRuleTable? _targetTable;
    private FieldMeasureWindow? _fieldMeasure;
    private bool _measuring;

    /// <summary>„Feld messen“ (21.11.2026): Liste der gemessenen Felder und die Zieltabelle, die sie sofort übernimmt.</summary>
    public void AttachFieldMeasurement(MeasuredFieldStore store, Core.Platform.TargetRuleTable table)
    {
        _measuredFields = store;
        _targetTable = table;
        ReloadMeasured(notify: true, atStart: true);

        // „Zeile löschen = wieder der bisherige Wert“ gilt sofort, nicht erst nach einem Neustart (eigene Vorprüfung)
        try
        {
            _measuredWatcher = new FileSystemWatcher(Path.GetDirectoryName(Path.GetFullPath(store.Path))!, Path.GetFileName(store.Path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler reload = (_, _) => ReloadMeasured(notify: true);
            _measuredWatcher.Changed += reload;
            _measuredWatcher.Created += reload;
            _measuredWatcher.Deleted += reload;
            _measuredWatcher.Renamed += (_, _) => ReloadMeasured(notify: true);
            _measuredWatcher.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Ohne Überwachung gilt eine Änderung von Hand nach der nächsten Messung oder dem nächsten Start
        }
    }

    private FileSystemWatcher? _measuredWatcher;

    /// <summary>
    /// Liest die Liste neu; ist sie ungültig oder gerade nicht lesbar, bleibt der bisherige Stand (nie eine leere Liste). Mit
    /// <paramref name="notify"/> nennt ein Hinweis den Grund (auch vom Hintergrund-Thread der Überwachung) und das Protokoll
    /// hält ihn fest; eine Messung nennt ihn stattdessen im Ergebnis.
    /// </summary>
    private bool ReloadMeasured(bool notify = false, bool atStart = false)
    {
        if (_measuredFields is not { } store || _targetTable is not { } table)
        {
            return false;
        }

        try
        {
            table.SetMeasured(store.Load().Select(item => item.ToRule()));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (notify)
            {
                var line = exception is MeasuredListInvalidException invalid
                    ? invalid.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "-";
                _log?.Write($"ereignis gemessene_liste_ungueltig zeile={line} start={(atStart ? "ja" : "nein")}");
                var text = MeasuredFieldStore.Problem(exception, atStart);
                _pad.Dispatcher.BeginInvoke(() => ShowNotice(text, StateColor.Yellow, TimeSpan.FromSeconds(10)));
            }

            return false;
        }
    }

    private void OpenFieldMeasure()
    {
        if (_measuredFields is null || _targetTable is null)
        {
            ShowNotice("Feld messen geht erst, wenn Freihand ganz gestartet ist", StateColor.Yellow, TimeSpan.FromSeconds(4));
            return;
        }

        if (_fieldMeasure is { } open)
        {
            open.Activate();
            return;
        }

        _fieldMeasure = new FieldMeasureWindow(MeasureBlocked, MeasureFieldAsync, SaveManualField, OpenMeasuredFields);
        _fieldMeasure.Closed += (_, _) => _fieldMeasure = null;
        _fieldMeasure.Show();
        _fieldMeasure.Activate();
    }

    /// <summary>Während eines Diktats oder einer laufenden Messung würde Freihand selbst ins gemessene Feld schreiben.</summary>
    private string? MeasureBlocked() =>
        _measuring ? "Es läuft schon eine Messung."
        : _controller is { } controller && controller.Current.Phase != ControllerPhase.Idle ? "Erst messen, wenn kein Diktat läuft."
        : null;

    /// <summary>Wert von Hand (Bedienregel vom 21.11.2026): speichern, sofort übernehmen, ins Protokoll.</summary>
    private string SaveManualField(MeasuredField field)
    {
        if (_measuredFields is not { } store)
        {
            return "Nicht gespeichert.";
        }

        try
        {
            store.Save(field);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Nicht gespeichert. " + MeasuredFieldStore.Problem(exception, atStart: false);
        }

        _log?.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"ereignis feld_von_hand prozess={field.Process.Replace(' ', '_')} zeichen={field.Characters}"));
        return ReloadMeasured()
            ? $"Gilt ab sofort: {field.Characters} Zeichen je Schritt. Beim Diktieren prüfen, ob Buchstaben fehlen."
            : "Gespeichert; wird übernommen, sobald die Liste wieder lesbar ist.";
    }

    private async Task<MeasurementResult> MeasureFieldAsync(bool identifyOnly)
    {
        var tool = Path.Combine(AppContext.BaseDirectory, "Freihand.Zielprobe.exe");
        if (!File.Exists(tool))
        {
            return new MeasurementResult(null, "Das Messwerkzeug fehlt im Programmordner (Freihand.Zielprobe.exe). Nichts gemessen.", false);
        }

        var folder = Path.Combine(_folder, "Messung");
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, $"feld-messen-{Guid.NewGuid():N}.json");
        _measuring = true;
        ShowNotice(identifyOnly
                ? "Feld erkennen: jetzt in das Feld klicken, dann warten bis drei Töne"
                : "Feld messen: jetzt in ein leeres Feld klicken, dann nichts anfassen bis drei Töne",
            StateColor.Yellow, TimeSpan.FromSeconds(12));
        try
        {
            var info = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "messen", "--wartezeit", "10", "--out", output })
            {
                info.ArgumentList.Add(argument);
            }

            if (identifyOnly)
            {
                info.ArgumentList.Add("--modus");
                info.ArgumentList.Add("erkennen");
            }

            using (var process = Process.Start(info) ?? throw new InvalidOperationException("Messwerkzeug nicht gestartet."))
            {
                // Obergrenze nur gegen ein hängendes Werkzeug: meist rund eine Minute, mit Pausen zum Leeren von Hand (je
                // höchstens 90 s, Sicherheitsprüfung) länger
                using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                try
                {
                    // Wartet das Werkzeug, bis der Nutzer das Testfeld leert, liegt die Pausendatei daneben: Hinweis zeigen
                    var pause = Path.ChangeExtension(output, ".pause");
                    var shown = false;
                    while (!process.HasExited)
                    {
                        var waiting = File.Exists(pause);
                        if (waiting && !shown)
                        {
                            ShowNotice("Feld messen: Prüftext kam verändert an. Bitte das Testfeld selbst leeren (Strg+A, Entf), dann nichts anfassen",
                                StateColor.Yellow, TimeSpan.FromSeconds(90));
                        }

                        if (!waiting && shown)
                        {
                            ShowNotice("Feld messen läuft weiter: nichts anfassen bis drei Töne", StateColor.Yellow, TimeSpan.FromSeconds(10));
                        }

                        shown = waiting;
                        await Task.WhenAny(process.WaitForExitAsync(limit.Token), Task.Delay(500, limit.Token));
                        limit.Token.ThrowIfCancellationRequested();
                    }
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    return new MeasurementResult(null, "Die Messung hing und wurde abgebrochen. Bitte den Prüftext von Hand löschen.", true);
                }
            }

            // Das Werkzeug legt neben die JSON-Datei eine Zusammenfassung „schlüssel = wert“; Freihand liest nur diese
            var summary = Path.ChangeExtension(output, ".txt");
            if (!File.Exists(summary))
            {
                // Eigene Vorprüfung: ohne Zusammenfassung ist unbekannt, ob schon Prüftext im Feld steht
                _log?.Write("ereignis feld_gemessen ergebnis=ohne_zusammenfassung");
                return new MeasurementResult(null,
                    "Das Messwerkzeug hat kein Ergebnis geliefert. Nichts gespeichert. Bitte das Feld prüfen; steht noch Prüftext darin, von Hand löschen.",
                    true);
            }

            var result = FieldMeasurement.Evaluate(await File.ReadAllTextAsync(summary), DateOnly.FromDateTime(DateTime.Now));
            if (result.Field is not { } field)
            {
                _log?.Write("ereignis feld_gemessen ergebnis=ohne");
                return result;
            }

            try
            {
                _measuredFields!.Save(field);
                if (!ReloadMeasured())
                {
                    // Gespeichert, aber gerade nicht wieder lesbar: wirkt, sobald die Datei lesbar ist (Überwachung, Start)
                    return result with { Text = result.Text + " Die Liste wird übernommen, sobald sie wieder lesbar ist." };
                }

                _log?.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"ereignis feld_gemessen prozess={field.Process.Replace(' ', '_')} zeichen={field.Characters}"));
                return result;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Eigene Vorprüfung: kein „gilt ab sofort“ neben „nicht gespeichert“
                return result with
                {
                    Field = null,
                    Text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"Dreimal fehlerfrei mit {field.Characters} Zeichen am Stück, aber nicht gespeichert. {MeasuredFieldStore.Problem(exception, atStart: false)}"),
                };
            }
        }
        finally
        {
            _measuring = false;
            try
            {
                File.Delete(output);
                File.Delete(Path.ChangeExtension(output, ".txt"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Nur Merkmale und Längen, kein Inhalt; bleibt sonst im Messordner liegen
            }
        }
    }

    private void OpenMeasuredFields()
    {
        if (_measuredFields is not { } store)
        {
            return;
        }

        try
        {
            store.EnsureExists();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        OpenSettings(store.Path);
    }

    /// <summary>Infobereich, Pille und Diktatfenster an die Steuerung; Ton und Tasten aus den Einstellungen.</summary>
    public void Connect(DictationController controller, PrototypeSettings settings, string settingsPath)
    {
        _controller = controller;
        _presenter.Sound = settings.Sound;
        _pad.SetKeyText(settings.Hotkeys.DictationWindow);
        _pad.HandOver = controller.HandOverAsync;
        var dispatcher = _pad.Dispatcher;
        controller.DictationWindowRequested += (_, request) => dispatcher.BeginInvoke(() => TogglePad(request.Mark));
        _keyTexts = settings.Hotkeys;
        _suggestion.HandOver = controller.HandOverAsync;
        controller.CorrectionRequested += (_, request) => dispatcher.BeginInvoke(() => Correct(request));
        controller.MarkRequested += (_, _) => dispatcher.BeginInvoke(() => MarkProblem("taste"));
        controller.LevelChanged += (_, level) => dispatcher.BeginInvoke(() => _pill.SetLevel(level));
        if (_tray is null)
        {
            ConfigureBeta(settings, settingsPath);
        }

        RenderTray();
    }

    private DailyLog? _log;
    private ProblemMarker? _marker;
    private bool _powerSubscribed;

    /// <summary>Protokolldatei und Marken (Beta B4, B5); vor <see cref="ConfigureBeta"/>.</summary>
    public void AttachProtocol(DailyLog log, ProblemMarker marker)
    {
        _log = log;
        _marker = marker;

        // Standby und Aufwachen (P11, Kapitel 6, Entscheid P11-E6): zeigt, ob Freihand ein abgebrochenes Herunterfahren
        // überlebt; ohne Neuanmeldung greift der Autostart nicht
        if (!_powerSubscribed)
        {
            try
            {
                Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
                _powerSubscribed = true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                _log?.Write("ereignis energie_status=unverfuegbar");
            }
        }
    }

    /// <summary>Protokollzeile zu einem Wechsel des Energiezustands; null für andere Wechsel (Akku, Netz).</summary>
    internal static string? PowerLine(Microsoft.Win32.PowerModes mode) => mode switch
    {
        Microsoft.Win32.PowerModes.Suspend => "ereignis standby",
        Microsoft.Win32.PowerModes.Resume => "ereignis aufgewacht",
        _ => null,
    };

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (PowerLine(e.Mode) is { } line)
        {
            _log?.Write(line);
        }

        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            Resumed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Das Gerät ist aus dem Standby aufgewacht (für die Freigabe gesperrter Neustarts).</summary>
    public event EventHandler? Resumed;

    /// <summary>Prüfaufnahmen im Menü (P11-E1); nur in der privaten Ausgabe, vor <see cref="ConfigureBeta"/>.</summary>
    public void AttachReview(ReviewRecorder review) => _review = review;

    private ReviewRecorder? _review;

    private void ToggleReview()
    {
        if (_review is not { } review || _tray is not { } tray)
        {
            return;
        }

        if (review.ActiveUntil is not null)
        {
            ShowNotice(review.Disable()
                    ? "Prüfaufnahmen aus, alle gelöscht"
                    : "Prüfaufnahmen aus; nicht alles gelöscht: " + review.Folder,
                review.ActiveUntil is null ? StateColor.Green : StateColor.Yellow, TimeSpan.FromSeconds(6));
        }
        else if (review.Enable() && review.ActiveUntil is { } until)
        {
            ShowNotice($"Prüfaufnahmen bis {until.ToLocalTime():dd.MM. HH:mm}", StateColor.Yellow, TimeSpan.FromSeconds(4));
        }
        else
        {
            ShowNotice("Prüfaufnahmen nicht einschaltbar: " + review.Folder, StateColor.Yellow, TimeSpan.FromSeconds(8));
        }

        tray.SetReview(review.ActiveUntil);
    }

    /// <summary>„Problem merken“ aus Menü oder Taste: Marke ins Protokoll, kurze Bestätigung in der Pille (B5).</summary>
    private void MarkProblem(string source)
    {
        if (_marker is not { } marker)
        {
            return;
        }

        var number = marker.Mark(source);
        ShowNotice($"Problem Nr. {number} gemerkt", StateColor.Green, TimeSpan.FromSeconds(3));
    }

    private void OpenLogFolder()
    {
        if (_log is not { } log)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(log.Folder);
            Process.Start(new ProcessStartInfo(log.Folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowNotice("Protokollordner nicht zu öffnen: " + log.Folder, StateColor.Yellow, TimeSpan.FromSeconds(8));
        }
    }

    /// <summary>Geräte beim Öffnen des Menüs; ohne Steuerung (kein Erkenner) gibt es kein Diktat und keine Wahl.</summary>
    private void RefreshMicrophones()
    {
        if (_tray is null)
        {
            return;
        }

        var controller = _controller;
        _tray.SetMicrophones(
            MicrophoneMenu.Entries(controller?.Devices ?? [], controller?.DeviceId), enabled: controller is not null);
    }

    /// <summary>Wählt das Mikrofon ab dem nächsten Diktat und speichert die Wahl in der Einstellungsdatei (B3).</summary>
    private void ChooseMicrophone(string? id, string settingsPath, PrototypeSettings loaded)
    {
        if (_controller is not { } controller)
        {
            return;
        }

        controller.SelectDevice(id);
        var name = id is null ? MicrophoneMenu.WindowsDefault : controller.Devices.FirstOrDefault(device => device.Id == id)?.Name ?? "Gerät";
        var saved = SaveDevice(settingsPath, loaded, controller.DeviceId);
        ShowNotice(saved ? $"Mikrofon: {name} – ab dem nächsten Diktat" : $"Mikrofon: {name} – nur bis zum Beenden (nicht gespeichert)",
            StateColor.Green, TimeSpan.FromSeconds(4));
    }

    private string _runningModel = string.Empty;
    private string _savedModel = string.Empty;

    private void RefreshModels() => _tray?.SetModels(ModelMenu.Entries(_runningModel, _savedModel, File.Exists));

    /// <summary>Speichert Modell und Rechenfenster zusammen; gilt ab dem nächsten Start (Menü „Modell“).</summary>
    private void ChooseModel(ModelEntry entry, string settingsPath, PrototypeSettings loaded)
    {
        var saved = SaveModel(settingsPath, loaded, entry);
        if (saved)
        {
            _savedModel = entry.Path;
        }

        RefreshModels();
        var shown = ModelMenu.Entries(_runningModel, _savedModel, File.Exists).FirstOrDefault(item => item.Path == entry.Path) ?? entry;
        ShowNotice(ModelMenu.Notice(shown, saved), saved ? StateColor.Green : StateColor.Yellow, TimeSpan.FromSeconds(6));
    }

    /// <summary>Kurzer eigener Hinweis in der Pille, bis es Neues gibt; das Symbol bleibt.</summary>
    private void ShowNotice(string text, StateColor color, TimeSpan duration)
    {
        _ownNotice = true;
        _pill.Render(new PillView(PillState.Notice, color, text, [], duration, false));
    }

    /// <summary>
    /// Taste fürs Korrigieren oder Glätten (B9): im Diktatfenster wie die Knöpfe dort, sonst mit der Markierung des
    /// anderen Programms im Vorschlagsfenster.
    /// </summary>
    private void Correct(CorrectionRequest request)
    {
        if (request.FromPad)
        {
            _ = _pad.SuggestAsync(request.Kind);
            return;
        }

        if (_corrector is { } corrector)
        {
            var key = request.Kind == Adapters.Transform.CorrectionKind.Correct ? _keyTexts.Correct : _keyTexts.Smooth;
            _ = _suggestion.RunAsync(request, corrector, _route, key);
        }
    }

    /// <summary>Programmordner nach Entscheid B-E1; nur von dort lässt sich der Autostart einschalten.</summary>
    public static string ProgramFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Freihand");

    private Windows.Beta.ApiKeyStore _keys = null!;
#if !GESCHAEFT
    private Online.ClaudeTransformer? _transformer;
    private string? _transformerKey;

    /// <summary>Der Dienst zum gespeicherten Schlüssel, neu gebaut, wenn der Schlüssel wechselt; null ohne Schlüssel.</summary>
    private Core.Transform.ITextTransformer? Transformer(string model)
    {
        var key = _keys.Load();
        if (key is null)
        {
            DropTransformer();
            return null;
        }

        if (_transformer is null || _transformerKey != key)
        {
            DropTransformer();
            _transformer = new Online.ClaudeTransformer(key, model, Adapters.Transform.CorrectionService.DefaultTimeout);
            _transformerKey = key;
        }

        return _transformer;
    }

    private void DropTransformer()
    {
        _transformer?.Dispose();
        _transformer = null;
        _transformerKey = null;
    }
#else
    private static void DropTransformer()
    {
        // Geschäftsausgabe: es gibt keinen Online-Dienst (GE-02)
    }
#endif

    /// <summary>Stand der Steuerung; auf dem UI-Thread.</summary>
    public void Show(ControllerUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        _last = update;
        if (_presenter.ToneFor(update) is { } tone)
        {
            Tones.Play(tone);
        }

        _pill.SetFocusWait(update.FocusWait);
        Render();
    }

    /// <summary>Startproblem (Modell fehlt, Taste doppelt …): in der Pille und im Symbol, bis es Neues gibt.</summary>
    public void ShowStartup(string text)
    {
        _ownNotice = true;
        _pill.Render(new PillView(PillState.Notice, StateColor.Yellow, text, [], TimeSpan.FromSeconds(12), false));
        _tray?.Set(StateColor.Yellow, "Freihand – " + text);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _measuredWatcher?.Dispose();
        if (_powerSubscribed)
        {
            try
            {
                Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                // Ein Fehler des optionalen Protokolls darf das Sichern beim Schliessen nicht verhindern
            }

            _powerSubscribed = false;
        }

        DropTransformer();
        _tray?.Dispose();
        _pill.Close();
        _pad.CloseForExit();
        _suggestion.CloseForExit();
    }

    private void Render()
    {
        if (_last is not { } update)
        {
            return;
        }

        var view = _presenter.Show(update);
        if (!(_ownNotice && view.State == PillState.Hidden))
        {
            _ownNotice = false;
            _pill.Render(view);
        }

        RenderTray();
    }

    private void RenderTray()
    {
        if (_tray is null || _last is not { } update)
        {
            return;
        }

        _tray.Set(PillPresenter.TrayColor(update, _pad.Offers), PillPresenter.TrayText(update, _pad.Offers));
        _tray.SetRestartEnabled(PillPresenter.CanRestartRecognizer(update));
        _tray.SetOffers(_pad.Offers);
    }

    /// <summary>Taste: aus dem Diktatfenster heraus schliesst sie es, sonst öffnet sie es mit dem gemerkten Feld.</summary>
    private void TogglePad(Core.Platform.MarkResult mark)
    {
        if (_pad.IsVisible && _pad.IsActive)
        {
            _pad.Hide();
            return;
        }

        _pad.Open(mark);
    }

    /// <summary>Öffnet das Diktatfenster; aus der Pille „gehalten“ mit dem gehaltenen Text (Kapitel 3.4).</summary>
    private void OpenPad(bool held)
    {
        var text = held ? _controller?.TakeHeld() ?? string.Empty : string.Empty;
        _pad.Open(null);
        _pad.Append(text);
        if (held && _last is { } last)
        {
            _presenter.Dismiss(last, PillState.Held);
            Render();
        }
    }

    private void ShowStatus()
    {
        if (_last is not { } update)
        {
            return;
        }

        var view = _presenter.Show(update);
        _ownNotice = view.State == PillState.Hidden;
        _pill.Render(view.State == PillState.Hidden
            ? new PillView(PillState.Notice, PillPresenter.TrayColor(update, _pad.Offers), PillPresenter.TrayText(update, _pad.Offers),
                view.Details, TimeSpan.FromSeconds(5), false)
            : view);
        _pill.ShowDetails(true);
    }

    private static void OpenSettings(string path)
    {
        try
        {
            // Wirkt nach dem nächsten Start; geöffnet im Standardprogramm für Textdateien
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Kein Programm für .txt: die Datei bleibt, wo sie ist
        }
    }

    /// <returns>Falsch, wenn die Datei nicht geschrieben werden konnte; die Wahl gilt dann bis zum Beenden.</returns>
    private static bool SaveDevice(string path, PrototypeSettings loaded, string? deviceId)
    {
        try
        {
            // Neu lesen, damit eine Änderung von Hand in der Datei nicht verloren geht
            PrototypeSettings.LoadOrCreate(path, loaded);
            PrototypeSettings.SetValue(path, "geraet", deviceId ?? string.Empty);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <returns>Falsch, wenn die Datei nicht geschrieben werden konnte; dann gilt weiter die bisherige Wahl.</returns>
    private static bool SaveModel(string path, PrototypeSettings loaded, ModelEntry entry)
    {
        try
        {
            // Neu lesen, damit eine Änderung von Hand in der Datei nicht verloren geht; Modell und Fenster in einem Schritt
            PrototypeSettings.LoadOrCreate(path, loaded);
            PrototypeSettings.SetValues(path,
                ("modell", entry.Path), ("audiokontext", entry.AudioContext.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <returns>Falsch, wenn die Datei nicht geschrieben werden konnte; der Ton gilt dann bis zum Beenden.</returns>
    private static bool SaveSound(string path, PrototypeSettings loaded, bool on)
    {
        try
        {
            // Neu lesen, damit eine Änderung von Hand in der Datei nicht verloren geht
            PrototypeSettings.LoadOrCreate(path, loaded);
            PrototypeSettings.SetValue(path, "ton", on ? "ein" : "aus");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string PlacePath => Path.Combine(_folder, "anzeige.txt");

    private PillAnchor? LoadPlace()
    {
        try
        {
            return File.Exists(PlacePath) ? PillPresenter.ParseAnchor(File.ReadAllLines(PlacePath)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SavePlace(PillAnchor anchor)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(PlacePath, PillPresenter.FormatAnchor(anchor));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Der Platz gilt bis zum Beenden trotzdem
        }
    }
}
