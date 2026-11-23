using System.IO;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Freihand.App;

/// <summary>
/// Symbol im Infobereich, Variante A „Wellenrahmen“: Schallwelle in einem
/// abgerundeten Quadrat; grau bereit, rot Aufnahme, gelb Hinweis. Menü mit Diktatfenster, geretteten Texten, Ton,
/// Einstellungen, Diagnose und Beenden. Gezeichnet wird zur Laufzeit; es gibt keine Bilddatei.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon = new();
    private readonly Dictionary<StateColor, Drawing.Icon> _icons = [];
    private readonly Forms.ToolStripMenuItem _window = new("Diktatfenster öffnen");
    private readonly Forms.ToolStripMenuItem _offers = new("Gerettete Texte");
    private readonly Forms.ToolStripMenuItem _sound = new("Ton bei Start und Ende") { CheckOnClick = true };
    private readonly Forms.ToolStripMenuItem _restart = new("Erkennung neu starten") { Enabled = false, Visible = false };
    private readonly Forms.ToolStripMenuItem _autostart = new("Mit Windows starten") { CheckOnClick = true };
    private readonly Forms.ToolStripMenuItem _problem = new("Problem merken");
    private readonly Forms.ToolStripMenuItem _microphone = new("Mikrofon");
    private readonly Forms.ToolStripMenuItem _model = new("Modell") { Visible = false };
    private readonly Forms.ToolStripMenuItem _review = new(ReviewOff) { Visible = false };
    private StateColor? _color;

    private const string ReviewOff = "Prüfaufnahmen 7 Tage einschalten";

    /// <param name="online">Menüeintrag für den Schlüssel; in der Geschäftsausgabe fehlt er (GE-02).</param>
    public TrayIcon(bool sound, bool online = true)
    {
        _sound.Checked = sound;
        var menu = new Forms.ContextMenuStrip();
        _window.Click += (_, _) => WindowRequested?.Invoke(this, EventArgs.Empty);
        _offers.Click += (_, _) => OffersRequested?.Invoke(this, EventArgs.Empty);
        _sound.CheckedChanged += (_, _) => SoundChanged?.Invoke(this, _sound.Checked);
        _restart.Click += (_, _) => RestartRequested?.Invoke(this, EventArgs.Empty);
        _problem.Click += (_, _) => ProblemRequested?.Invoke(this, EventArgs.Empty);
        _microphone.DropDownItems.Add(new Forms.ToolStripMenuItem(Adapters.Dictation.MicrophoneMenu.WindowsDefault) { Enabled = false });
        menu.Opening += (_, _) => MenuOpening?.Invoke(this, EventArgs.Empty);
        // Menü (Bedienregel vom 21.11.2026): oben, was man im Alltag braucht; Einstellungen, Messen und
        // Prüfwerkzeuge unter „Erweitert“. „Erkennung neu starten“ erscheint nur, wenn es gebraucht wird.
        menu.Items.Add(_window);
        menu.Items.Add(_offers);
        menu.Items.Add(_microphone);
        menu.Items.Add("Wort lernen …", null, (_, _) => LearnRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(_problem);
        menu.Items.Add(_restart);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _autostart.CheckedChanged += (_, _) =>
        {
            if (!_settingAutostart)
            {
                AutostartChanged?.Invoke(this, _autostart.Checked);
            }
        };
        menu.Items.Add(_autostart);
        menu.Items.Add(_sound);

        var advanced = new Forms.ToolStripMenuItem("Erweitert");
        advanced.DropDownItems.Add(_model);
        advanced.DropDownItems.Add("Feld messen …", null, (_, _) => FieldMeasureRequested?.Invoke(this, EventArgs.Empty));
        advanced.DropDownItems.Add("Wörterliste öffnen", null, (_, _) => WordListRequested?.Invoke(this, EventArgs.Empty));
        if (online)
        {
            advanced.DropDownItems.Add("Schlüssel für Korrigieren eintragen …", null, (_, _) => KeyRequested?.Invoke(this, EventArgs.Empty));
        }

        advanced.DropDownItems.Add(new Forms.ToolStripSeparator());
        _review.Click += (_, _) => ReviewToggled?.Invoke(this, EventArgs.Empty);
        advanced.DropDownItems.Add(_review);
        advanced.DropDownItems.Add("Einstellungsdatei öffnen (wirkt nach Neustart)", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        advanced.DropDownItems.Add("Protokollordner öffnen", null, (_, _) => LogFolderRequested?.Invoke(this, EventArgs.Empty));
        advanced.DropDownItems.Add("Statusfenster (Diagnose)", null, (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(advanced);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                StatusRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        Set(StateColor.Grey, "Freihand – startet");
        _icon.Visible = true;
    }

    public event EventHandler? WindowRequested;

    public event EventHandler? OffersRequested;

    public event EventHandler<bool>? SoundChanged;

    /// <summary>„Erkennung neu starten“; nur wählbar, solange der Erkenner nicht verfügbar ist.</summary>
    public event EventHandler? RestartRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? DiagnosticsRequested;

    public event EventHandler? StatusRequested;

    public event EventHandler? ExitRequested;

    public void Set(StateColor color, string text)
    {
        if (_color != color)
        {
            if (!_icons.TryGetValue(color, out var icon))
            {
                icon = _icons[color] = Draw(color);
            }

            _icon.Icon = icon;
            _color = color;
        }

        _icon.Text = text.Length <= 127 ? text : text[..127];
    }

    public void SetRestartEnabled(bool enabled)
    {
        _restart.Enabled = enabled;
        _restart.Visible = enabled;
    }

    /// <summary>Schlüssel für Korrigieren eintragen (Beta B8).</summary>
    public event EventHandler? KeyRequested;

    /// <summary>„Mit Windows starten“ umgeschaltet (Beta B2).</summary>
    public event EventHandler<bool>? AutostartChanged;

    private bool _settingAutostart;

    /// <summary>Zeigt den Stand; ausserhalb des Programmordners nicht wählbar, mit Grund.</summary>
    public void SetAutostart(bool enabled, bool canEnable)
    {
        _settingAutostart = true;
        _autostart.Checked = enabled;
        _settingAutostart = false;
        _autostart.Enabled = canEnable || enabled;
        _autostart.ToolTipText = canEnable ? string.Empty : "Nur für Freihand im Programmordner (Einrichte-Skript)";
    }

    public void SetKeys(string windowKey, string markKey)
    {
        _window.Text = $"Diktatfenster öffnen ({windowKey})";
        _problem.Text = $"Problem merken ({markKey})";
    }

    /// <summary>„Problem merken“ im Menü (Beta B5).</summary>
    public event EventHandler? ProblemRequested;

    /// <summary>„Wort lernen …“ (P11 S4).</summary>
    public event EventHandler? LearnRequested;

    /// <summary>„Feld messen …“ (21.11.2026).</summary>
    public event EventHandler? FieldMeasureRequested;

    /// <summary>„Wörterliste öffnen“: <c>woerter.txt</c> im Standardprogramm (P11 S4).</summary>
    public event EventHandler? WordListRequested;

    /// <summary>Prüfaufnahmen ein- oder ausschalten (P11-E1); nur sichtbar nach <see cref="SetReview"/>.</summary>
    public event EventHandler? ReviewToggled;

    /// <summary>Zeigt den Stand der Prüfaufnahmen: aus, oder ein mit Ablauf (Ortszeit).</summary>
    public void SetReview(DateTimeOffset? until)
    {
        _review.Visible = true;
        _review.Checked = until is not null;
        _review.Text = until is { } end
            ? $"Prüfaufnahmen bis {end.ToLocalTime():dd.MM. HH:mm} (ausschalten löscht sie)"
            : ReviewOff;
    }

    /// <summary>„Protokollordner öffnen“ (Beta B4).</summary>
    public event EventHandler? LogFolderRequested;

    /// <summary>Das Menü geht auf; die Anzeige füllt dann „Mikrofon“ mit den aktuellen Geräten (Beta B3).</summary>
    public event EventHandler? MenuOpening;

    /// <summary>Ein Mikrofon gewählt; null heisst Standardgerät von Windows.</summary>
    public event EventHandler<string?>? MicrophoneChosen;

    /// <summary>Ein Modell gewählt (gilt ab dem nächsten Start).</summary>
    public event EventHandler<Adapters.Dictation.ModelEntry>? ModelChosen;

    /// <summary>Füllt „Modell“; mit weniger als zwei Modellen gibt es nichts zu wählen und der Eintrag fehlt.</summary>
    public void SetModels(IReadOnlyList<Adapters.Dictation.ModelEntry> entries)
    {
        _model.Visible = entries.Count > 1;
        _model.DropDownItems.Clear();
        foreach (var entry in entries)
        {
            var item = new Forms.ToolStripMenuItem(Adapters.Dictation.ModelMenu.Label(entry)) { Checked = entry.Selected };
            item.Click += (_, _) => ModelChosen?.Invoke(this, entry);
            _model.DropDownItems.Add(item);
        }
    }

    /// <summary>Füllt „Mikrofon“; ohne Steuerung (kein Erkenner) nicht wählbar.</summary>
    public void SetMicrophones(IReadOnlyList<Adapters.Dictation.MicrophoneEntry> entries, bool enabled)
    {
        _microphone.Enabled = enabled;
        _microphone.DropDownItems.Clear();
        foreach (var entry in entries)
        {
            var item = new Forms.ToolStripMenuItem(entry.Name) { Checked = entry.Selected, Enabled = entry.Present };
            var id = entry.Id;
            item.Click += (_, _) => MicrophoneChosen?.Invoke(this, id);
            _microphone.DropDownItems.Add(item);
        }
    }

    public void SetOffers(int offers)
    {
        _offers.Text = offers == 0 ? "Gerettete Texte" : $"Gerettete Texte ({offers})";
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
    }

    /// <summary>Wellenrahmen in 32 px, als Symbol mit eingebettetem PNG; kein Handle bleibt offen.</summary>
    internal static Drawing.Icon Draw(StateColor color)
    {
        var tint = color switch
        {
            StateColor.Red => Drawing.Color.FromArgb(0xE5, 0x48, 0x4D),
            StateColor.Yellow => Drawing.Color.FromArgb(0xF5, 0xB0, 0x00),
            StateColor.Green => Drawing.Color.FromArgb(0x2E, 0xA0, 0x43),
            _ => Drawing.Color.FromArgb(0x9A, 0x9A, 0x9A),
        };
        const int size = 32;
        using var bitmap = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Drawing.Color.Transparent);
            using var frame = new Drawing.Pen(tint, 2.6f);
            using var path = RoundedSquare(new Drawing.RectangleF(2.5f, 2.5f, size - 5, size - 5), 8);
            graphics.DrawPath(frame, path);

            // Schallwelle: eine Periode Sinus, in der Mitte am höchsten
            var points = new List<Drawing.PointF>();
            for (var x = 7f; x <= size - 7f; x += 0.5f)
            {
                var t = (x - 7f) / (size - 14f);
                var envelope = (float)Math.Sin(Math.PI * t);
                points.Add(new Drawing.PointF(x, (size / 2f) - (8f * envelope * (float)Math.Sin(4 * Math.PI * t))));
            }

            using var wave = new Drawing.Pen(tint, 2.4f) { StartCap = Drawing.Drawing2D.LineCap.Round, EndCap = Drawing.Drawing2D.LineCap.Round };
            graphics.DrawLines(wave, [.. points]);
        }

        using var png = new MemoryStream();
        bitmap.Save(png, Drawing.Imaging.ImageFormat.Png);
        var image = png.ToArray();
        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((short)0);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write((byte)size);
            writer.Write((byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(image.Length);
            writer.Write(6 + 16);
            writer.Write(image);
        }

        ico.Position = 0;
        return new Drawing.Icon(ico);
    }

    private static Drawing.Drawing2D.GraphicsPath RoundedSquare(Drawing.RectangleF bounds, float radius)
    {
        var path = new Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Leiser Ton beim Start und Ende der Aufnahme (Kapitel 3.5), im Speicher erzeugt; Standard aus.</summary>
internal static class Tones
{
    // Bleiben bestehen, solange die App läuft: Windows spielt asynchron aus ihrem Puffer
    private static readonly Lazy<System.Media.SoundPlayer> StartPlayer = new(() => Player(880));
    private static readonly Lazy<System.Media.SoundPlayer> EndPlayer = new(() => Player(660));

    public static void Play(Tone tone)
    {
        try
        {
            (tone == Tone.Start ? StartPlayer : EndPlayer).Value.Play();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            // Kein Ton ist kein Fehler des Diktats
        }
    }

    private static System.Media.SoundPlayer Player(double frequency)
    {
        var player = new System.Media.SoundPlayer(new MemoryStream(Wave(frequency, 0.09)));
        player.Load();
        return player;
    }

    /// <summary>PCM 16 bit mono, 22,05 kHz, leise (12 %), weich ein- und ausgeblendet.</summary>
    private static byte[] Wave(double frequency, double seconds)
    {
        const int rate = 22050;
        var samples = (int)(rate * seconds);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + (samples * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var index = 0; index < samples; index++)
        {
            var fade = Math.Min(1, Math.Min(index, samples - index) / (rate * 0.01));
            writer.Write((short)(Math.Sin(2 * Math.PI * frequency * index / rate) * 0.12 * fade * short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }
}
