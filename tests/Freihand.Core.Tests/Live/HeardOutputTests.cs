using Freihand.Core.Commands;
using Freihand.Core.Live;
using Freihand.Core.Recognition;
using Freihand.Core.Tests.Live.Fakes;
using static Freihand.Core.Tests.Live.LiveTestKit;

namespace Freihand.Core.Tests.Live;

/// <summary>
/// Die Wörter der Erkennung gehen nur ans Lernen (P11 S4, Codex zu PR #66): <see cref="HeardOutput"/> gibt es nur mit
/// <c>reportHeard</c>, vor dem festen Text derselben Äusserung; das Diktat sieht davon nichts.
/// </summary>
public sealed class HeardOutputTests
{
    private static async IAsyncEnumerable<AudioChunk> NoAudio()
    {
        await Task.CompletedTask;
        yield break;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheRecognizedWordsAreReportedOnlyWhenRequested(bool reportHeard)
    {
        var source = new ScriptedTranscriptSource();
        var session = new DictationSession(
            source, new DictationInterpreter(BuiltInCommands.Pilot, Interpreting()), () => CommandScope.Everywhere, reportHeard);
        var utterance = Utterance(0.5, "Heute", "Visual", ".NET");
        source.Emit(new UtteranceCompleted(utterance));
        source.End();

        var outputs = new List<DictationOutput>();
        await foreach (var output in session.RunAsync(NoAudio()).WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token))
        {
            outputs.Add(output);
        }

        Assert.Equal(["Heute Visual.NET"], FixedTexts(outputs));
        if (reportHeard)
        {
            Assert.Same(utterance, Assert.IsType<HeardOutput>(outputs[0]).Utterance);
            Assert.Single(outputs.OfType<HeardOutput>());
        }
        else
        {
            Assert.Empty(outputs.OfType<HeardOutput>());
        }
    }
}
