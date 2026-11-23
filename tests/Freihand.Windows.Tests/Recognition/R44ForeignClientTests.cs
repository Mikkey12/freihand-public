using System.IO.Pipes;
using Freihand.Adapters.Recognition;
using Freihand.Windows.Recognition;

namespace Freihand.Windows.Tests.Recognition;

/// <summary>
/// R44: Der Testprozess verbindet sich als Client mit der Pipe eines Launchers, der einen anderen Prozess gestartet hat.
/// Der Launcher meldet die Prozesskennung des Testprozesses; der Supervisor trennt, ohne eine Nachricht zu lesen oder zu
/// senden (WE-07).
/// </summary>
[Collection(ProcessTestsCollection.Name)]
public sealed class R44ForeignClientTests
{
    [Fact]
    public async Task R44_LauncherReportsTheProcessIdOfTheConnectedClient()
    {
        // Gestartet wird ein harmloser Hilfsprozess; der Name der Pipe kommt als Argument in die Fabrik.
        var pipeName = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new RecordingLauncher(new RecognizerLauncher(name =>
        {
            pipeName.TrySetResult(name);
            return HelperProcess.StartInfo();
        }));

        try
        {
            var launched = await Hangs.GuardAsync(launcher.LaunchAsync(), "LaunchAsync");
            var name = await Hangs.GuardAsync(pipeName.Task, "Name der Pipe");

            // Der gestartete Prozess ist der Hilfsprozess, nicht der Testprozess.
            Assert.NotEqual(Environment.ProcessId, launched.ProcessId);

            using var timeout = new CancellationTokenSource(Hangs.Limit);
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);

            var connection = await launched.AcceptAsync(timeout.Token);
            await using var serverStream = connection.Stream;

            Assert.Equal(Environment.ProcessId, connection.ClientProcessId);
        }
        finally
        {
            launcher.KillAll();
        }
    }

    [Fact]
    public async Task R44_SupervisorDisconnectsAForeignClientWithoutReadingOrSending()
    {
        using var temp = new TempDirectory();
        var pipeName = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new RecordingLauncher(new RecognizerLauncher(name =>
        {
            pipeName.TrySetResult(name);
            return HelperProcess.StartInfo();
        }));
        RecognizerSupervisor? host = null;

        try
        {
            host = new RecognizerSupervisor(
                launcher, TestConfigurations.MissingModel(temp.Path), ClientTimeouts.Default, TimeProvider.System);
            host.Start();

            var name = await Hangs.GuardAsync(pipeName.Task, "Name der Pipe");

            // Der Testprozess ist nicht der gestartete Hilfsprozess. Er schickt nichts und liest nichts, bevor die
            // Verbindung endet: Hätte der Supervisor zuerst eine Nachricht gelesen (der echte Erkenner sendet "hello"),
            // bliebe die Verbindung offen; hätte er gesendet (etwa "configure"), kämen Bytes an.
            using var timeout = new CancellationTokenSource(Hangs.Limit);
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);

            var received = await ReadUntilConnectionEndsAsync(client, timeout.Token);

            Assert.Equal(0, received);
        }
        finally
        {
            launcher.KillAll();
            if (host is not null)
            {
                await host.DisposeAsync();
            }
        }
    }

    /// <summary>Liest bis zum Ende der Verbindung und gibt die Zahl der empfangenen Byte zurück (0 erwartet).</summary>
    private static async Task<int> ReadUntilConnectionEndsAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        try
        {
            // Ein einziges empfangenes Byte genügt als Befund; weiterlesen muss der Test dann nicht.
            return await stream.ReadAsync(buffer, cancellationToken);
        }
        catch (IOException)
        {
            // Ein abgebrochenes Rohr ist das Verbindungsende, ohne dass ein Byte ankam.
            return 0;
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Der Supervisor hat die fremde Verbindung nicht innerhalb des Hängerschutzes getrennt.");
            return -1;
        }
    }
}
