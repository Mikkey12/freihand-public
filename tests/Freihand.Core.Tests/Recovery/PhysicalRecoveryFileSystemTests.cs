using Freihand.Core.Recovery;

namespace Freihand.Core.Tests.Recovery;

/// <summary>Echte Dateien in einem eigenen temporären Verzeichnis (ER-06).</summary>
public sealed class PhysicalRecoveryFileSystemTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "freihand-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void DraftSurvivesRestartOnDisk()
    {
        var time = new ManualTimeProvider();
        var protector = new FakeDataProtector();
        var files = new PhysicalRecoveryFileSystem(_directory);
        Assert.Empty(files.ListFiles());

        using (var store = new RecoveryStore(files, protector, time))
        {
            var draft = store.StartDraft();
            draft.Append(new(Guid.NewGuid(), "auf der Platte", Segments.SegmentState.Fixed, TimeSpan.Zero, TimeSpan.Zero));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var name = Assert.Single(files.ListFiles());
        Assert.EndsWith(".draft", name, StringComparison.Ordinal);

        using var restarted = new RecoveryStore(new PhysicalRecoveryFileSystem(_directory), protector, time);
        Assert.Equal("auf der Platte", Assert.Single(restarted.Scan().Drafts).Text);

        restarted.DeleteAll();
        Assert.Empty(files.ListFiles());
    }

    [Fact]
    public void ReplaceOverwritesTheTarget()
    {
        var files = new PhysicalRecoveryFileSystem(_directory);
        files.WriteAllBytes("a.draft", [1]);
        files.WriteAllBytes("a.draft.tmp", [2]);

        files.Replace("a.draft.tmp", "a.draft");

        Assert.Equal(["a.draft"], files.ListFiles());
        Assert.Equal([2], files.ReadAllBytes("a.draft"));
    }

    [Theory]
    [InlineData("../ausserhalb.draft")]
    [InlineData("unter/ordner.draft")]
    [InlineData("")]
    public void RejectsPaths(string name)
    {
        var files = new PhysicalRecoveryFileSystem(_directory);

        Assert.ThrowsAny<ArgumentException>(() => files.WriteAllBytes(name, [1]));
    }

    [Fact]
    public void DeletingAMissingFileIsNoError()
    {
        new PhysicalRecoveryFileSystem(_directory).Delete("fehlt.draft");
    }
}
