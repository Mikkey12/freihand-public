using Freihand.Core.Platform;
using Freihand.Zielprobe;

[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility",
    Scope = "member", Target = "~M:Freihand.Zielprobe.Countdown.Warten(System.Int32,System.String)",
    Justification = "The linked source is tested only with a zero countdown.")]

namespace Freihand.Core.Tests.App
{
    public sealed class Pr70FactsAnchorCounterTests
    {
        [Fact]
        public void AFocusChangeDuringFactsCannotAnchorTheSecondField()
        {
            _ = Fakten.Ausfuehren(Optionen.Lesen(["fakten", "--wartezeit", "0", "--name", "test", "--out", "unused.json"]));

            Assert.NotEqual("41.2", Fakten.LetzteRuntimeId);
        }
    }
}

