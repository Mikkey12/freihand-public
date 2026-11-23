namespace Freihand.Core.Recovery;

/// <summary>
/// Verschlüsselt die Sicherung lokal (ER-06, Grundentscheid 2). Unter Windows mit DPAPI im
/// Benutzerkontext; der Kern kennt nur diese Schnittstelle.
/// </summary>
public interface IDataProtector
{
    /// <summary>Verschlüsselt die Daten; lesbar nur für diesen Benutzer auf diesem Gerät.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>Entschlüsselt mit <see cref="Protect"/> erzeugte Daten.</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Die Daten sind beschädigt, verändert oder gehören einem anderen Benutzer.
    /// </exception>
    byte[] Unprotect(byte[] protectedData);
}
