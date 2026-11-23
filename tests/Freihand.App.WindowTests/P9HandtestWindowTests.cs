using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Freihand.Core.Recovery;
using Freihand.Core.Segments;

namespace Freihand.App;

/// <summary>
/// Handtest P9 vom 11.11.2026, Diktatfenster unsichtbar geprüft: Gerettet (gelb) ist nur, was nirgends angekommen ist;
/// „Alle löschen“ fragt nach und lässt die Schreibfläche stehen; „Leeren“ hat Alt+L. Kein Fenster wird gezeigt, kein
/// Dialog erscheint, kein Fokus wechselt.
/// </summary>
public sealed class P9HandtestWindowTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Freihand-P9H-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public Task OnlyRescuedDraftsCountAndHandedOverOnesAreMarked() => OnWindow(_folder, (window, store) =>
    {
        Saved(store, "Abgebrochen", handOver: false);
        Saved(store, "Schon im Feld", handOver: true);

        window.Refresh();

        Assert.Equal(1, window.Offers);
        var summaries = Summaries(window);
        Assert.Equal(2, summaries.Count);
        Assert.DoesNotContain("übernommen", summaries[0], StringComparison.Ordinal);
        Assert.StartsWith("übernommen · ", summaries[1], StringComparison.Ordinal);
    });

    [Fact]
    public Task DeleteAllAsksAndDeletesOnlyOnYesKeepingThePad() => OnWindow(_folder, (window, store) =>
    {
        Saved(store, "Eins", handOver: false);
        Saved(store, "Zwei", handOver: true);
        var pad = (TextBox)window.FindName("Pad");
        pad.Text = "Bleibt stehen";
        window.Refresh();
        var asked = new List<int>();
        var answer = false;
        window.ConfirmDeleteAllOverride = count =>
        {
            asked.Add(count);
            return answer;
        };

        DeleteAll(window);
        Assert.Equal([2], asked);
        Assert.Equal(2, store.Scan().Drafts.Count);

        answer = true;
        DeleteAll(window);
        Assert.Empty(store.Scan().Drafts);
        Assert.Equal(0, window.Offers);
        Assert.Equal("Bleibt stehen", pad.Text);
    });

    [Fact]
    public void ClearHasAltLAndNoLongerAltR()
    {
        var xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Freihand.App", "DictationWindow.xaml"));

        Assert.Contains("Content=\"_Leeren\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("_r", xaml.Replace("_Leeren", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static void Saved(RecoveryStore store, string text, bool handOver)
    {
        var draft = store.StartDraft();
        draft.Append(new TextSegment(Guid.NewGuid(), text, SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        if (handOver)
        {
            draft.MarkHandedOver();
        }
        else
        {
            draft.Dispose();
        }
    }

    private static List<string> Summaries(DictationWindow window) =>
        [.. ((ItemsControl)window.FindName("Drafts")).Items.Cast<object>()
            .Select(item => (string)item.GetType().GetProperty("Summary")!.GetValue(item)!)];

    private static void DeleteAll(DictationWindow window) =>
        typeof(DictationWindow).GetMethod("OnDeleteAll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, new RoutedEventArgs()]);

    /// <summary>Fenster auf eigenem STA-Thread, nie gezeigt; die Grenze ist nur Notbremse.</summary>
    private static async Task OnWindow(string folder, Action<DictationWindow, RecoveryStore> check)
    {
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            DictationWindow? window = null;
            Exception? failure = null;
            var store = new RecoveryStore(new PhysicalRecoveryFileSystem(folder), new XorProtector(), TimeProvider.System);
            try
            {
                window = new DictationWindow { RevealOverride = () => { } };
                window.AttachRecovery(store, copyAllowed: true);
                check(window, store);
                Assert.False(window.IsVisible, "Die Prüfung zeigt nie ein Fenster");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                // Erst Fenster und Ablage schliessen, dann melden: sonst löscht der Test den Ordner während des letzten Sicherns
                window?.CloseForExit();
                store.Dispose();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                finished.TrySetResult(failure);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var failure = await finished.Task.WaitAsync(TimeSpan.FromSeconds(120));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Freihand.slnx")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("Freihand.slnx nicht gefunden");
    }

    /// <summary>Statt DPAPI: umkehrbar verschleiert; keine Verschlüsselung.</summary>
    private sealed class XorProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext) => [.. plaintext.Select(b => (byte)(b ^ 0x5A))];

        public byte[] Unprotect(byte[] protectedData) => [.. protectedData.Select(b => (byte)(b ^ 0x5A))];
    }
}
