namespace Freihand.Core.Tests.Recognition.Kit;

/// <summary>Seite einer <see cref="DuplexPipe"/>: die App (Supervisor, Client) oder der Erkenner (Dienst).</summary>
internal enum PipeSide
{
    App,
    Recognizer,
}

/// <summary>Ein Schreibaufruf einer Seite, mit fortlaufender Nummer über beide Richtungen der Verbindung.</summary>
internal sealed record WireWrite(long Sequence, PipeSide Side, byte[] Bytes);

/// <summary>
/// Zwei verbundene Speicherströme wie die beiden Enden einer Named Pipe (P8-Konzept, Kapitel 6), ohne echte Zeit.
/// <para>Bewusst eigene Umsetzung statt <c>System.IO.Pipelines</c> (im geteilten Framework vorhanden): Eine
/// <c>Pipe</c> erlaubt kein <c>Complete</c> von einem dritten Faden, während Leser oder Schreiber arbeiten; das harte
/// Trennen durch den Test (<see cref="Sever"/>) braucht genau das. Hier schützt eine Sperre den ganzen Zustand.</para>
/// <para>Verhalten wie bei einer Pipe: Schliesst eine Seite (<c>Dispose</c>), liest die andere zuerst den Rest und dann
/// das Ende (0 Byte); ihr Schreiben wirft <see cref="IOException"/>. <see cref="Sever"/> trennt hart: Ungelesenes geht
/// verloren, beide Seiten lesen sofort das Ende, jedes Schreiben wirft. Jede Ausnahme trägt die <see cref="Marker"/>,
/// damit <c>UnobservedFailureLog</c> sie erkennt. Alle geschriebenen Bytes stehen im <see cref="Log"/>.</para>
/// </summary>
internal sealed class DuplexPipe
{
    private readonly object _gate = new();
    private readonly List<WireWrite> _log = [];
    private readonly Direction _toRecognizer = new();
    private readonly Direction _toApp = new();
    private long _sequence;
    private bool _severed;

    public DuplexPipe(string marker)
    {
        Marker = marker;
        App = new DuplexStream(this, PipeSide.App);
        Recognizer = new DuplexStream(this, PipeSide.Recognizer);
    }

    /// <summary>Kennung in jeder Ausnahme dieser Verbindung.</summary>
    public string Marker { get; }

    /// <summary>Ende der App.</summary>
    public DuplexStream App { get; }

    /// <summary>Ende des Erkenners.</summary>
    public DuplexStream Recognizer { get; }

    public bool Severed
    {
        get
        {
            lock (_gate)
            {
                return _severed;
            }
        }
    }

    /// <summary>Alle Schreibaufrufe beider Seiten in ihrer Reihenfolge.</summary>
    public IReadOnlyList<WireWrite> Log
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    /// <summary>Alle Bytes, die eine Seite geschrieben hat, aneinandergehängt.</summary>
    public byte[] WrittenBy(PipeSide side) => [.. Log.Where(write => write.Side == side).SelectMany(write => write.Bytes)];

    /// <summary>Die Rahmen einer Seite, je mit der Nummer des Schreibaufrufs, der ihr letztes Byte enthielt.</summary>
    public IReadOnlyList<(Frame Frame, long Sequence)> FramesBy(PipeSide side)
    {
        var writes = Log.Where(write => write.Side == side).ToList();
        var bytes = writes.SelectMany(write => write.Bytes).ToArray();
        var sequenceOfByte = writes.SelectMany(write => Enumerable.Repeat(write.Sequence, write.Bytes.Length)).ToArray();
        return [.. FrameCodec.ParseWithEnds(bytes).Select(entry => (entry.Frame, sequenceOfByte[entry.LastByte]))];
    }

    /// <summary>Die Rahmen, die eine Seite geschrieben hat.</summary>
    public IReadOnlyList<Frame> SentBy(PipeSide side) => FrameCodec.Parse(WrittenBy(side));

    /// <summary>Trennt die Verbindung hart, wie ein abgestürzter Prozess oder eine gebrochene Pipe.</summary>
    public void Sever()
    {
        lock (_gate)
        {
            _severed = true;
            _toApp.Chunks.Clear();
            _toRecognizer.Chunks.Clear();
        }

        Signal();
    }

    private Direction Outgoing(PipeSide side) => side == PipeSide.App ? _toRecognizer : _toApp;

    private Direction Incoming(PipeSide side) => side == PipeSide.App ? _toApp : _toRecognizer;

    internal void Write(DuplexStream stream, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(stream.IsClosed, stream);
            var outgoing = Outgoing(stream.Side);
            if (_severed || outgoing.ReaderClosed)
            {
                throw new IOException($"Verbindung getrennt ({Marker}).");
            }

            if (bytes.IsEmpty)
            {
                return;
            }

            var copy = bytes.ToArray();
            outgoing.Chunks.Enqueue(copy);
            _log.Add(new WireWrite(++_sequence, stream.Side, copy));
            stream.BytesWritten += copy.Length;
        }

        Signal();
    }

    internal async ValueTask<int> ReadAsync(DuplexStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            stream.ReadCalls++;
        }

        while (true)
        {
            Task changed;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(stream.IsClosed, stream);
                var incoming = Incoming(stream.Side);
                if (_severed)
                {
                    return 0;
                }

                if (buffer.IsEmpty)
                {
                    return 0;
                }

                if (incoming.Chunks.Count > 0)
                {
                    var read = 0;
                    while (read < buffer.Length && incoming.Chunks.Count > 0)
                    {
                        var head = incoming.Chunks.Peek();
                        var take = Math.Min(buffer.Length - read, head.Length - incoming.HeadOffset);
                        head.AsSpan(incoming.HeadOffset, take).CopyTo(buffer.Span[read..]);
                        read += take;
                        incoming.HeadOffset += take;
                        if (incoming.HeadOffset == head.Length)
                        {
                            incoming.Chunks.Dequeue();
                            incoming.HeadOffset = 0;
                        }
                    }

                    stream.BytesRead += read;
                    return read;
                }

                if (incoming.WriterClosed)
                {
                    return 0;
                }

                changed = incoming.Changed.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal void Close(DuplexStream stream)
    {
        lock (_gate)
        {
            if (stream.IsClosed)
            {
                return;
            }

            stream.IsClosed = true;
            Outgoing(stream.Side).WriterClosed = true;
            Incoming(stream.Side).ReaderClosed = true;
        }

        Signal();
    }

    private void Signal()
    {
        TaskCompletionSource toApp;
        TaskCompletionSource toRecognizer;
        lock (_gate)
        {
            toApp = _toApp.Changed;
            toRecognizer = _toRecognizer.Changed;
            _toApp.Changed = NewSignal();
            _toRecognizer.Changed = NewSignal();
        }

        toApp.TrySetResult();
        toRecognizer.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Eine Richtung der Verbindung; nur unter der Sperre lesen und schreiben.</summary>
    private sealed class Direction
    {
        public Queue<byte[]> Chunks { get; } = new();

        public int HeadOffset { get; set; }

        /// <summary>Der Schreiber hat geschlossen: nach dem Rest liest der Leser das Ende.</summary>
        public bool WriterClosed { get; set; }

        /// <summary>Der Leser hat geschlossen: Schreiben wirft.</summary>
        public bool ReaderClosed { get; set; }

        public TaskCompletionSource Changed { get; set; } = NewSignal();
    }
}

/// <summary>Ein Ende der Verbindung.</summary>
internal sealed class DuplexStream : Stream
{
    private readonly DuplexPipe _pipe;

    internal DuplexStream(DuplexPipe pipe, PipeSide side)
    {
        _pipe = pipe;
        Side = side;
    }

    public PipeSide Side { get; }

    /// <summary>Diese Seite hat geschlossen (<c>Dispose</c>); nur unter der Sperre der Verbindung schreiben.</summary>
    public bool IsClosed { get; internal set; }

    /// <summary>Zahl der Leseaufrufe dieser Seite, auch solcher ohne Ergebnis (R30: vor dem ersten Lesen getrennt).</summary>
    public int ReadCalls { get; internal set; }

    public long BytesRead { get; internal set; }

    public long BytesWritten { get; internal set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) =>
        _pipe.ReadAsync(this, buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        var rented = new byte[buffer.Length];
        var read = _pipe.ReadAsync(this, rented, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        rented.AsSpan(0, read).CopyTo(buffer);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _pipe.ReadAsync(this, buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _pipe.ReadAsync(this, buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => _pipe.Write(this, buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer) => _pipe.Write(this, buffer);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pipe.Write(this, buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pipe.Write(this, buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pipe.Close(this);
        }

        base.Dispose(disposing);
    }
}
