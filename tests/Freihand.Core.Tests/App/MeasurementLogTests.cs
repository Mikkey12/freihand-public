using System.Globalization;
using Freihand.Adapters.Dictation;

namespace Freihand.Core.Tests.App;

/// <summary>Messprotokoll (RM10, RM11): nur mit <c>FREIHAND_MESSPROTOKOLL</c> entsteht eine Datei.</summary>
public sealed class MeasurementLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "freihand-messprotokoll-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void N01_WithoutVariableNoFileAppearsAndTheInnerLogStillGetsTheLine(string? value)
    {
        Directory.CreateDirectory(_directory);
        var inner = new List<string>();

        var log = MeasurementLog.Create(name => name == MeasurementLog.VariableName ? value : null, inner.Add);
        log("phase=Idle start_ms=- stopp_ms=-");

        Assert.Equal(["phase=Idle start_ms=- stopp_ms=-"], inner);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public void N02_WithPathEachLineIsAppendedWithTimestampAndTheInnerLogStillGetsIt()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "mess.log");
        var inner = new List<string>();
        var before = DateTimeOffset.UtcNow;

        var log = MeasurementLog.Create(name => name == MeasurementLog.VariableName ? path : null, inner.Add);
        log("phase=Recording start_ms=212 stopp_ms=-");
        log("phase=Idle start_ms=212 stopp_ms=1034");

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal(2, inner.Count);
        var parts = lines[0].Split(' ', 2);
        Assert.True(DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stamp));
        Assert.InRange(stamp, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal("phase=Recording start_ms=212 stopp_ms=-", parts[1]);
        Assert.EndsWith("phase=Idle start_ms=212 stopp_ms=1034", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void N03_UnwritablePathNeverThrowsAndCreatesNoDirectory()
    {
        var path = Path.Combine(_directory, "gibt-es-nicht", "mess.log");
        var log = MeasurementLog.Create(name => name == MeasurementLog.VariableName ? path : null, null);

        log("phase=Idle start_ms=- stopp_ms=-");

        Assert.False(Directory.Exists(_directory));
    }
}
