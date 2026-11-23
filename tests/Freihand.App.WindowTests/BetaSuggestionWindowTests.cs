using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Freihand.Adapters.Dictation;
using Freihand.Adapters.Transform;
using Freihand.Core.Platform;
using Freihand.Core.Routing;
using Freihand.Core.Transform;

namespace Freihand.App;

/// <summary>
/// BT09 im Vorschlagsfenster: Vorschlag zuerst, Ersetzen nur auf Verlangen und mit der
/// gelesenen Markierung als Bedingung, was nicht ersetzt wurde, bleibt kopierbar, ohne Markierung kein Aufruf.
/// Kein Fenster wird gezeigt, kein Dienst wird aufgerufen.
/// </summary>
public sealed class BetaSuggestionWindowTests
{
    private const string Route = "online · Test";

    [Fact]
    public Task TheSuggestionComesFirstAndReplacesTheReadSelection() => OnWindow(async window =>
    {
        var calls = new List<(string Text, TargetMark Mark, string Replacing)>();
        window.HandOver = (text, mark, replacing) =>
        {
            calls.Add((text, mark, replacing));
            return Task.FromResult(new HandOverReport(HandOverResult.Written, string.Empty, null));
        };
        var mark = Mark();

        await window.RunAsync(Request(mark, "Wir komen morgen.\n"), Service(_ => "Wir kommen morgen."), Route, "Strg+Alt+F");

        Assert.Empty(calls);
        Assert.Equal(Visibility.Visible, ((ScrollViewer)window.FindName("PreviewHost")).Visibility);
        Assert.True(((Button)window.FindName("ReplaceButton")).IsEnabled);
        Assert.Equal(Route, Text(window, "RouteLine"));

        await window.ReplaceAsync();

        var call = Assert.Single(calls);
        Assert.Equal("Wir kommen morgen.\n", call.Text);
        Assert.Same(mark, call.Mark);
        Assert.Equal("Wir komen morgen.\n", call.Replacing);
        await window.ReplaceAsync();
        Assert.Single(calls);
    });

    [Theory]
    [InlineData(SelectionRefusal.NoSelection, "Nichts markiert")]
    [InlineData(SelectionRefusal.SpecialContent, "Tabelle")]
    [InlineData(SelectionRefusal.Unreadable, "Diktatfenster")]
    public Task WithoutAReadableSelectionNothingIsSent(SelectionRefusal refusal, string hint) => OnWindow(async window =>
    {
        var asked = 0;
        var request = new CorrectionRequest(CorrectionKind.Correct, new MarkResult(Mark(), null), SelectionRead.Refused(refusal), false);

        await window.RunAsync(request, Service(text => { asked++; return text; }), Route, "Strg+Alt+F");

        Assert.Equal(0, asked);
        Assert.Contains(hint, Text(window, "Hint"), StringComparison.Ordinal);
        Assert.False(((Button)window.FindName("ReplaceButton")).IsEnabled);
    });

    [Fact]
    public Task WithoutAFieldThereIsAHint() => OnWindow(async window =>
    {
        var request = new CorrectionRequest(CorrectionKind.Smooth, new MarkResult(null, BindRefusal.PasswordField),
            SelectionRead.Refused(SelectionRefusal.Unreadable), false);

        await window.RunAsync(request, Service(text => text), Route, "Strg+Alt+G");

        Assert.Contains("Kein Textfeld erkannt (PasswordField)", Text(window, "Hint"), StringComparison.Ordinal);
        Assert.Equal("Glätten", Text(window, "Heading"));
    });

    [Theory]
    [InlineData(HandOverResult.Refused, "SelectionChanged", "", "Markierung hat sich", "Wir kommen morgen.")]
    [InlineData(HandOverResult.Partial, "FocusElsewhere", "morgen.", "teilweise", "morgen.")]
    public Task WhatWasNotReplacedStaysCopyable(HandOverResult result, string reason, string held, string hint, string copied) =>
        OnWindow(async window =>
        {
            var clipboard = new List<string>();
            window.CopyOverride = clipboard.Add;
            window.HandOver = (text, _, _) => Task.FromResult(new HandOverReport(result, held.Length == 0 ? text : held, reason));

            await window.RunAsync(Request(Mark(), "Wir komen morgen."), Service(_ => "Wir kommen morgen."), Route, "Strg+Alt+F");
            await window.ReplaceAsync();

            Assert.Contains(hint, Text(window, "Hint"), StringComparison.Ordinal);
            var copy = (Button)window.FindName("CopyButton");
            Assert.Equal(Visibility.Visible, copy.Visibility);
            copy.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal([copied], clipboard);
        });

    [Fact]
    public Task DiscardReplacesNothing() => OnWindow(async window =>
    {
        var calls = 0;
        window.HandOver = (_, _, _) =>
        {
            calls++;
            return Task.FromResult(new HandOverReport(HandOverResult.Written, string.Empty, null));
        };

        await window.RunAsync(Request(Mark(), "Wir komen morgen."), Service(_ => "Wir kommen morgen."), Route, "Strg+Alt+F");
        window.OnDiscard(window, new RoutedEventArgs());
        await window.ReplaceAsync();

        Assert.Equal(0, calls);
    });

    [Fact]
    public Task TheReadTextGoesOutConfidential() => OnWindow(async window =>
    {
        var classes = new List<DataClass>();
        var service = new CorrectionService(() => new FakeTransformer(_ => "Anders.", classes), text => text, TimeSpan.FromSeconds(5));

        await window.RunAsync(Request(Mark(), "Original."), service, Route, "Strg+Alt+F");

        Assert.Equal([DataClass.Confidential], classes);
    });

    private static CorrectionRequest Request(TargetMark mark, string selection) =>
        new(CorrectionKind.Correct, new MarkResult(mark, null),
            SelectionText.From(selection, ReadPurpose.ExplicitCommand, DataClass.Confidential), false);

    private static TargetMark Mark() =>
        new("testeditor", FieldKinds.DocumentBody, "Testeditor Dokumenttext",
            new ElementIdentity(42, 4242, 7, "1.2.3", "Document", "TestEdit", ""),
            new TargetRule("testeditor", null, null, null, null, FieldKinds.DocumentBody, "Testeditor Dokumenttext",
                LineBreakMode.Refuse, 20, IdentityMode.RuntimeId));

    private static string Text(SuggestionWindow window, string name) => ((TextBlock)window.FindName(name)).Text;

    private static CorrectionService Service(Func<string, string> answer) =>
        new(() => new FakeTransformer(answer, []), text => text, TimeSpan.FromSeconds(5));

    /// <summary>Fenster auf eigenem STA-Thread mit laufendem Dispatcher, nie gezeigt; die Grenze ist nur Notbremse.</summary>
    private static async Task OnWindow(Func<SuggestionWindow, Task> body)
    {
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var window = new SuggestionWindow { RevealOverride = () => { } };
            dispatcher.BeginInvoke(new Func<Task>(async () =>
            {
                Exception? failure = null;
                try
                {
                    await body(window);
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

    private sealed class FakeTransformer(Func<string, string> answer, List<DataClass> classes) : ITextTransformer
    {
        public TransformRoute Route => TransformRoute.PrivateProvider;

        public Task<TransformResult> TransformAsync(TransformRequest request, CancellationToken cancellationToken = default)
        {
            classes.Add(request.DataClass);
            return Task.FromResult(new TransformResult(answer(request.Text)));
        }
    }
}
