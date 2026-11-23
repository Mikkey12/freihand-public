using System.ComponentModel;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Freihand.Windows.Recognition;

namespace Freihand.Windows.Tests.Recognition;

/// <summary>R43: Die Pipe des Erkenners gehört dem angemeldeten Benutzer und ist nur für ihn zugänglich (WE-07, SI-02).</summary>
public sealed class R43PipeAccessTests
{
    [Fact]
    public void R43_PipeNameIsFreihandPlusRandomGuid()
    {
        var first = RecognizerPipe.NewName();
        var second = RecognizerPipe.NewName();

        Assert.StartsWith("freihand-", first, StringComparison.Ordinal);
        Assert.True(Guid.TryParse(first["freihand-".Length..], out _), $"Nach dem Präfix steht keine GUID: {first}");
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void R43_PipeIsOwnedByTheCurrentUserAndOnlyAccessibleForThem()
    {
        var user = CurrentUser();
        var name = RecognizerPipe.NewName();

        // Beide Instanzen: erst die erste (FirstPipeInstance), dann die zweite.
        using var firstInstance = RecognizerPipe.CreateServer(name, first: true);
        using var secondInstance = RecognizerPipe.CreateServer(name, first: false);

        foreach (var instance in new[] { firstInstance, secondInstance })
        {
            var security = instance.GetAccessControl();

            var owner = security.GetOwner(typeof(SecurityIdentifier));
            Assert.Equal(user.Value, owner?.Value);

            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<PipeAccessRule>()
                .ToList();

            // Eine leere Liste hiesse: kein Eintrag, also entweder keine Zugriffsliste (alle dürfen) oder keiner darf.
            Assert.NotEmpty(rules);
            Assert.All(rules, rule => Assert.Equal(user.Value, rule.IdentityReference.Value));

            // Ausdrücklich: keine Einträge für Jeder, Authentifizierte Benutzer und Netzwerk.
            foreach (var forbidden in new[]
            {
                WellKnownSidType.WorldSid,
                WellKnownSidType.AuthenticatedUserSid,
                WellKnownSidType.NetworkSid,
            })
            {
                var sid = new SecurityIdentifier(forbidden, null);
                Assert.DoesNotContain(rules, rule => rule.IdentityReference.Value == sid.Value);
            }
        }
    }

    [Fact]
    public void R43_ThirdServerInstanceOfTheSameNameCannotBeCreated()
    {
        var name = RecognizerPipe.NewName();
        using var first = RecognizerPipe.CreateServer(name, first: true);
        using var second = RecognizerPipe.CreateServer(name, first: false);

        NamedPipeServerStream? third = null;
        try
        {
            // .NET meldet "Alle Pipeinstanzen sind ausgelastet" als IOException.
            Assert.ThrowsAny<IOException>(() => third = RecognizerPipe.CreateServer(name, first: false));
        }
        finally
        {
            third?.Dispose();
        }
    }

    [Fact]
    public void R43_FirstInstanceCannotBeCreatedForAnExistingName()
    {
        var name = RecognizerPipe.NewName();
        using var existing = RecognizerPipe.CreateServer(name, first: true);

        // FirstPipeInstance: Wer den Namen schon vor dem Launcher belegt hat, wird erkannt. .NET meldet das als
        // UnauthorizedAccessException; je nach Umsetzung (z. B. über CsWin32) kommt eine IOException oder Win32Exception.
        NamedPipeServerStream? duplicate = null;
        try
        {
            var exception = Record.Exception(() => duplicate = RecognizerPipe.CreateServer(name, first: true));
            Assert.True(
                exception is UnauthorizedAccessException or IOException or Win32Exception,
                $"Erwartet: der Name ist belegt und wird abgewiesen; erhalten: {exception?.GetType().Name ?? "keine Ausnahme"}.");
        }
        finally
        {
            duplicate?.Dispose();
        }
    }

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("Der angemeldete Benutzer hat keine Kennung.");
    }
}
