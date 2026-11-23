using System.Security.Cryptography;
using System.Text;
using Freihand.Core.Recovery;
using Freihand.Core.Segments;
using Freihand.Windows.Recovery;

namespace Freihand.Windows.Tests.Recovery;

/// <summary>
/// D01 und D08, Teil Datei: echtes DPAPI im Benutzerkontext. Braucht
/// keinen Desktop, nur einen angemeldeten Benutzer. Erfundene Texte.
/// </summary>
public sealed class WindowsDataProtectorTests : IDisposable
{
    private const string Marker = "KLARTEXT-MARKE-8RX";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Freihand-D01-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void D01_ProtectThenUnprotectGivesTheSameBytes()
    {
        var protector = new WindowsDataProtector();
        var plain = Encoding.UTF8.GetBytes("Grüezi " + Marker);

        var sealedData = protector.Protect(plain);

        Assert.Equal(plain, protector.Unprotect(sealedData));
        Assert.NotEqual(plain, sealedData);
    }

    [Fact]
    public void D01_AnotherEntropyCannotDecrypt()
    {
        var sealedData = new WindowsDataProtector().Protect(Encoding.UTF8.GetBytes(Marker));

        Assert.ThrowsAny<CryptographicException>(() => new WindowsDataProtector("Anderer.Zusatzwert.v1").Unprotect(sealedData));
    }

    [Fact]
    public void D01_ChangedDataCannotBeDecrypted()
    {
        var protector = new WindowsDataProtector();
        var sealedData = protector.Protect(Encoding.UTF8.GetBytes(Marker));
        sealedData[sealedData.Length / 2] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(sealedData));
    }

    [Fact]
    public void D01_EmptyDataWorks()
    {
        var protector = new WindowsDataProtector();

        Assert.Empty(protector.Unprotect(protector.Protect([])));
    }

    [Fact]
    public void D01_TheEncryptedDataNeverContainsThePlaintext()
    {
        var plain = Encoding.UTF8.GetBytes(Marker);

        var sealedData = new WindowsDataProtector().Protect(plain);

        Assert.True(sealedData.AsSpan().IndexOf(plain) < 0, "Klartext in der verschlüsselten Nutzlast");
    }

    [Fact]
    public void D08_TheDraftFileOnDiskHasNoPlaintextAndIsReadBackAfterARestart()
    {
        using (var store = new RecoveryStore(new PhysicalRecoveryFileSystem(_folder), new WindowsDataProtector(), TimeProvider.System))
        {
            var draft = store.StartDraft();
            draft.Append(new TextSegment(Guid.NewGuid(), Marker + " und Umlaute äöü", SegmentState.Fixed, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
            draft.Flush();
        }

        var file = Assert.Single(Directory.GetFiles(_folder));
        Assert.EndsWith(".draft", file, StringComparison.Ordinal);
        var bytes = File.ReadAllBytes(file);
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Marker)) < 0, "Klartext (UTF-8) in der Datei");
        Assert.True(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(Marker)) < 0, "Klartext (UTF-16) in der Datei");

        using var restart = new RecoveryStore(new PhysicalRecoveryFileSystem(_folder), new WindowsDataProtector(), TimeProvider.System);
        Assert.Equal(Marker + " und Umlaute äöü", Assert.Single(restart.Scan().Drafts).Text);

        // Mit fremdem Zusatzwert: beschädigt gemeldet, nicht angeboten, nie Klartext
        using var foreign = new RecoveryStore(new PhysicalRecoveryFileSystem(_folder), new WindowsDataProtector("Fremd.v1"), TimeProvider.System);
        var scan = foreign.Scan();
        Assert.Empty(scan.Drafts);
        var issue = Assert.Single(scan.Issues);
        Assert.Equal(RecoveryIssueKind.Damaged, issue.Kind);
        Assert.DoesNotContain(Marker, issue.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
