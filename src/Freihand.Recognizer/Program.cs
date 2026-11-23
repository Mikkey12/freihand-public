using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using Freihand.Adapters.Recognition;

namespace Freihand.Recognizer;

/// <summary>
/// <c>Freihand.Recognizer.exe --pipe &lt;Name&gt; --parent &lt;PID&gt;</c>: verbindet sich zuerst auf <c>decode</c>, nach
/// <c>ready</c> auf <c>detect</c> und endet, sobald der Elternprozess endet (P8-Konzept, Kapitel 5.4 und 10.4). Kein
/// Netzwerk (WE-07); Ausgaben enthalten nie Text oder Audio.
/// </summary>
public static class Program
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    public static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out var pipe, out var parentId))
        {
            return 2;
        }

        // Den Elternprozess ab dem Start überwachen, auch während auf configure gewartet wird (Kapitel 10.4)
        Process parent;
        try
        {
            parent = Process.GetProcessById(parentId);
        }
        catch (ArgumentException)
        {
            return 3;
        }

        using var stop = new CancellationTokenSource();
        _ = WatchParentAsync(parent);

        await using var decode = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await decode.ConnectAsync(ConnectTimeout, stop.Token).ConfigureAwait(false);

        var service = new RecognizerService(WhisperNetLoader.Load);
        var decoding = service.RunDecodeChannelAsync(decode, stop.Token);
        if (await Task.WhenAny(service.Ready, decoding).ConfigureAwait(false) == service.Ready)
        {
            var detect = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await detect.ConnectAsync(ConnectTimeout, stop.Token).ConfigureAwait(false);
            _ = service.RunDetectChannelAsync(detect, stop.Token);
        }

        // Endet mit shutdown, getrennter Verbindung oder einem Fehler beim Laden
        await decoding.ConfigureAwait(false);
        return 0;
    }

    private static async Task WatchParentAsync(Process parent)
    {
        await parent.WaitForExitAsync().ConfigureAwait(false);
        Environment.Exit(0);
    }

    private static bool TryParse(string[] args, out string pipe, out int parentId)
    {
        pipe = string.Empty;
        parentId = 0;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--pipe":
                    pipe = args[i + 1];
                    break;
                case "--parent":
                    if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out parentId))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        return pipe.Length > 0 && parentId > 0;
    }
}
