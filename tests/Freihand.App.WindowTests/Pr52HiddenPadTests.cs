using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Freihand.Adapters.Dictation;
using Freihand.Core.Platform;

namespace Freihand.App;

/// <summary>
/// Gegenprobe der Codex-Prüfung von PR #52 (Befund 2), übernommen, dazu der gleichartige Fall beim Teilerfolg. Regel: Nach
/// dem Übernehmen wird die Schreibfläche nur verändert, wenn sie seit dem Beginn des Übernehmens unverändert ist; neue
/// Eingaben gehen nie verloren. Kein Fenster wird gezeigt, kein Fokus wechselt, kein SendInput, keine echte Steuerung.
/// </summary>
public sealed class Pr52HiddenPadTests
{
    private static readonly TargetMark Mark = new(
        "synthetic", FieldKinds.DocumentBody, "synthetic", new ElementIdentity(1, 1, 1, "1", "Document", "Edit", ""),
        new TargetRule("synthetic", null, null, null, null, FieldKinds.DocumentBody, "synthetic", LineBreakMode.Refuse, 20, IdentityMode.RuntimeId));

    // Befund 2: Übernahmeabschluss löscht neue Eingaben
    [Fact]
    public Task CompletingTransferMustNotEraseNewPadEdits() =>
        Run(new HandOverReport(HandOverResult.Written, "", null), "original", "new unsent input", "new unsent input");

    // Gleichartig: Ein Teilerfolg überschreibt neue Eingaben nicht mit dem Rest
    [Fact]
    public Task APartialTransferMustNotOverwriteNewPadEdits() =>
        Run(new HandOverReport(HandOverResult.Partial, "nal", "FocusElsewhere"), "original", "new unsent input", "new unsent input");

    // Ohne neue Eingaben wie bisher: ganz übernommen leert, Teilerfolg lässt den Rest stehen
    [Fact]
    public Task WithoutNewEditsAWrittenTransferEmptiesThePad() =>
        Run(new HandOverReport(HandOverResult.Written, "", null), "original", null, string.Empty);

    [Fact]
    public Task WithoutNewEditsAPartialTransferLeavesTheRest() =>
        Run(new HandOverReport(HandOverResult.Partial, "nal", "FocusElsewhere"), "original", null, "nal");

    /// <summary>Startet Übernehmen, ändert während des Wartens allenfalls den Text, schliesst ab und prüft die Schreibfläche.</summary>
    private static async Task Run(HandOverReport result, string before, string? editWhilePending, string expected)
    {
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            DictationWindow? window = null;
            try
            {
                // Kein Fenster zeigen, keinen Fokus bewegen (Codex-Deltaprüfung PR #52)
                window = new DictationWindow { RevealOverride = () => { } };
                var pad = (TextBox)window.FindName("Pad");
                typeof(DictationWindow).GetProperty("Mark")!.SetValue(window, Mark);
                var transfer = new TaskCompletionSource<HandOverReport>();
                window.HandOver = (_, _) => transfer.Task;
                pad.Text = before;
                typeof(DictationWindow).GetMethod("OnHandOver", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [window, new RoutedEventArgs()]);

                // Wieder geöffnet, während das Übernehmen noch läuft: neue Eingaben, ohne das Fenster zu zeigen
                if (editWhilePending is not null)
                {
                    pad.Text = editWhilePending;
                }

                transfer.SetResult(result);
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    try
                    {
                        Assert.Equal(expected, pad.Text);
                        Assert.False(window.IsVisible, "Die Prüfung zeigt nie ein Fenster");
                        finished.TrySetResult(null);
                    }
                    catch (Exception exception)
                    {
                        finished.TrySetResult(exception);
                    }
                    finally
                    {
                        window.CloseForExit();
                        dispatcher.InvokeShutdown();
                    }
                }));
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                finished.TrySetResult(exception);
                window?.CloseForExit();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        // Nur Notbremse, kein Prüfkriterium: Der erste WPF-Start lag in der CI parallel zu den Kerntests über 15 s
        // (Lauf 43016909307), dieselbe Prüfung vorher 7 s für alle vier; kein Test hängt von der Geschwindigkeit ab
        var failure = await finished.Task.WaitAsync(TimeSpan.FromSeconds(120));
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
