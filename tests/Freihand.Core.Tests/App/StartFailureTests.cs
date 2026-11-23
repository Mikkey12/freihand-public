using Freihand.Adapters.Dictation;

namespace Freihand.Core.Tests.App;

/// <summary>Ein gescheiterter Start nennt Typ und Stellen im Programm, nie die Meldung (21.11.2026).</summary>
public sealed class StartFailureTests
{
    [Fact]
    public void TypeAndPlacesWithoutTheMessage()
    {
        Exception caught;
        try
        {
            Thrower.Fail();
            throw new System.Diagnostics.UnreachableException();
        }
        catch (InvalidOperationException exception)
        {
            caught = exception;
        }

        var text = StartFailure.Describe(caught);

        Assert.StartsWith("typ=InvalidOperationException/IOException ort=", text, StringComparison.Ordinal);
        Assert.Contains("Thrower.Fail", text, StringComparison.Ordinal);
        Assert.Contains(" innen=", text, StringComparison.Ordinal);
        Assert.Contains("Thrower.Inner", text, StringComparison.Ordinal);
        Assert.DoesNotContain("geheim", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExceptionThatWasNeverThrownStillHasAType()
    {
        Assert.Equal("typ=InvalidOperationException ort=-", StartFailure.Describe(new InvalidOperationException("geheim")));
    }

    private static class Thrower
    {
        public static void Fail()
        {
            try
            {
                Inner();
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException(@"geheim C:\Users\x\text.txt", exception);
            }
        }

        public static void Inner() => throw new IOException("geheim");
    }
}
