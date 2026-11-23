using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Freihand.Adapters.Recognition;
using Microsoft.Win32.SafeHandles;

namespace Freihand.Windows.Recognition;

/// <summary>
/// Named Pipe für den Erkenner (P8-Konzept, Kapitel 5.4 und 10.5): Name <c>freihand-</c> plus GUID, höchstens zwei
/// Instanzen, die erste mit <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c>, Fernzugriff abgewiesen
/// (<c>PIPE_REJECT_REMOTE_CLIENTS</c>), Besitzer und einziger Zugriffseintrag der angemeldete Benutzer. .NET setzt das
/// Abweisen nicht selbst und lässt eigene Rechte nicht mit <see cref="PipeOptions.CurrentUserOnly"/> zu; darum entsteht die
/// Pipe über <c>CreateNamedPipeW</c>.
/// </summary>
public static class RecognizerPipe
{
    public const int MaxInstances = 2;

    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint BufferSize = 65_536;

    public static string NewName() => "freihand-" + Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);

    /// <param name="name">Name ohne <c>\\.\pipe\</c>.</param>
    /// <param name="first">Erste Instanz: scheitert, wenn der Name schon besteht.</param>
    public static NamedPipeServerStream CreateServer(string name, bool first)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Kein angemeldeter Benutzer.");
        var sid = user.Value;
        var descriptor = new RawSecurityDescriptor($"O:{sid}G:{sid}D:P(A;;GA;;;{sid})");
        var binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);

        var pinned = GCHandle.Alloc(binary, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pinned.AddrOfPinnedObject(),
                InheritHandle = 0,
            };
            var openMode = PipeAccessDuplex | FileFlagOverlapped | (first ? FileFlagFirstPipeInstance : 0);
            var handle = CreateNamedPipeW(
                @"\\.\pipe\" + name, openMode, PipeTypeByte | PipeRejectRemoteClients, MaxInstances, BufferSize, BufferSize, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new IOException("Pipe für den Erkenner nicht erstellt.", new Win32Exception(error));
            }

            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>Prozesskennung des verbundenen Clients (<c>GetNamedPipeClientProcessId</c>).</summary>
    public static int GetClientProcessId(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
        {
            throw new IOException("Prozesskennung des Clients unbekannt.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return checked((int)processId);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafePipeHandle CreateNamedPipeW(
        string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout,
        ref SecurityAttributes securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }
}

/// <summary>Pipe-Server und Prozessstart des Erkenners; je <see cref="LaunchAsync"/> eine neue Pipe.</summary>
/// <param name="startInfo">Startangaben aus dem Namen der Pipe.</param>
public sealed class RecognizerLauncher(Func<string, ProcessStartInfo> startInfo) : IRecognizerLauncher
{
    /// <summary>Startet <c>Freihand.Recognizer.exe --pipe &lt;Name&gt; --parent &lt;PID&gt;</c>.</summary>
    public static RecognizerLauncher ForExecutable(string recognizerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recognizerPath);
        return new RecognizerLauncher(name =>
        {
            var info = new ProcessStartInfo(recognizerPath) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--pipe");
            info.ArgumentList.Add(name);
            info.ArgumentList.Add("--parent");
            info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            return info;
        });
    }

    public Task<LaunchedRecognizer> LaunchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = RecognizerPipe.NewName();

        // Beide Instanzen bestehen, bevor der Erkenner startet; die erste sichert den Namen (FirstPipeInstance)
        var servers = new Queue<NamedPipeServerStream>();
        servers.Enqueue(RecognizerPipe.CreateServer(name, first: true));
        servers.Enqueue(RecognizerPipe.CreateServer(name, first: false));

        Process process;
        try
        {
            process = Process.Start(startInfo(name)) ?? throw new InvalidOperationException("Erkenner nicht gestartet.");
        }
        catch
        {
            while (servers.TryDequeue(out var server))
            {
                server.Dispose();
            }

            throw;
        }

        var gate = new object();
        async Task<PipeConnection> AcceptAsync(CancellationToken token)
        {
            NamedPipeServerStream server;
            lock (gate)
            {
                // Nach einer getrennten fremden Verbindung ist wieder Platz für eine Instanz
                server = servers.Count > 0 ? servers.Dequeue() : RecognizerPipe.CreateServer(name, first: false);
            }

            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                return new PipeConnection(server, RecognizerPipe.GetClientProcessId(server));
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        void Kill()
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // schon beendet
            }

            lock (gate)
            {
                while (servers.TryDequeue(out var server))
                {
                    server.Dispose();
                }
            }
        }

        var exited = process.WaitForExitAsync(CancellationToken.None);
        return Task.FromResult(new LaunchedRecognizer(process.Id, exited, AcceptAsync, Kill));
    }
}
