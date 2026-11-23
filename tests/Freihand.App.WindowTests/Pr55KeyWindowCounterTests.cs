using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Freihand.Windows.Beta;

namespace Freihand.App;

/// <summary>
/// Gegenprobe der Codex-Prüfung zu PR #55 (b7076af), als fester Regressionstest übernommen: Ein Speicherfehler im
/// Schlüsselfenster entweicht nie dem Klick. Kein Fenster wird gezeigt; eine gesperrte eigene Datei ersetzt den Fehler.
/// Dazu: Ein Schlüssel mit Zeilenumbruch wird nicht gespeichert.
/// </summary>
public sealed class Pr55KeyWindowCounterTests
{
    [Fact]
    public async Task AKeyWithALineBreakIsNotSaved()
    {
        var folder = Path.Combine(Path.GetTempPath(), "freihand-pr55-key-" + Guid.NewGuid().ToString("N"));
        var store = new ApiKeyStore(Path.Combine(folder, "key.bin"));
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            KeyWindow? window = null;
            try
            {
                window = new KeyWindow(store);
                ((PasswordBox)window.FindName("KeyBox")).Password = "sk-ant-ERFUNDEN\nZWEITE-ZEILE";
                typeof(KeyWindow).GetMethod("OnSave", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [window, new RoutedEventArgs()]);
                Assert.False(store.HasKey);
                Assert.Contains("nicht wie ein Schlüssel", ((TextBlock)window.FindName("State")).Text, StringComparison.Ordinal);
            }
            catch (Exception exception) { failure = exception; }
            finally { window?.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); finished.TrySetResult(failure); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        try
        {
            var error = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("OnSave")]
    [InlineData("OnDelete")]
    public async Task AKeyStorageFailureMustNotEscapeTheClickHandler(string method)
    {
        var path = Path.Combine(Path.GetTempPath(), "freihand-pr55-key-" + Guid.NewGuid().ToString("N"), "key.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new ApiKeyStore(path);
        store.Save("ERFUNDENER-TESTSCHLUESSEL");
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            KeyWindow? window = null;
            try
            {
                window = new KeyWindow(store);
                ((PasswordBox)window.FindName("KeyBox")).Password = "ERFUNDENER-NEUER-SCHLUESSEL";
                using var lockedFile = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var click = typeof(KeyWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!;
                var error = Record.Exception(() => click.Invoke(window, [window, new RoutedEventArgs()]));
                Assert.Null(error);
                Assert.False(window.IsVisible);
            }
            catch (Exception exception) { failure = exception; }
            finally { window?.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); finished.TrySetResult(failure); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        try
        {
            var error = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!)) File.Delete(file);
            Directory.Delete(Path.GetDirectoryName(path)!);
        }
    }
}
