using System.Buffers.Binary;
using System.Reflection;
using Freihand.Adapters.Dictation;
using Freihand.Benchmark;
namespace Freihand.Benchmark.Tests;
public sealed class Pr65PruefungCounterTests
{
    [Fact]
    public void ALeadStartingInsideAnAudioGapMustKeepTheFollowingSection()
    {
        var header = new ReviewHeader(1, 1, DateTimeOffset.UnixEpoch, 16000, 0,
            [new ReviewChunk(0, 0, 32000), new ReviewChunk(5000, 32000, 32000)], []);
        var audio = Enumerable.Range(0, 32000).Select(i => (float)i).ToArray();
        var method = typeof(Pruefung).GetMethod("Slice", BindingFlags.NonPublic | BindingFlags.Static)!;
        var actual = (float[])method.Invoke(null, [audio, header, 4500L, 5500L])!;
        Assert.Equal(audio[16000..24000], actual);
    }

    [Fact]
    public void WavHeaderMatchesSyntheticMonoPcm16()
    {
        var path = Path.Combine(Path.GetTempPath(), "pr65-wav-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            var method = typeof(Pruefung).GetMethod("WriteWav", BindingFlags.NonPublic | BindingFlags.Static)!;
            var write = method.CreateDelegate<WriteWav>();
            byte[] pcm = [1, 2, 3, 4];
            write(path, pcm, 16000);
            var wav = File.ReadAllBytes(path);
            Assert.Equal(48, wav.Length);
            Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
            Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
            Assert.Equal("WAVEfmt "u8.ToArray(), wav[8..16]);
            Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(16)));
            Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));
            Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
            Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
            Assert.Equal(32000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)));
            Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(32)));
            Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
            Assert.Equal("data"u8.ToArray(), wav[36..40]);
            Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
            Assert.Equal(pcm, wav[44..]);
        }
        finally { File.Delete(path); }
    }
    private delegate void WriteWav(string path, ReadOnlySpan<byte> pcm, int sampleRate);
}