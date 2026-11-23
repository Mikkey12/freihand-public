using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Freihand.App;

/// <summary>
/// Schwebende, durchscheinende Pille unten rechts. Zeigt nur, was
/// <see cref="PillPresenter"/> liefert; nimmt nie den Fokus (D09), ist verschiebbar und merkt sich den Platz.
/// </summary>
public partial class PillWindow : Window
{
    private const int ExtendedStyle = -20;
    private const int NoActivate = 0x08000000;
    private const int ToolWindow = 0x00000080;

    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _hide = new();
    private PillView? _view;
    private TimeSpan? _focusWait;
    private DateTime _focusWaitAt;
    private bool _detailsPinned;

    public PillWindow()
    {
        InitializeComponent();
        Theme.Apply(Resources);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, ExtendedStyle, GetWindowLongPtr(handle, ExtendedStyle) | NoActivate | ToolWindow);
        };
        // Wächst die Pille, wächst sie nach links und oben und bleibt auf dem Bildschirm (Handtest vom 11.11.2026)
        SizeChanged += (_, e) => Anchor(e.NewSize.Width, e.NewSize.Height);
        _countdown.Tick += (_, _) => ShowCountdown();
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            HideElapsed?.Invoke(this, _view?.State ?? PillState.Hidden);
        };
    }

    /// <summary>„Diktatfenster“ oder ein Klick auf die Pille im Zustand „gehalten“.</summary>
    public event EventHandler<bool>? WindowRequested;

    public event EventHandler? CancelRequested;

    /// <summary>Der Zustand hat seine Zeit gezeigt und darf ausgeblendet werden.</summary>
    public event EventHandler<PillState>? HideElapsed;

    /// <summary>Die Pille wurde verschoben; der neue Platz wird gemerkt.</summary>
    public event EventHandler<PillAnchor>? Moved;

    /// <summary>Zeigt einen Stand; versteckt sich bei <see cref="PillState.Hidden"/>. Nimmt dabei nie den Fokus.</summary>
    public void Render(PillView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var changed = _view is null || _view.State != view.State || _view.Text != view.Text;
        _view = view;
        if (view.State == PillState.Hidden)
        {
            _hide.Stop();
            _countdown.Stop();
            Hide();
            return;
        }

        Dot.Fill = Theme.Dot(view.Color);
        Label.Text = view.Text;
        DetailLines.ItemsSource = view.Details;
        var recording = view.State is PillState.Recording or PillState.FocusAway or PillState.Finishing or PillState.Starting;
        LevelTrack.Visibility = view.State == PillState.Recording ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (view.State != PillState.Recording)
        {
            LevelBar.Width = 0;
        }

        if (changed)
        {
            _hide.Stop();
            if (view.HideAfter is { } after)
            {
                _hide.Interval = after;
                _hide.Start();
            }
        }

        if (!IsVisible)
        {
            Show();
        }
    }

    /// <summary>Restzeit „Zurück ins Feld klicken“; zählt hier weiter, bis die Steuerung Neues meldet.</summary>
    public void SetFocusWait(TimeSpan? remaining)
    {
        _focusWait = remaining;
        _focusWaitAt = DateTime.UtcNow;
        if (remaining is null)
        {
            _countdown.Stop();
        }
        else
        {
            _countdown.Start();
        }
    }

    /// <summary>Pegel 0 bis 1; zeigt nur, dass Ton ankommt.</summary>
    public void SetLevel(float level)
    {
        if (_view?.State == PillState.Recording)
        {
            LevelBar.Width = LevelTrack.Width * Math.Clamp(level, 0, 1);
        }
    }

    /// <summary>Details für einige Sekunden, z. B. nach einem Klick aufs Symbol im Infobereich.</summary>
    public void ShowDetails(bool show)
    {
        _detailsPinned = show;
        DetailLines.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Regel (Handtest vom 11.11.2026): Die Pille hängt an der Ecke, die dem nächsten Bildschirmrand zugewandt ist, wächst
    /// von dort weg (längerer Text, Details) und bleibt immer ganz im Arbeitsbereich ihres Bildschirms
    /// (<see cref="PillPresenter.AnchorOf"/>, <see cref="PillPresenter.PlaceAt"/>).
    /// </summary>
    /// <param name="saved">Gemerkter Anker; ohne oder ausserhalb aller Bildschirme unten rechts.</param>
    public void Place(PillAnchor? saved)
    {
        var area = SystemParameters.WorkArea;
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        _anchor = saved is { } anchor && virtualScreen.Contains(new Point(anchor.X, anchor.Y))
            ? anchor
            : PillPresenter.DefaultAnchor((area.Left, area.Top, area.Right, area.Bottom));
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Anchor(DesiredSize.Width, DesiredSize.Height);
    }

    private PillAnchor _anchor;

    private void Anchor(double width, double height)
    {
        // Ein Punkt knapp innerhalb des festen Rands bestimmt den Bildschirm
        var inside = new Point(_anchor.X + (_anchor.FromLeft ? 1 : -1), _anchor.Y + (_anchor.FromTop ? 1 : -1));
        var area = WorkAreaAt(inside);
        var (left, top) = PillPresenter.PlaceAt(_anchor, width, height, (area.Left, area.Top, area.Right, area.Bottom));
        Left = left;
        Top = top;
    }

    /// <summary>Arbeitsbereich des Bildschirms, auf dem der Punkt liegt, in geräteunabhängigen Einheiten.</summary>
    private Rect WorkAreaAt(Point point)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
            (int)(point.X * dpi.DpiScaleX), (int)(point.Y * dpi.DpiScaleY)));
        var area = screen.WorkingArea;
        return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
    }

    private void ShowCountdown()
    {
        if (_focusWait is not { } remaining || _view?.State != PillState.FocusAway)
        {
            return;
        }

        Label.Text = PillPresenter.FocusText(remaining - (DateTime.UtcNow - _focusWaitAt), _view.MicrophoneMuted);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var before = new Point(Left, Top);
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Maustaste schon wieder oben
        }

        var after = new Point(Left, Top);
        if (before != after)
        {
            // Neuen Anker nach dem nächsten Bildschirmrand merken und die Pille ganz auf den Bildschirm holen
            var area = WorkAreaAt(new Point(Left + (ActualWidth / 2), Top + (ActualHeight / 2)));
            _anchor = PillPresenter.AnchorOf(Left, Top, ActualWidth, ActualHeight, (area.Left, area.Top, area.Right, area.Bottom));
            Anchor(ActualWidth, ActualHeight);
            Moved?.Invoke(this, _anchor);
            return;
        }

        // Ein Klick ohne Verschieben: gehalten → Diktatfenster, sonst Details ein und aus
        if (_view?.OpensDictationWindow == true)
        {
            WindowRequested?.Invoke(this, true);
        }
        else
        {
            ShowDetails(!_detailsPinned);
        }
    }

    private void OnMouseEnter(object sender, MouseEventArgs e) => DetailLines.Visibility = Visibility.Visible;

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_detailsPinned)
        {
            DetailLines.Visibility = Visibility.Collapsed;
        }
    }

    private void OnWindowButton(object sender, RoutedEventArgs e) =>
        WindowRequested?.Invoke(this, _view?.OpensDictationWindow == true);

    private void OnCancelButton(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}

/// <summary>Farben nach hell und dunkel von Windows; Farbe nur im Zustandspunkt (Kapitel 3.5).</summary>
internal static class Theme
{
    public static bool Dark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>Durchscheinend; ohne Transparenz des Systems bleibt es ein schlichter, fast deckender Hintergrund.</summary>
    public static void Apply(ResourceDictionary resources)
    {
        var dark = Dark();
        resources["PillBack"] = Brush(dark ? 0xE6202020 : 0xEBF7F7F7);
        resources["PillBorder"] = Brush(dark ? 0x33FFFFFF : 0x1F000000);
        resources["PillText"] = Brush(dark ? 0xFFF2F2F2 : 0xFF1B1B1B);
        resources["PillButtonBack"] = Brush(dark ? 0x26FFFFFF : 0x14000000);
        resources["PillButtonHover"] = Brush(dark ? 0x40FFFFFF : 0x26000000);
    }

    public static SolidColorBrush Dot(StateColor color) => color switch
    {
        StateColor.Red => Brush(0xFFE5484D),
        StateColor.Yellow => Brush(0xFFF5B000),
        StateColor.Green => Brush(0xFF2EA043),
        _ => Brush(0xFF8A8A8A),
    };

    private static SolidColorBrush Brush(long argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }
}
