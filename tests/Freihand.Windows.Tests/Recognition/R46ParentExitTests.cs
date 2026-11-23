using System.Diagnostics;
using System.Globalization;
using Freihand.Adapters.Recognition;
using Freihand.Windows.Recognition;

namespace Freihand.Windows.Tests.Recognition;

/// <summary>
/// R46: Der Erkenner, gestartet mit <c>--parent</c> eines Hilfsprozesses, endet von selbst, nachdem der Hilfsprozess
/// beendet wurde (BE-18). Die Frist von 2 s ist eine Messung (RM13, Konzept Kapitel 10.4), kein Teil dieses Tests.
/// </summary>
[Collection(ProcessTestsCollection.Name)]
public sealed class R46ParentExitTests
{
    [Fact]
    public async Task R46_RecognizerEndsAfterItsParent()
    {
        var executable = RecognizerExecutable.Locate();
        using var parent = HelperProcess.Start();
        var launcher = new RecordingLauncher(new RecognizerLauncher(pipeName =>
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("--pipe");
            info.ArgumentList.Add(pipeName);
            info.ArgumentList.Add("--parent");
            info.ArgumentList.Add(parent.Id.ToString(CultureInfo.InvariantCulture));
            return info;
        }));

        try
        {
            var launched = await Hangs.GuardAsync(launcher.LaunchAsync(), "LaunchAsync");

            // Erst weitermachen, wenn der Erkenner läuft: Er verbindet sich als Erstes auf "decode". Endet er schon vorher,
            // ist das ein eigener Befund und keine Antwort auf das Ende des Hilfsprozesses.
            using var timeout = new CancellationTokenSource(Hangs.Limit);
            var accept = launched.AcceptAsync(timeout.Token);
            if (await Task.WhenAny(accept, launched.Exited) == launched.Exited)
            {
                Assert.Fail("Der Erkenner endete, bevor er sich mit der Pipe verbunden hat.");
            }

            PipeConnection connection;
            try
            {
                connection = await accept;
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("Der Erkenner hat sich nicht innerhalb des Hängerschutzes mit der Pipe verbunden.");
                return;
            }

            // Die Verbindung bleibt offen: Der Erkenner wartet auf "configure" und muss trotzdem enden.
            await using var serverStream = connection.Stream;
            Assert.False(launched.Exited.IsCompleted, "Der Erkenner lief nicht mehr, obwohl sein Elternprozess lebt.");

            // Hilfsprozess beenden; der Erkenner muss danach von selbst enden. Der Hängerschutz prüft keine Dauer.
            await parent.KillAndWaitAsync();
            await Hangs.GuardAsync(launched.Exited, "Ende des Erkenners");
        }
        finally
        {
            launcher.KillAll();
        }
    }
}
