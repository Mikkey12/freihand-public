using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Freihand.Adapters.Transform;
using Freihand.Core.Routing;
using Freihand.Core.Transform;

namespace Freihand.App;

/// <summary>
/// BT08 im Diktatfenster: Vorschlag neben dem Original, nichts ersetzt ohne „Ersetzen“,
/// nur die Auswahl, nie nach inzwischen geändertem Text, ohne Schlüssel ein Hinweis.
/// Kein Fenster wird gezeigt, kein Dienst wird aufgerufen.
/// </summary>
public sealed class BetaCorrectionWindowTests
{
    [Fact]
    public Task TheSuggestionIsShownAndReplacesOnlyOnRequest() => OnWindow(async (window, pad) =>
    {
        window.AttachCorrector(Service(_ => "Ein korrigierter Satz.\nZweite Zeile."), "online · Test");
        pad.Text = "Ein korigierter Satz.\r\nZweite Zeile.";

        await window.SuggestAsync(CorrectionKind.Correct);

        Assert.Equal(Visibility.Visible, Preview(window).Visibility);
        Assert.Equal("Ein korigierter Satz.\r\nZweite Zeile.", pad.Text);

        window.OnReplace(window, new RoutedEventArgs());

        Assert.Equal("Ein korrigierter Satz.\r\nZweite Zeile.", pad.Text);
        Assert.Equal(Visibility.Collapsed, Preview(window).Visibility);

        // Strg+Z: ein nie angezeigtes Textfeld führt keine Rückgängig-Liste; Ersetzen nutzt denselben Weg wie „Leeren“,
        // dessen Strg+Z im Handtest H9 bestanden hat. Rückgängig nach „Ersetzen“ prüft der Handtest (BT07)
    });

    [Fact]
    public Task OnlyTheSelectionIsSentAndReplaced() => OnWindow(async (window, pad) =>
    {
        var sent = new List<string>();
        window.AttachCorrector(Service(text => { sent.Add(text); return "zweiter"; }), "online · Test");
        pad.Text = "erster zweitr dritter";
        pad.Select(7, 6);

        await window.SuggestAsync(CorrectionKind.Correct);
        window.OnReplace(window, new RoutedEventArgs());

        Assert.Equal(["zweitr"], sent);
        Assert.Equal("erster zweiter dritter", pad.Text);
    });

    [Fact]
    public Task TextChangedSinceTheRequestIsNeverOverwritten() => OnWindow(async (window, pad) =>
    {
        window.AttachCorrector(Service(_ => "Vorschlag."), "online · Test");
        pad.Text = "Original.";

        await window.SuggestAsync(CorrectionKind.Smooth);
        pad.Text = "Original. Neu geschrieben.";
        window.OnReplace(window, new RoutedEventArgs());

        Assert.Equal("Original. Neu geschrieben.", pad.Text);
        Assert.Equal(Visibility.Collapsed, Preview(window).Visibility);
    });

    [Fact]
    public Task DiscardLeavesTheTextAndWithoutAKeyThereIsAHint() => OnWindow(async (window, pad) =>
    {
        window.AttachCorrector(Service(_ => "Anders."), "online · Test");
        pad.Text = "Bleibt.";
        await window.SuggestAsync(CorrectionKind.Correct);
        window.OnDiscard(window, new RoutedEventArgs());
        Assert.Equal("Bleibt.", pad.Text);

        window.AttachCorrector(new CorrectionService(() => null, text => text, TimeSpan.FromSeconds(5)), "online · Test");
        await window.SuggestAsync(CorrectionKind.Correct);

        Assert.Equal(Visibility.Collapsed, Preview(window).Visibility);
        Assert.Contains("Kein Schlüssel", ((TextBlock)window.FindName("Hint")).Text, StringComparison.Ordinal);
        Assert.Equal("online · Test", ((TextBlock)window.FindName("RouteLine")).Text);
    });

    private static Border Preview(DictationWindow window) => (Border)window.FindName("PreviewHost");

    private static CorrectionService Service(Func<string, string> answer) =>
        new(() => new FakeTransformer(answer), text => text, TimeSpan.FromSeconds(5));

    /// <summary>Fenster auf eigenem STA-Thread mit laufendem Dispatcher, nie gezeigt; die Grenze ist nur Notbremse.</summary>
    private static async Task OnWindow(Func<DictationWindow, TextBox, Task> body)
    {
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var window = new DictationWindow { RevealOverride = () => { } };
            dispatcher.BeginInvoke(new Func<Task>(async () =>
            {
                Exception? failure = null;
                try
                {
                    await body(window, (TextBox)window.FindName("Pad"));
                    Assert.False(window.IsVisible, "Die Prüfung zeigt nie ein Fenster");
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    window.CloseForExit();
                    dispatcher.InvokeShutdown();
                    finished.TrySetResult(failure);
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(120));
        if (result is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(result).Throw();
        }
    }

    private sealed class FakeTransformer(Func<string, string> answer) : ITextTransformer
    {
        public TransformRoute Route => TransformRoute.PrivateProvider;

        public Task<TransformResult> TransformAsync(TransformRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransformResult(answer(request.Text)));
    }
}
