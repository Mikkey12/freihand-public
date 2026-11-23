using Freihand.Adapters.Dictation;
using Freihand.Adapters.Recognition;
using Freihand.Core.Live;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Recognition;
using Freihand.Core.Recovery;
using Freihand.Core.Tests.App;

// Testanschlüsse für die unverändert eingebundene App.xaml.cs. Kein echtes WPF, Fenster, Mikrofon oder Modell.
namespace System.Windows
{
    public class StartupEventArgs : EventArgs { public string[] Args { get; init; } = []; }
    public class ExitEventArgs : EventArgs;
    public class Application
    {
        public object? MainWindow { get; set; }
        public Threading.Dispatcher Dispatcher { get; } = new();
        protected virtual void OnStartup(StartupEventArgs e) { }
        protected virtual void OnExit(ExitEventArgs e) { }
        public void Shutdown() { }
    }
}
namespace System.Windows.Threading
{
    public sealed class Dispatcher
    {
        public void BeginInvoke(Action action) => action();
        public void Invoke(Action action) => action();
    }
}
namespace Freihand.Windows { internal static class NamespaceMarker; }
namespace Freihand.Windows.Audio { internal static class NamespaceMarker; }
namespace Freihand.Windows.Recognition { internal static class NamespaceMarker; }
namespace Freihand.Windows.Recovery { internal static class NamespaceMarker; }
namespace Freihand.Windows.Beta { internal static class NamespaceMarker; }
namespace Freihand.App
{
    // App.xaml.cs kopiert über PrivateClipboard (Zwischenablage ohne Verlauf und Cloud); hier ohne WPF
    internal static class PrivateClipboard
    {
        public static void SetText(string text) => throw new NotSupportedException();
        public static void Register(bool copyAllowed) { }
    }

    // Lokale Namen binden nur in dieser Prüfassembly; der Produktquelltext bleibt unangetastet.
    internal static class Environment
    {
        internal enum SpecialFolder { LocalApplicationData }
        internal static string Folder = string.Empty;
        internal static string GetFolderPath(SpecialFolder folder) => Folder;
    }
    public sealed class MainWindow
    {
        internal static TaskCompletionSource<string> StartupMessage = NewMessage();
        internal static TaskCompletionSource<string> NewMessage() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<Guid, bool>? CopyHeld { get; set; }
#pragma warning disable CS0067 // Schliessen lösen C06/C07 nicht aus
        public event System.ComponentModel.CancelEventHandler? Closing;
#pragma warning restore CS0067
        public void Show() { }
        public void Hide() { }
        public void Show(ControllerUpdate update) { }
        public void ShowKeys(string text) { }
        public void ShowStartup(string text)
        {
            if (text.StartsWith("Start gescheitert:", StringComparison.Ordinal)) StartupMessage.TrySetResult(text);
        }
    }
    internal sealed class RecognizerSupervisor : IRecognizerHost
    {
        internal static int Starts;
        internal static int Releases;
        internal static TaskCompletionSource ReleaseEntered = NewSignal();
        internal static TaskCompletionSource? ReleaseGate;
        internal static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RecognizerSupervisor(object launcher, RecognizerConfiguration configuration, ClientTimeouts timeouts, TimeProvider time) { }
        public void Start() => Interlocked.Increment(ref Starts);
        public RecognizerAvailability Availability => RecognizerAvailability.Ready;
        public event EventHandler<RecognizerLaunch>? Launched { add { } remove { } }
        public void HoldRestarts() { }
        public void ResumeRestarts() { }
        public RecognizerInfo? Info => null;
        public RecognizerErrorCode? LastError => null;
        public event EventHandler<RecognizerAvailability>? AvailabilityChanged { add { } remove { } }
        public Task<RecognizerSession> StartSessionAsync(RecognitionOptions options, LiveOptions live, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task RestartAsync() => throw new NotSupportedException();
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Releases);
            ReleaseEntered.TrySetResult();
            if (ReleaseGate is { } gate) await gate.Task;
        }
    }
    internal static class RecognizerLauncher { internal static object ForExecutable(string path) => new(); }
    internal sealed class WindowsTargets : IDisposable
    {
        internal static bool Fail;
        private readonly EventLog _log = new();
        public WindowsTargets(TargetRuleTable table, OutputOptions options)
        {
            if (Fail) throw new InvalidOperationException("Künstlicher Startfehler");
            Binding = new FakeBinding(_log);
            Output = new FakeTextOutput(_log);
            Guard = new FakeGuard(_log);
            Hotkeys = new FakeHotkeys(_log);
        }
        public ITargetBinding Binding { get; }
        public ITargetMarker Marker => (ITargetMarker)Binding;
        public ITextOutput Output { get; }
        public IRecordingGuard Guard { get; }
        public IHotkeyService Hotkeys { get; }
        public IForeignContentReader? ForeignContent => null;
        public void Dispose() { }
    }
    internal static class WasapiAudio { public static IAudioCapture Create() => new FakeAudioCapture(new EventLog()); }

    /// <summary>Statt benannter Ereignisse: jede Prüfung beansprucht ihre eigene Sitzung (Beta B1).</summary>
    /// <summary>Ohne echtes Fenster kein Schutz fürs Sitzungsende; der Zusammenbau läuft wie ohne.</summary>
    internal sealed class SessionEndGuard : IDisposable
    {
        public static nint ApplicationWindow(object application) => 0;

        public static SessionEndGuard? Attach(nint window, Action sessionEnds) => null;

        public static SessionEndGuard? Attach(nint window, Action sessionEnds, Action? sessionQueried, Action? sessionContinues) => null;

        public void Dispose()
        {
        }
    }

    internal sealed class InstanceSignal : IDisposable
    {
        internal static int ExitRequests;
#pragma warning disable CS0067 // die Prüfungen beenden über OnExit
        public event EventHandler? ExitRequested;
#pragma warning restore CS0067
        public static InstanceSignal? TryClaim() => new();
        public static bool RequestExit() { Interlocked.Increment(ref ExitRequests); return false; }
        public void Dispose() { }
    }

    /// <summary>Statt DPAPI: umkehrbar verschleiert, damit die Prüfung ohne Windows läuft; keine Verschlüsselung.</summary>
    internal sealed class WindowsDataProtector : IDataProtector
    {
        public WindowsDataProtector() { }
        public WindowsDataProtector(string entropy) { }
        public byte[] Protect(byte[] plaintext) => [.. plaintext.Select(b => (byte)(b ^ 0x5A))];
        public byte[] Unprotect(byte[] protectedData) => [.. protectedData.Select(b => (byte)(b ^ 0x5A))];
    }

    /// <summary>Anzeige ohne Fenster: hält fest, was angeboten und gemeldet wird (D04).</summary>
    internal sealed class Shell : IDisposable
    {
        internal static TaskCompletionSource<RecoveryScan> Offered = NewOffer();
        internal static readonly List<string> Startups = [];
        internal static TaskCompletionSource<RecoveryScan> NewOffer() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Shell(MainWindow diagnostics, string folder) { }
#pragma warning disable CS0067 // Beenden lösen die Prüfungen über OnExit aus
        public event EventHandler? ExitRequested;
#pragma warning restore CS0067
        public IDictationPad? Pad => null;
        public void AttachRecovery(RecoveryStore store, RecoveryScan scan, bool copyAllowed) => Offered.TrySetResult(scan);
        public void Connect(DictationController controller, PrototypeSettings settings, string settingsPath) { }
        public void ConfigureBeta(PrototypeSettings settings, string settingsPath) { }
        public void AttachProtocol(DailyLog log, ProblemMarker marker) { }
#pragma warning disable CS0067 // Aufwachen gibt es in den Prüfungen nicht
        public event EventHandler? Resumed;
#pragma warning restore CS0067
        public void AttachReview(ReviewRecorder review) { }
        public void AttachWords(
            WordStore words, WordListener? listener, Freihand.Core.Commands.CommandSet commands,
            Freihand.Core.Live.InterpreterOptions? interpreting) { }
        public void AttachFieldMeasurement(MeasuredFieldStore store, TargetRuleTable table) { }
        public void Show(ControllerUpdate update) { }
        public void ShowStartup(string text) { lock (Startups) Startups.Add(text); }
        public void Dispose() { }
    }
}
