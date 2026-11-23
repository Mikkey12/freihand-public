using NAudio.Wave;

namespace Freihand.Benchmark;

/// <summary>Nimmt Referenzaufnahmen auf (QM-01): 16 kHz, 16 Bit, mono, bis Enter gedrückt wird.</summary>
internal static class Recorder
{
    public static void ListDevices()
    {
        if (WaveIn.DeviceCount == 0)
        {
            Console.WriteLine("Kein Mikrofon gefunden.");
            return;
        }

        for (var i = 0; i < WaveIn.DeviceCount; i++)
        {
            Console.WriteLine($"{i}: {WaveIn.GetCapabilities(i).ProductName}");
        }
    }

    public static void Record(string path, int device)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var format = new WaveFormat(WavFile.SampleRate, 16, 1);
        using var writer = new WaveFileWriter(path, format);
        using var waveIn = new WaveIn { DeviceNumber = device, WaveFormat = format, BufferMilliseconds = 50 };
        using var stopped = new ManualResetEventSlim();
        Exception? failure = null;

        waveIn.DataAvailable += (_, e) => writer.Write(e.BufferSpan[..e.BytesRecorded]);
        waveIn.RecordingStopped += (_, e) =>
        {
            failure = e.Exception;
            stopped.Set();
        };

        waveIn.StartRecording();
        Console.WriteLine($"Aufnahme läuft ({WaveIn.GetCapabilities(device).ProductName}). Enter beendet sie.");
        Console.ReadLine();
        waveIn.StopRecording();
        stopped.Wait(TimeSpan.FromSeconds(5));

        if (failure is not null)
        {
            throw new IOException("Die Aufnahme wurde mit einem Fehler beendet.", failure);
        }

        writer.Flush();
        Console.WriteLine($"Gespeichert: {Path.GetFullPath(path)} ({writer.TotalTime:mm\\:ss})");
    }
}
