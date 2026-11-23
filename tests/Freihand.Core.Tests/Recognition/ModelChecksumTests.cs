using Freihand.Adapters.Recognition;
using Freihand.Core.Tests.Recognition.Kit;

namespace Freihand.Core.Tests.Recognition;

/// <summary>
/// R31: Prüfsumme bei jedem Laden über einen gegen Schreiben gesperrten Handle, kein Cache über Grösse und Änderungszeit
/// (P8-Konzept, Kapitel 5.4, WE-14). Echte temporäre Dateien aus 1 KiB Zufallsdaten (<see cref="ModelFiles"/>); jeder
/// „neue Prozess“ ist ein neuer <see cref="RecognizerService"/> mit eigener Lade-Attrappe.
/// <para>Die Teilprüfungen zur Schreibsperre laufen nur unter Windows (<see cref="WindowsFactAttribute"/>): Unter Linux
/// setzt .NET <c>FileShare</c> nur als beratende Sperre um, ein fremdes Öffnen zum Schreiben scheitert dort nicht. Stufe A
/// in der CI (Linux) prüft darum die Prüfsumme selbst, die Sperre prüft der Windows-Rechner.</para>
/// </summary>
public sealed class ModelChecksumTests
{
    [Fact]
    public async Task R31_WrongChecksumFailsWithoutLoading()
    {
        using var files = ModelFiles.Create();
        await using var service = await ServiceHarness.CreateAsync(files.Configuration() with { ModelSha256 = ModelFiles.Sha256([1, 2, 3]) });

        var answer = await service.ConfigureAsync();

        Assert.Equal("failed", answer.Type);
        Assert.Equal(RecognizerErrorCode.ModelChecksum, answer.Code);
        Assert.Empty(service.Loader.Calls);
    }

    [Fact]
    public async Task R31_WrongVadChecksumFailsWithoutLoading()
    {
        using var files = ModelFiles.Create();
        await using var service = await ServiceHarness.CreateAsync(files.Configuration() with { VadSha256 = ModelFiles.Sha256([1, 2, 3]) });

        var answer = await service.ConfigureAsync();

        Assert.Equal("failed", answer.Type);
        Assert.Equal(RecognizerErrorCode.ModelChecksum, answer.Code);
        Assert.Empty(service.Loader.Calls);
    }

    [Fact]
    public async Task R31_CorrectChecksumLoadsExactlyOnceInEveryProcess()
    {
        using var files = ModelFiles.Create();
        var configuration = files.Configuration();

        await using (var first = await ServiceHarness.CreateAsync(configuration))
        {
            Assert.Equal("ready", (await first.ConfigureAsync()).Type);
            Assert.Single(first.Loader.Calls);
        }

        await using var second = await ServiceHarness.CreateAsync(configuration);
        Assert.Equal("ready", (await second.ConfigureAsync()).Type);
        Assert.Single(second.Loader.Calls);
    }

    /// <summary>
    /// Gegenfall gleiche Metadaten: Nach einem erfolgreichen Laden ändert sich ein Byte, Grösse und Änderungszeit sind
    /// zurückgesetzt. Der nächste Prozess muss die Prüfsumme neu berechnen und scheitert; ein Cache über Grösse und
    /// Änderungszeit (auch ein prozessübergreifender, statischer) würde hier laden.
    /// </summary>
    [Fact]
    public async Task R31_ChangedByteWithSameMetadataIsDetected()
    {
        using var files = ModelFiles.Create();
        var configuration = files.Configuration();
        await using (var first = await ServiceHarness.CreateAsync(configuration))
        {
            Assert.Equal("ready", (await first.ConfigureAsync()).Type);
        }

        files.CorruptModelKeepingMetadata();

        await using var second = await ServiceHarness.CreateAsync(configuration);
        var answer = await second.ConfigureAsync();
        Assert.Equal("failed", answer.Type);
        Assert.Equal(RecognizerErrorCode.ModelChecksum, answer.Code);
        Assert.Empty(second.Loader.Calls);
    }

    /// <summary>Während Prüfung und Laden lässt sich keine der beiden Dateien zum Schreiben öffnen, löschen oder umbenennen.</summary>
    [WindowsFact]
    public async Task R31_FilesAreLockedAgainstWritingWhileLoading()
    {
        using var files = ModelFiles.Create();
        var attempts = new List<(string File, string Action, bool Succeeded)>();
        await using var service = await ServiceHarness.CreateAsync(files.Configuration(), loader => loader.DuringLoad = configuration =>
        {
            foreach (var path in new[] { configuration.ModelPath, configuration.VadModelPath })
            {
                var name = Path.GetFileName(path);
                attempts.Add((name, "schreiben", Try(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose())));
                attempts.Add((name, "umbenennen", Try(() => File.Move(path, path + ".neu"))));
                attempts.Add((name, "löschen", Try(() => File.Delete(path))));
            }
        });

        Assert.Equal("ready", (await service.ConfigureAsync()).Type);

        Assert.Equal(6, attempts.Count);
        Assert.All(attempts, attempt => Assert.False(attempt.Succeeded, $"{attempt.File} liess sich beim Laden {attempt.Action}."));
    }

    /// <summary>Hält ein anderer eine der Dateien zum Schreiben offen: <c>ModelChecksum</c>, kein Ladeaufruf.</summary>
    [WindowsFact]
    public async Task R31_FileHeldOpenForWritingFailsWithChecksumError()
    {
        foreach (var vad in new[] { false, true })
        {
            using var files = ModelFiles.Create();
            using var writer = new FileStream(vad ? files.VadPath : files.ModelPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            await using var service = await ServiceHarness.CreateAsync(files.Configuration());

            var answer = await service.ConfigureAsync();

            Assert.Equal("failed", answer.Type);
            Assert.Equal(RecognizerErrorCode.ModelChecksum, answer.Code);
            Assert.Empty(service.Loader.Calls);
        }
    }

    private static bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
