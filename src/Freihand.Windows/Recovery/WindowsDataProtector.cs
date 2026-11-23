using System.Security.Cryptography;
using Freihand.Core.Recovery;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Cryptography;

namespace Freihand.Windows.Recovery;

/// <summary>
/// Verschlüsselung der Sicherung mit DPAPI im Benutzerkontext:
/// Nur der angemeldete Benutzer auf diesem Gerät kann entschlüsseln. Ein eigener Zusatzwert trennt die Sicherung von
/// anderen DPAPI-Daten desselben Benutzers; es gibt keinen eigenen Schlüssel und nichts zu verwalten. Nie mit
/// Rückfrage an den Benutzer (<c>CRYPTPROTECT_UI_FORBIDDEN</c>).
/// </summary>
public sealed class WindowsDataProtector : IDataProtector
{
    /// <summary>Zusatzwert der Sicherung, Fassung 1.</summary>
    public const string DefaultEntropy = "Freihand.Wiederherstellung.v1";

    private const uint UiForbidden = 0x1;

    private readonly byte[] _entropy;

    public WindowsDataProtector()
        : this(DefaultEntropy)
    {
    }

    /// <param name="entropy">Zusatzwert; ein anderer Wert kann die Daten nicht entschlüsseln (D01).</param>
    public WindowsDataProtector(string entropy)
    {
        ArgumentException.ThrowIfNullOrEmpty(entropy);
        _entropy = System.Text.Encoding.UTF8.GetBytes(entropy);
    }

    /// <exception cref="CryptographicException">DPAPI hat die Daten nicht verschlüsselt.</exception>
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Call(plaintext, protect: true);
    }

    /// <exception cref="CryptographicException">Daten beschädigt, verändert, anderer Zusatzwert oder anderer Benutzer.</exception>
    public byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        return Call(protectedData, protect: false);
    }

    private unsafe byte[] Call(byte[] data, bool protect)
    {
        // Leere Daten: DPAPI lehnt einen Nullzeiger ab, darum ein Platzhalter mit Länge 0 (wie ProtectedData in .NET)
        byte placeholder = 0;
        fixed (byte* buffer = data)
        fixed (byte* entropy = _entropy)
        {
            var input = data.Length == 0 ? &placeholder : buffer;
            var inBlob = new CRYPT_INTEGER_BLOB { cbData = (uint)data.Length, pbData = input };
            var entropyBlob = new CRYPT_INTEGER_BLOB { cbData = (uint)_entropy.Length, pbData = entropy };
            var outBlob = default(CRYPT_INTEGER_BLOB);
            var ok = protect
                ? PInvoke.CryptProtectData(&inBlob, null, &entropyBlob, null, null, UiForbidden, &outBlob)
                : PInvoke.CryptUnprotectData(&inBlob, null, &entropyBlob, null, null, UiForbidden, &outBlob);
            if (!ok)
            {
                // Nur der Fehlercode, nie Daten (SI-01)
                throw new CryptographicException(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            }

            try
            {
                return new ReadOnlySpan<byte>(outBlob.pbData, (int)outBlob.cbData).ToArray();
            }
            finally
            {
                // Klartext nach dem Entschlüsseln im fremden Puffer überschreiben, dann freigeben
                new Span<byte>(outBlob.pbData, (int)outBlob.cbData).Clear();
                PInvoke.LocalFree((HLOCAL)(nint)outBlob.pbData);
            }
        }
    }
}
