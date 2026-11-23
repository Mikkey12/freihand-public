using Freihand.Core.Platform;
using Freihand.Core.Recognition;
using Freihand.Windows.Audio;

namespace Freihand.Windows.Tests.Audio;

/// <summary>
/// R47: WASAPI ohne Mikrofon (der Server der automatischen Prüfung hat keines): <c>Devices</c> liefert eine Liste ohne
/// Ausnahme; <c>Open</c> mit einer nicht vorhandenen Kennung ergibt <c>Stopped(DeviceMissing)</c> ohne Ausnahme (ER-13).
/// </summary>
public sealed class R47WasapiWithoutMicrophoneTests
{
    /// <summary>Eine Endpunktkennung im Format von Windows, die es auf keinem Rechner gibt.</summary>
    private static readonly AudioDevice PhantomDevice =
        new("{0.0.1.00000000}.{00000000-dead-beef-0000-000000000047}", "Erfundenes Mikrofon");

    [Fact]
    public void R47_DevicesAreListedWithoutException()
    {
        var capture = WasapiAudio.Create();

        var devices = capture.Devices;
        var defaultDevice = capture.DefaultDevice;

        // Ohne Mikrofon ist die Liste leer und es gibt kein Standardgerät; mit Mikrofon sind es echte Einträge.
        Assert.NotNull(devices);
        Assert.All(devices, device => Assert.False(string.IsNullOrEmpty(device.Id), "Gerät ohne Kennung."));
        if (devices.Count == 0)
        {
            Assert.Null(defaultDevice);
        }
    }

    [Fact]
    public async Task R47_OpeningAnUnknownDeviceStopsWithDeviceMissing()
    {
        var capture = WasapiAudio.Create();

        ICaptureSession? opened = null;
        var exception = Record.Exception(() => opened = capture.Open(PhantomDevice));

        Assert.Null(exception);
        Assert.NotNull(opened);
        await using var session = opened;

        Assert.Equal(PhantomDevice, session.Device);
        Assert.Equal(CapturePhase.Stopped, session.Status.Phase);
        Assert.Equal(CaptureStopReason.DeviceMissing, session.Status.StopReason);
        Assert.Null(session.Status.StartLatency);

        // Der Stopp ist regulär beendet: Der Strom endet ohne Fehler und ohne Audio; nie ein Ausweichen auf ein anderes
        // Mikrofon (ER-13). Der Token schützt nur vor einem Hänger.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var chunks = new List<AudioChunk>();
        await foreach (var chunk in session.ReadAsync(timeout.Token))
        {
            chunks.Add(chunk);
        }

        Assert.Empty(chunks);
    }
}
