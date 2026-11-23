using System.Collections.Concurrent;
using Freihand.Adapters.Recognition;
using Freihand.Windows.Recognition;

namespace Freihand.Windows.Tests.Recognition;

/// <summary>
/// R45: Der echte <c>Freihand.Recognizer.exe</c> mit nicht vorhandener Modelldatei: <c>failed</c> mit
/// <c>ModelMissing</c>, <c>Unavailable</c>, genau ein Prozessstart (BE-18, WE-14). Die Frist von 10 s ist eine Messung
/// (RM13, Konzept Kapitel 10.4), kein Teil dieses Tests: kein Test hängt von der Rechnergeschwindigkeit ab.
/// </summary>
[Collection(ProcessTestsCollection.Name)]
public sealed class R45MissingModelTests
{
    [Fact]
    public async Task R45_MissingModelMakesTheRecognizerUnavailable()
    {
        var executable = RecognizerExecutable.Locate();
        using var temp = new TempDirectory();
        var launcher = new RecordingLauncher(RecognizerLauncher.ForExecutable(executable));
        var changes = new ConcurrentQueue<RecognizerAvailability>();
        var unavailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecognizerSupervisor? host = null;

        try
        {
            host = new RecognizerSupervisor(
                launcher, TestConfigurations.MissingModel(temp.Path), ClientTimeouts.Default, TimeProvider.System);
            host.AvailabilityChanged += (_, availability) =>
            {
                changes.Enqueue(availability);
                if (availability == RecognizerAvailability.Unavailable)
                {
                    unavailable.TrySetResult();
                }
            };

            host.Start();

            // Der Hängerschutz (30 s) schützt nur vor einem Hänger; geprüft wird der Ablauf, nicht die Dauer.
            await Hangs.GuardAsync(unavailable.Task, "Wechsel auf Unavailable");

            Assert.Equal(RecognizerAvailability.Unavailable, host.Availability);
            Assert.Equal(RecognizerErrorCode.ModelMissing, host.LastError);
            Assert.Null(host.Info);
            Assert.Equal(1, launcher.LaunchCount);

            // Alles beenden und dann nochmals zählen: bleibender Fehler heisst Unavailable ohne Neustart.
            await host.DisposeAsync();
            host = null;

            Assert.Equal(1, launcher.LaunchCount);
            Assert.DoesNotContain(RecognizerAvailability.Restarting, changes);
            Assert.Equal(RecognizerAvailability.Unavailable, changes.Last());
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
}
