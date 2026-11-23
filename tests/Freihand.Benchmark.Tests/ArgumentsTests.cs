namespace Freihand.Benchmark.Tests;

/// <summary>Grenzwerte der Befehlszeile (M05 und M06 aus dem Codex-Review).</summary>
public sealed class ArgumentsTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void M05_StepMustBeFiniteAndPositive(string value)
    {
        var arguments = Arguments.Parse(["run", "--step", value]);

        Assert.Throws<ArgumentException>(() => arguments.Positive("step", 1.0));
    }

    [Fact]
    public void M05_ZeroIsAllowedWhereStated()
    {
        var arguments = Arguments.Parse(["run", "--guard", "0"]);

        Assert.Equal(0, arguments.Positive("guard", 1.0, allowZero: true));
    }

    [Fact]
    public void M05_FallbackIsUsedWithoutOption()
    {
        Assert.Equal(0.6, Arguments.Parse(["run"]).Positive("pause", 0.6));
    }

    [Fact]
    public void M05_ThreadsBelowMinimumAreRejected()
    {
        var arguments = Arguments.Parse(["run", "--threads", "0"]);

        Assert.Throws<ArgumentException>(() => arguments.Int("threads", 4, minimum: 1));
    }

    [Fact]
    public void M06_AmbiguousParakeetFolderIsRejected()
    {
        var folder = Directory.CreateTempSubdirectory("freihand-parakeet-");
        try
        {
            foreach (var name in new[] { "encoder.int8.onnx", "encoder.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" })
            {
                File.WriteAllBytes(Path.Combine(folder.FullName, name), [0]);
            }

            var error = Assert.Throws<ArgumentException>(() => new ParakeetTranscriber(folder.FullName, threads: 1));
            Assert.Contains("mehrdeutig", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
