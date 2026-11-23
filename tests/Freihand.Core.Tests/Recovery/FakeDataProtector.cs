using System.Security.Cryptography;
using System.Text;
using Freihand.Core.Recovery;

namespace Freihand.Core.Tests.Recovery;

/// <summary>
/// Attrappe statt DPAPI: verschleiert umkehrbar und hält die Klartexte fest, damit Tests den Inhalt der
/// Nutzlast prüfen können. Keine echte Verschlüsselung.
/// </summary>
internal sealed class FakeDataProtector : IDataProtector
{
    private static readonly byte[] Prefix = "FAKE"u8.ToArray();

    public List<byte[]> Plaintexts { get; } = [];

    /// <summary>Stellt einen fremden Benutzer oder ein anderes Gerät nach: Entschlüsseln scheitert.</summary>
    public bool FailUnprotect { get; set; }

    public IEnumerable<string> PlaintextStrings => Plaintexts.Select(p => Encoding.UTF8.GetString(p));

    public byte[] Protect(byte[] plaintext)
    {
        Plaintexts.Add([.. plaintext]);
        return [.. Prefix, .. plaintext.Select(b => (byte)(b ^ 0x5A))];
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        if (FailUnprotect || protectedData.Length < Prefix.Length || !protectedData.AsSpan(0, Prefix.Length).SequenceEqual(Prefix))
        {
            throw new CryptographicException("Schlüssel ungültig.");
        }

        return [.. protectedData.Skip(Prefix.Length).Select(b => (byte)(b ^ 0x5A))];
    }
}
