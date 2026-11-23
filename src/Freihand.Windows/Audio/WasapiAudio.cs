using System.Runtime.InteropServices;
using Freihand.Adapters.Audio;
using Freihand.Core.Platform;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Freihand.Windows.Audio;

/// <summary>
/// Aufnahme über WASAPI im gemeinsamen Modus (P8-Konzept, Kapitel 5.2, 5.3 und 10.5): Geräteliste ohne Aufnahmezugang,
/// Zugang erst in <see cref="IAudioCapture.Open"/>. Die Puffer liest der Windows-Teil selbst über
/// <see cref="AudioCaptureClient.GetBuffer(out int, out AudioClientBufferFlags, out long, out long)"/>, weil nur dort
/// Discontinuity-Flag und Geräteposition ankommen.
/// </summary>
public static class WasapiAudio
{
    /// <summary>Die Aufnahme mit Startwerten und Systemuhr.</summary>
    public static IAudioCapture Create() => Create(CaptureOptions.Default, TimeProvider.System);

    public static IAudioCapture Create(CaptureOptions options, TimeProvider time) =>
        new AudioCapture(new WasapiDeviceCatalog(), device => new WasapiCaptureBackend(device.Id), options, time);

    /// <summary>Aktive Aufnahmegeräte und Standardgerät der Rolle „Konsole“; öffnet kein Mikrofon (ER-17).</summary>
    private sealed class WasapiDeviceCatalog : IAudioDeviceCatalog
    {
        private readonly MMDeviceEnumerator _enumerator = new();
        private readonly MMDeviceNotificationClient _notifications;

        public WasapiDeviceCatalog()
        {
            _notifications = _enumerator.CreateNotificationClient(false);
            _notifications.DeviceAdded += (_, _) => DevicesChanged?.Invoke(this, EventArgs.Empty);
            _notifications.DeviceRemoved += (_, _) => DevicesChanged?.Invoke(this, EventArgs.Empty);
            _notifications.DeviceStateChanged += (_, _) => DevicesChanged?.Invoke(this, EventArgs.Empty);
            _notifications.DefaultDeviceChanged += (_, _) => DevicesChanged?.Invoke(this, EventArgs.Empty);
        }

        public IReadOnlyList<AudioDevice> Devices
        {
            get
            {
                using var collection = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                return [.. collection.Select(device => new AudioDevice(device.ID, device.FriendlyName))];
            }
        }

        public AudioDevice? DefaultDevice =>
            _enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out var device)
                ? new AudioDevice(device.ID, device.FriendlyName)
                : null;

        public event EventHandler? DevicesChanged;
    }

    /// <summary>Ein WASAPI-Zugang im gemeinsamen Modus mit eigenem Lesefaden.</summary>
    private sealed class WasapiCaptureBackend : ICaptureBackend
    {
        private const int DeviceInvalidated = unchecked((int)0x88890004);
        private const int DeviceInUse = unchecked((int)0x8889000A);
        private const int AccessDenied = unchecked((int)0x80070005);
        private const long BufferDuration = 1_000_000; // 100 ms in 100-ns-Einheiten

        private readonly MMDevice _device;
        private readonly AudioClient _client;
        private readonly WaveFormat _mixFormat;
        private readonly AutoResetEvent _ready = new(false);
        private volatile bool _stopping;
        private Thread? _thread;

        public WasapiCaptureBackend(string deviceId)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _device = enumerator.GetDevice(deviceId);
                if (_device.State != DeviceState.Active)
                {
                    throw new CaptureBackendException(CaptureStopReason.DeviceMissing);
                }

                _client = _device.CreateAudioClient();
                _mixFormat = _client.MixFormat;
                Format = ToCaptureFormat(_mixFormat);
            }
            catch (COMException exception)
            {
                throw new CaptureBackendException(Reason(exception, CaptureStopReason.DeviceMissing));
            }
        }

        public CaptureFormat Format { get; }

        public event EventHandler<CaptureData>? DataAvailable;

        public event EventHandler<CaptureStopReason?>? Stopped;

        private EventHandler<bool>? _muteChanged;
        private bool _watchingMute;

        // Stummschalter aus dem Rückruf: 0 noch keiner, 1 frei, 2 stumm; zugestellt wird immer der neueste Stand
        private int _muteReported;
        private int _muteDelivered;
        private int _muteDelivering;
        private volatile bool _released;

        /// <summary>Stummschalter des Endpunkts; null, wenn Windows ihn nicht liefert.</summary>
        public bool? EndpointMuted
        {
            get
            {
                try
                {
                    return _device.AudioEndpointVolume.Mute;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Nur ein Hinweis: ein Fehler hier beendet nie die Aufnahme
                    return null;
                }
            }
        }

        public event EventHandler<bool>? MuteChanged
        {
            add
            {
                _muteChanged += value;
                if (!_watchingMute)
                {
                    try
                    {
                        _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                        _watchingMute = true;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        // Ohne Benachrichtigung bleibt der Stand vom Öffnen; die Aufnahme läuft trotzdem
                    }
                }
            }

            remove => _muteChanged -= value;
        }

        /// <summary>
        /// Rückruf von Windows (IAudioEndpointVolumeCallback): darf nie warten. Merkt nur den neuesten Stand und stellt ihn
        /// ausserhalb zu, immer nur eine Zustellung zugleich, damit die Reihenfolge stimmt; nach der Freigabe verworfen.
        /// </summary>
        private void OnVolumeNotification(AudioVolumeNotificationData data)
        {
            Interlocked.Exchange(ref _muteReported, data.Muted ? 2 : 1);
            if (Interlocked.Exchange(ref _muteDelivering, 1) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static backend => backend.DeliverMute(), this, preferLocal: false);
            }
        }

        private void DeliverMute()
        {
            while (true)
            {
                var reported = Volatile.Read(ref _muteReported);
                if (!_released && reported != _muteDelivered)
                {
                    _muteDelivered = reported;
                    try
                    {
                        _muteChanged?.Invoke(this, reported == 2);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        // Nur ein Hinweis: ein Fehler beim Abonnenten beendet nie die Aufnahme
                    }
                }

                // Freigeben, dann nachsehen: ein Wechsel während der Zustellung geht so nie verloren
                Interlocked.Exchange(ref _muteDelivering, 0);
                if (_released || Volatile.Read(ref _muteReported) == _muteDelivered
                    || Interlocked.Exchange(ref _muteDelivering, 1) != 0)
                {
                    return;
                }
            }
        }

        public void Start()
        {
            try
            {
                _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, BufferDuration, 0, _mixFormat, Guid.Empty);
                _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
                _client.Start();
            }
            catch (COMException exception)
            {
                throw new CaptureBackendException(Reason(exception, CaptureStopReason.Failure));
            }

            _thread = new Thread(Loop) { IsBackground = true, Name = "Freihand WASAPI" };
            _thread.Start();
        }

        public void Stop()
        {
            _stopping = true;
            _ready.Set();
        }

        public void Dispose()
        {
            Stop();
            if (_thread is not null && _thread != Thread.CurrentThread)
            {
                _thread.Join();
            }

            _released = true;
            if (_watchingMute)
            {
                // Rein verwaltet; die COM-Abmeldung macht _device.Dispose (NAudio, wirft nicht)
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
            }

            try
            {
                _client.Dispose();
            }
            finally
            {
                _device.Dispose();
                _ready.Dispose();
            }
        }

        private static CaptureStopReason Reason(COMException exception, CaptureStopReason otherwise) => exception.HResult switch
        {
            DeviceInvalidated => CaptureStopReason.DeviceRemoved,
            DeviceInUse => CaptureStopReason.DeviceInUse,
            AccessDenied => CaptureStopReason.AccessDenied,
            _ => otherwise,
        };

        private static CaptureFormat ToCaptureFormat(WaveFormat format)
        {
            var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                || (format is WaveFormatExtensible extensible && extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
            var sampleFormat = (isFloat, format.BitsPerSample) switch
            {
                (true, 32) => CaptureSampleFormat.Float32,
                (false, 16) => CaptureSampleFormat.Pcm16,
                (false, 24) => CaptureSampleFormat.Pcm24,
                (false, 32) => CaptureSampleFormat.Pcm32,
                _ => throw new CaptureBackendException(CaptureStopReason.Failure),
            };
            return new CaptureFormat(format.SampleRate, format.Channels, sampleFormat);
        }

        private void Loop()
        {
            CaptureStopReason? reason = null;
            long? expectedPosition = null;
            try
            {
                var capture = _client.AudioCaptureClient;
                while (!_stopping)
                {
                    _ready.WaitOne(200);
                    while (!_stopping && capture.GetNextPacketSize() > 0)
                    {
                        var pointer = capture.GetBuffer(out var frames, out var flags, out var position, out _);
                        var bytes = new byte[frames * _mixFormat.BlockAlign];
                        if (!flags.HasFlag(AudioClientBufferFlags.Silent))
                        {
                            Marshal.Copy(pointer, bytes, 0, bytes.Length);
                        }

                        capture.ReleaseBuffer(frames);

                        // Fehlende Frames aus dem Sprung der Geräteposition; ohne frühere Position unbekannt (Kapitel 10.5)
                        var discontinuity = flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity);
                        long? missing = discontinuity && expectedPosition is { } expected && position >= expected
                            ? position - expected
                            : null;
                        expectedPosition = position + frames;
                        DataAvailable?.Invoke(this, new CaptureData(bytes, discontinuity, missing));
                    }
                }

                _client.Stop();
            }
            catch (COMException exception)
            {
                reason = Reason(exception, CaptureStopReason.Failure);
            }

            Stopped?.Invoke(this, reason);
        }
    }
}
