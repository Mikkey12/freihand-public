using Freihand.Adapters.Fakes;
using Freihand.Core.Recognition;
using Freihand.Core.Segments;

namespace Freihand.Core.Tests.Adapters;

public class ScriptedRecognitionEngineTests
{
    private static readonly RecognitionOptions Options = new("de-CH", []);

    [Fact]
    public async Task YieldsOneScriptedUpdatePerAudioChunk()
    {
        var first = Update(provisional: "Guten");
        var second = Update(@fixed: "Guten Tag.");
        var engine = new ScriptedRecognitionEngine([first, second]);

        var updates = await engine.RecognizeAsync(Chunks(3), Options).ToListAsync();

        Assert.Equal([first, second], updates);
    }

    [Fact]
    public async Task StopsWhenCancelled()
    {
        var engine = new ScriptedRecognitionEngine([Update(provisional: "Hallo")]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await engine.RecognizeAsync(Chunks(1), Options, cts.Token).ToListAsync());
    }

    private static RecognitionUpdate Update(string? @fixed = null, string? provisional = null) => new(
        @fixed is null ? [] : [Segment(@fixed, SegmentState.Fixed)],
        provisional is null ? [] : [Segment(provisional, SegmentState.Provisional)]);

    private static TextSegment Segment(string text, SegmentState state) =>
        new(Guid.NewGuid(), text, state, TimeSpan.Zero, TimeSpan.FromSeconds(1));

    private static async IAsyncEnumerable<AudioChunk> Chunks(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Yield();
            yield return new AudioChunk(new byte[320], 16_000, TimeSpan.FromMilliseconds(10 * i));
        }
    }
}
