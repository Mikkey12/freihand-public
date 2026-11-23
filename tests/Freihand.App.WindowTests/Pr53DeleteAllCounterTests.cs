using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Freihand.Core.Recovery;
using Freihand.Core.Tests.Recovery;

namespace Freihand.App;

/// <summary>
/// Gegenprobe der Codex-Prüfung von PR #53 (Befund 2), übernommen. Regel: Wird die Sicherung der Schreibfläche von aussen
/// gelöscht, wird ihr Text sofort neu gesichert, nicht erst im nächsten Takt. Kein Fenster, kein Dialog.
/// </summary>
public sealed class Pr53DeleteAllCounterTests
{
    [Fact]
    public async Task DeleteAllWithUnreadableFilesMustImmediatelyPersistExistingPadText()
    {
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var clock = new ManualTimeProvider();
            var files = new InMemoryRecoveryFileSystem(clock);
            var protector = new FakeDataProtector();
            using var store = new RecoveryStore(files, protector, clock);
            DictationWindow? window = null;
            Exception? failure = null;
            try
            {
                window = new DictationWindow { RevealOverride = () => { } };
                window.AttachRecovery(store, true);
                ((TextBox)window.FindName("Pad")).Text = "old unsent pad text";
                clock.Advance(RecoveryOptions.MaxSaveInterval);
                Assert.NotEmpty(files.Files);

                // An unreadable entry was reported by Scan; no dialog or actual window is opened.
                window.Offer(new RecoveryScan([], [new RecoveryIssue(RecoveryIssueKind.Damaged, "bad.draft", null, "damaged")]));
                window.ConfirmDeleteAllOverride = _ => true;
                typeof(DictationWindow).GetMethod("OnDeleteAll", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [window, new RoutedEventArgs()]);

                // Simulate a crash now: do not Dispose the pad or advance the save timer before checking the on-disk state.
                using var restarted = new RecoveryStore(files, protector, clock);
                Assert.Contains(restarted.Scan().Drafts, d => d.Text == "old unsent pad text");
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                window?.CloseForExit();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                finished.TrySetResult(failure);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        // Nur Notbremse, kein Prüfkriterium (Lauf 43016909307)
        var failure = await finished.Task.WaitAsync(TimeSpan.FromSeconds(120));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
