using System.Diagnostics;
using System.Text;

namespace Freihand.Windows.Tests.Desktop;

/// <summary>
/// Steuert die eigenen Testfenster in einem getrennten Prozess (Freihand.Windows.TestTarget), damit Freihand sie nicht als
/// eigenes Programm abweist (P7-Konzept, Kapitel 7.3).
/// </summary>
internal sealed class TestTargetProcess : IDisposable
{
    public const string ProcessName = "Freihand.Windows.TestTarget";

    private readonly Process _process;

    public TestTargetProcess()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, ProcessName + ".exe");
        Assert.True(File.Exists(exe), $"Testziel fehlt: {exe}");
        _process = Process.Start(new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        })!;
        Assert.Equal("bereit", ReadLine());
    }

    public int ProcessId => _process.Id;

    /// <summary>Legt ein Testfenster an: <c>multi</c>, <c>single</c> oder <c>password</c>; liefert das Handle des Felds.</summary>
    public nint Window(string id, string kind) => nint.Parse(Send($"window {id} {kind}"));

    public nint Frame(string id) => nint.Parse(Send($"frame {id}"));

    public void Title(string id, string title) => Send($"title {id} {Encode(title)}");

    public void SetText(string id, string text) => Send($"settext {id} {Encode(text)}");

    public string Text(string id) => Encoding.UTF8.GetString(Convert.FromBase64String(Send($"gettext {id}"))).Replace("\r\n", "\n");

    /// <summary>Holt das Testfenster in den Vordergrund; wahr nur, wenn es danach dort ist.</summary>
    public bool Focus(string id) => Send($"focus {id}") == "1";

    /// <summary>Zahl der <c>WM_KEYDOWN</c> mit Y, die das Feld erhalten hat (W22).</summary>
    public int YDowns(string id) => int.Parse(Send($"keys {id}"));

    /// <summary>Lädt und aktiviert ein Tastaturlayout im Testprozess; liefert das HKL.</summary>
    public nint Layout(string id) => nint.Parse(Send($"layout {id}"), System.Globalization.NumberStyles.HexNumber);

    /// <summary>Wartet, bis das Feld <paramref name="length"/> Zeichen hat, höchstens zwei Sekunden.</summary>
    public void WaitForLength(string id, int length)
    {
        var watch = Stopwatch.StartNew();
        while (Text(id).Length < length && watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            Thread.Sleep(20);
        }
    }

    public void Dispose()
    {
        try
        {
            _process.StandardInput.WriteLine("quit");
            if (!_process.WaitForExit(2000))
            {
                _process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Prozess schon beendet
        }

        _process.Dispose();
    }

    private string Send(string command)
    {
        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
        var answer = ReadLine();
        Assert.False(answer.StartsWith("fehler", StringComparison.Ordinal), $"Testziel: {command.Split(' ')[0]} → {answer}");
        return answer;
    }

    private string ReadLine() => _process.StandardOutput.ReadLine() ?? throw new InvalidOperationException("Testziel beendet.");

    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
}
