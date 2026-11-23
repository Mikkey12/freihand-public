using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Freihand.Adapters.Dictation;
using Freihand.Windows.Beta;

namespace Freihand.App;

/// <summary>
/// Handtest vom 18.11.2026 am Lernfenster (P11 S4): „Schliessen“ reagierte nicht auf Klick (IsCancel schliesst nur modale
/// Fenster), und nach einem Fehler liess sich nicht von vorne beginnen. Dasselbe galt für das Schlüsselfenster. Kein
/// Fenster wird gezeigt.
/// </summary>
public sealed class LearnWindowTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "freihand-lernen-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static async Task OnSta(Action action)
    {
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                finished.TrySetResult(failure);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var error = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private LearnWindow Learn() =>
        new(new WordStore(Path.Combine(_folder, "woerter.txt")), null, Core.Commands.BuiltInCommands.Pilot, null, () => null, () => true, null);

    private static Core.Live.Utterance Words(params string[] words) =>
        new(Guid.NewGuid(),
            [.. words.Select((text, k) => new Core.Recognition.RecognizedWord(text, TimeSpan.FromSeconds(k), TimeSpan.FromSeconds(k + 0.5), 1))],
            TimeSpan.Zero, TimeSpan.FromSeconds(words.Length), Core.Live.UtteranceEnd.Pause);

    private static void Click(Window window, string name) =>
        ((ButtonBase)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    [Fact]
    public Task TheCloseButtonClosesTheLearnWindowOnClick() => OnSta(() =>
    {
        var window = Learn();
        var closed = false;
        window.Closed += (_, _) => closed = true;

        Click(window, "CloseButton");

        Assert.True(closed);
    });

    [Fact]
    public Task TheCloseButtonClosesTheKeyWindowOnClick() => OnSta(() =>
    {
        var window = new KeyWindow(new ApiKeyStore(Path.Combine(Path.GetTempPath(), "freihand-lernen-key-" + Guid.NewGuid().ToString("N"), "key.bin")));
        var closed = false;
        window.Closed += (_, _) => closed = true;

        Click(window, "CloseButton");

        Assert.True(closed);
    });

    // Eigene Vorprüfung zu PR #66: gespeichert wird genau für die Schreibweise, für die geprüft wurde
    [Fact]
    public Task ChangingTheSpellingAfterListeningChecksTheProposalsAgain() => OnSta(() =>
    {
        var window = Learn();
        try
        {
            var box = (TextBox)window.FindName("SpellingBox");
            box.Text = "Bluetenpark";
            var heard = (List<Core.Live.Utterance>)typeof(LearnWindow).GetField("_heard", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            heard.Add(Words("Heute", "habe", "ich", "Blüt", "Park", "erwähnt."));
            typeof(LearnWindow).GetMethod("ShowCandidates", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, ["Bluetenpark", null]);
            Assert.True(((Button)window.FindName("SaveButton")).IsEnabled);

            box.Text = "Blüt Park";

            // Jetzt ist das Gehörte genau die Schreibweise: richtig erkannt, nichts zu speichern
            var candidates = (StackPanel)window.FindName("Candidates");
            var only = Assert.IsType<CheckBox>(Assert.Single(candidates.Children));
            Assert.False(only.IsEnabled);
            Assert.False(((Button)window.FindName("SaveButton")).IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task StartingOverDropsEverythingHeardAndKeepsTheSpelling() => OnSta(() =>
    {
        var window = Learn();
        try
        {
            ((TextBox)window.FindName("SpellingBox")).Text = "Nivella";
            var heard = (List<Core.Live.Utterance>)typeof(LearnWindow).GetField("_heard", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            heard.AddRange([Words("Heute", "habe", "ich", "Niwella", "erwähnt."), Words("Heute", "habe", "ich", "Niwela", "erwähnt.")]);
            typeof(LearnWindow).GetMethod("ShowCandidates", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, ["Nivella", null]);
            var candidates = (StackPanel)window.FindName("Candidates");
            Assert.Equal(2, candidates.Children.Count);

            Click(window, "ResetButton");

            Assert.Empty(candidates.Children);
            Assert.Empty(heard);
            Assert.False(((Button)window.FindName("SaveButton")).IsEnabled);
            Assert.Equal("Nivella", ((TextBox)window.FindName("SpellingBox")).Text);
            Assert.True(((TextBox)window.FindName("SpellingBox")).IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AMissingMicrophoneRemainsVisibleAfterCandidateRefresh() => OnSta(() =>
    {
        var store = new WordStore(Path.Combine(_folder, "woerter.txt"));
        var listener = new WordListener(new NoRun(), new NoAudio());
        var window = new LearnWindow(store, listener, Core.Commands.BuiltInCommands.Pilot, null, () => null, () => true, null);
        try
        {
            ((TextBox)window.FindName("SpellingBox")).Text = "Testziel";
            Click(window, "ListenButton");
            Assert.Contains("Kein Mikrofon gefunden", ((TextBlock)window.FindName("State")).Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    });

    private sealed class NoRun : IDictationFactory
    {
        public Task<IDictationRun> StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class NoAudio : Core.Platform.IAudioCapture
    {
        public IReadOnlyList<Core.Platform.AudioDevice> Devices => [];
        public Core.Platform.AudioDevice? DefaultDevice => null;
        public event EventHandler? DevicesChanged { add { } remove { } }
        public Core.Platform.ICaptureSession Open(Core.Platform.AudioDevice device) => throw new InvalidOperationException();
    }
}
