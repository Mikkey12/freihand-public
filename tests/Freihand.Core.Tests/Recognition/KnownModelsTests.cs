using Freihand.Adapters.Recognition;

namespace Freihand.Core.Tests.Recognition;

/// <summary>Sollwerte der Modelle kommen aus dem Inventar, nicht aus der vorhandenen Datei (WE-14, Gegencheck vom 10.11.2026).</summary>
public sealed class KnownModelsTests
{
    [Theory]
    [InlineData("ggml-large-v3-turbo.bin", "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69")]
    [InlineData("GGML-SMALL.BIN", "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b")]
    [InlineData("ggml-silero-v5.1.2.bin", "29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf")]
    // Freigegeben am 21.11.2026 für das Menü „Modell“ im Geschäftspaket
    [InlineData("ggml-large-v3-turbo-q5_0.bin", "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2")]
    public void KnownFilesHaveTheirFixedSha256(string file, string expected) =>
        Assert.Equal(expected, KnownModels.Expected(Path.Combine("Modelle", file)));

    [Theory]
    [InlineData("ggml-base.bin")]
    [InlineData("ggml-large-v3.bin")]
    [InlineData("eigenes.bin")]
    public void UnknownFilesHaveNoSha256(string file) => Assert.Null(KnownModels.Expected(Path.Combine("Modelle", file)));

    [Fact]
    public void EveryValueIsLowercaseHexAndStandsInTheInventory()
    {
        var inventory = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "freigabe", "inventar.md"));
        foreach (var (file, sha) in KnownModels.Sha256)
        {
            Assert.Matches("^[0-9a-f]{64}$", sha);
            Assert.Contains($"`{file}`", inventory, StringComparison.Ordinal);
            Assert.Contains($"`{sha}`", inventory, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Freihand.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("Freihand.slnx nicht gefunden.");
    }
}
