using Freihand.Core.Platform;

namespace Freihand.Adapters.Audio;

/// <summary>Fehler der Aufnahme mit Stoppgrund; die Meldung enthält nur den Grund.</summary>
public sealed class CaptureBackendException(CaptureStopReason reason) : Exception($"Aufnahme gescheitert: {reason}.")
{
    public CaptureStopReason Reason { get; } = reason;
}

/// <summary>Ein Puffer vom Gerät.</summary>
/// <param name="Buffer">Verschachtelte Frames im <see cref="ICaptureBackend.Format"/>; 0 Byte zählen nicht als Daten.</param>
/// <param name="Discontinuity">WASAPI meldet vor diesem Puffer eine Unterbrechung des Datenstroms.</param>
/// <param name="MissingFrames">Zahl der fehlenden Geräteframes aus der Geräteposition; null, wenn nicht bestimmbar.</param>
public sealed record CaptureData(ReadOnlyMemory<byte> Buffer, bool Discontinuity, long? MissingFrames);

/// <summary>Zugang zu einem Gerät (Windows: WASAPI über NAudio; Stufe A: Attrappe). Dispose gibt das Gerät frei.</summary>
public interface ICaptureBackend : IDisposable
{
    CaptureFormat Format { get; }

    event EventHandler<CaptureData>? DataAvailable;

    /// <summary>Null nach <see cref="Stop"/>, sonst Grund des Fehlers.</summary>
    event EventHandler<CaptureStopReason?>? Stopped;

    /// <summary>Stummschalter des Geräts in Windows beim Öffnen; null, wenn nicht bekannt.</summary>
    bool? EndpointMuted => null;

    /// <summary>Der Stummschalter hat gewechselt (wahr: stumm); auf beliebigem Thread.</summary>
    event EventHandler<bool>? MuteChanged
    {
        add { }
        remove { }
    }

    /// <exception cref="CaptureBackendException">Das Gerät lässt sich nicht starten.</exception>
    void Start();

    void Stop();
}

/// <summary>Geräteliste ohne Aufnahmezugang (Windows: MMDeviceEnumerator; Stufe A: Attrappe).</summary>
/// <remarks>Ergänzung zu Kapitel 4.3, damit <see cref="AudioCapture"/> plattformunabhängig prüfbar ist (R42).</remarks>
public interface IAudioDeviceCatalog
{
    IReadOnlyList<AudioDevice> Devices { get; }

    AudioDevice? DefaultDevice { get; }

    event EventHandler? DevicesChanged;
}

/// <summary>Zeitwerte der Aufnahme (P8-Konzept, Kapitel 4.3).</summary>
/// <param name="ChunkDuration">Länge eines Blocks, Startwert 200 ms.</param>
/// <param name="NoSignalTimeout">Nach den ersten Daten ohne Daten so lange → <see cref="CaptureStopReason.NoSignal"/>, Startwert 1 s.</param>
/// <param name="SilentSignalHint">So lange exakte Nullen → <see cref="CaptureStatus.SilentSignal"/>, Startwert 5 s.</param>
/// <param name="MaxQueuedAudio">Ungelesenes Audio, danach <see cref="CaptureStopReason.QueueFull"/>, Startwert 5 s.</param>
/// <param name="FirstDataTimeout">
/// Nach <c>Open</c> ohne erste Daten so lange → <see cref="CaptureStopReason.NoSignal"/>, Startwert 3 s. Bluetooth-Headsets
/// liefern erst nach rund 1 s (RM10, Entscheid vom 09.11.2026).
/// </param>
public sealed record CaptureOptions(
    TimeSpan ChunkDuration,
    TimeSpan NoSignalTimeout,
    TimeSpan SilentSignalHint,
    TimeSpan MaxQueuedAudio,
    TimeSpan FirstDataTimeout)
{
    /// <summary>Die Startwerte aus Kapitel 4.3.</summary>
    public static CaptureOptions Default { get; } =
        new(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(3));
}
