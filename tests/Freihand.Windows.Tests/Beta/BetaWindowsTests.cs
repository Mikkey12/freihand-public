using System.Text;
using Freihand.Windows.Beta;

namespace Freihand.Windows.Tests.Beta;

/// <summary>
/// Beta: Schlüsselspeicher mit echtem DPAPI (SI-03, BT08), Autostart nur mit eigenem Eintrag
/// und nur aus dem Programmordner (BT02), ein Freihand je Sitzung und Beenden von aussen (BT01). Kein Desktop nötig.
/// </summary>
public sealed class BetaWindowsTests : IDisposable
{
    private const string FakeKey = "sk-ant-test-ERFUNDEN-0000";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Freihand-Beta-" + Guid.NewGuid().ToString("N"));

    // ---- Schlüssel -------------------------------------------------------------------------------------------------

    [Fact]
    public void TheKeyIsStoredEncryptedAndReadBack()
    {
        var store = new ApiKeyStore(Path.Combine(_folder, "schluessel.bin"));
        Assert.False(store.HasKey);
        Assert.Null(store.Load());

        store.Save("  " + FakeKey + "\r\n");

        Assert.True(store.HasKey);
        Assert.Equal(FakeKey, store.Load());
        var bytes = File.ReadAllBytes(store.Path);
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(FakeKey)) < 0, "Schlüssel im Klartext");
        Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(FakeKey)) < 0, "Schlüssel im Klartext (UTF-16)");
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void ADamagedKeyFileGivesNoKeyAndDeleteRemovesIt()
    {
        var store = new ApiKeyStore(Path.Combine(_folder, "schluessel.bin"));
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(store.Path, [1, 2, 3]);

        Assert.Null(store.Load());

        store.Delete();
        Assert.False(store.HasKey);
    }

    [Fact]
    public void AnotherProtectionCannotReadTheKey()
    {
        var store = new ApiKeyStore(Path.Combine(_folder, "schluessel.bin"));
        store.Save(FakeKey);

        // Mit dem Zusatzwert der Sicherungen lässt sich der Schlüssel nicht entschlüsseln
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            new Freihand.Windows.Recovery.WindowsDataProtector().Unprotect(File.ReadAllBytes(store.Path)));
    }

    // ---- Autostart ---------------------------------------------------------------------------------------------------

    private const string ProgramFolder = @"C:\Users\Test\AppData\Local\Programs\Freihand";

    [Fact]
    public void AutostartWritesExactlyItsOwnEntryForTheProgramFolder()
    {
        var key = new FakeRunKey { Values = { ["AnderesProgramm"] = "\"C:\\x.exe\"" } };
        var autostart = new Autostart(key, ProgramFolder, ProgramFolder + @"\Freihand.exe");

        Assert.True(autostart.CanEnable);
        Assert.False(autostart.IsEnabled);
        Assert.True(autostart.Enable());

        Assert.Equal("\"" + ProgramFolder + "\\Freihand.exe\"", key.Values["Freihand"]);
        Assert.True(autostart.IsEnabled);

        autostart.Disable();
        Assert.False(key.Values.ContainsKey("Freihand"));
        Assert.Equal("\"C:\\x.exe\"", key.Values["AnderesProgramm"]);
    }

    [Theory]
    [InlineData(@"C:\Users\Test\Projects\tmp\freihand\src\Freihand.App\bin\Release\net10.0-windows\Freihand.exe")]
    [InlineData(@"C:\Users\Test\AppData\Local\Programs\FreihandAlt\Freihand.exe")]
    public void AutostartNeverPointsToABuildFolder(string executable)
    {
        var key = new FakeRunKey();
        var autostart = new Autostart(key, ProgramFolder, executable);

        Assert.False(autostart.CanEnable);
        Assert.False(autostart.Enable());
        Assert.Empty(key.Values);
    }

    [Fact]
    public void AnEntryPointingElsewhereDoesNotCountAsEnabled()
    {
        var key = new FakeRunKey { Values = { ["Freihand"] = "\"C:\\alt\\Freihand.exe\"" } };

        Assert.False(new Autostart(key, ProgramFolder, ProgramFolder + @"\Freihand.exe").IsEnabled);
    }

    // ---- Ein Freihand je Sitzung, Beenden von aussen ---------------------------------------------------------------

    [Fact]
    public async Task OnlyOneInstanceAndExitCanBeRequestedFromOutside()
    {
        var prefix = @"Local\Freihand-Test-" + Guid.NewGuid().ToString("N");
        Assert.False(InstanceSignal.IsRunning(prefix));
        Assert.False(InstanceSignal.RequestExit(prefix));

        using (var first = InstanceSignal.TryClaim(prefix))
        {
            Assert.NotNull(first);
            Assert.True(InstanceSignal.IsRunning(prefix));
            Assert.Null(InstanceSignal.TryClaim(prefix));

            var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first!.ExitRequested += (_, _) => asked.TrySetResult();
            Assert.True(InstanceSignal.RequestExit(prefix));
            await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.False(InstanceSignal.IsRunning(prefix));
        using var again = InstanceSignal.TryClaim(prefix);
        Assert.NotNull(again);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FakeRunKey : IRunKey
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? Get(string name) => Values.GetValueOrDefault(name);

        public void Set(string name, string value) => Values[name] = value;

        public void Delete(string name) => Values.Remove(name);
    }
}
