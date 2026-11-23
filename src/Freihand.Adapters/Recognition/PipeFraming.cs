using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Freihand.Core;

namespace Freihand.Adapters.Recognition;

/// <summary>Rahmenfehler nach Kapitel 4.7, Regel 1: die Verbindung endet ohne Antwort.</summary>
internal sealed class FrameException : Exception
{
    public FrameException()
        : base("Rahmenfehler.")
    {
    }
}

/// <summary>Eine Nachricht der Pipe: Kopf als JSON-Objekt mit <c>type</c>, dazu die Nutzlast.</summary>
internal sealed record PipeFrame(JsonObject Header, byte[] Payload)
{
    public string Type => (string)Header["type"]!;

    /// <summary>Ganzzahliges Feld oder null, wenn es fehlt oder keine ganze Zahl ist.</summary>
    public long? Long(string name) =>
        Header[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    public string? String(string name) =>
        Header[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>Rahmen des Protokolls Version 1 (Kapitel 4.7): 4 Byte Kopflänge LE, UTF-8-JSON-Kopf, Nutzlast.</summary>
internal static class PipeFraming
{
    public const int Protocol = 1;
    public const int MaxHeaderBytes = 65_536;
    public const long MaxPayloadBytes = 16_777_216;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>JSON wie in Kapitel 10.4: Web-Standard, Aufzählungen als Namen.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Liest eine Nachricht; null bei sauberem Ende vor einem Rahmen.</summary>
    /// <exception cref="FrameException">Rahmenfehler oder Ende mitten im Rahmen.</exception>
    public static async Task<PipeFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        var first = await ReadSomeAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (first == 0)
        {
            return null;
        }

        await ReadExactlyAsync(stream, prefix.AsMemory(first), cancellationToken).ConfigureAwait(false);
        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (headerLength is < 1 or > MaxHeaderBytes)
        {
            throw new FrameException();
        }

        var headerBytes = new byte[headerLength];
        await ReadExactlyAsync(stream, headerBytes, cancellationToken).ConfigureAwait(false);
        var header = ParseHeader(headerBytes);
        if (header["type"] is not JsonValue type || !type.TryGetValue<string>(out _))
        {
            throw new FrameException();
        }

        long payloadBytes = 0;
        if (header["payloadBytes"] is { } node)
        {
            if (node is not JsonValue value || !value.TryGetValue(out payloadBytes))
            {
                throw new FrameException();
            }
        }

        if (payloadBytes is < 0 or > MaxPayloadBytes)
        {
            throw new FrameException();
        }

        var payload = new byte[payloadBytes];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return new PipeFrame(header, payload);
    }

    /// <summary>
    /// Der einzige Leser von JSON auf der Pipe (JsonInputUnicodeMatrixTests): strenges UTF-8 und kein einzelnes
    /// Ersatzzeichen, roh oder als Escape, an keiner Stelle des Kopfs (gemeinsame Prüfung <see cref="JsonUnicode"/>); jeder
    /// Verstoss ist ein Rahmenfehler nach Regel 1.
    /// </summary>
    public static JsonObject ParseHeader(byte[] bytes)
    {
        try
        {
            var text = StrictUtf8.GetString(bytes);
            if (JsonUnicode.HasLoneSurrogate(text))
            {
                throw new FrameException();
            }

            return JsonNode.Parse(text) as JsonObject ?? throw new FrameException();
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or ArgumentException
            or InvalidOperationException)
        {
            throw new FrameException();
        }
    }

    /// <summary>Wandelt einen Teil eines schon geprüften Kopfs; null, wenn er nicht passt.</summary>
    public static T? Read<T>(JsonNode? node)
        where T : class
    {
        try
        {
            return node?.Deserialize<T>(Json);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException
            or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Schreibt eine Nachricht in einem Stück; setzt <c>payloadBytes</c>, wenn es Nutzlast gibt.</summary>
    public static async Task WriteAsync(
        Stream stream, SemaphoreSlim gate, JsonObject header, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var bytes = Encode(header, payload.Span);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public static byte[] Encode(JsonObject header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 0)
        {
            header["payloadBytes"] = payload.Length;
        }

        var json = Encoding.UTF8.GetBytes(header.ToJsonString());
        var bytes = new byte[4 + json.Length + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)json.Length);
        json.CopyTo(bytes, 4);
        payload.CopyTo(bytes.AsSpan(4 + json.Length));
        return bytes;
    }

    public static JsonObject Failed(long? id, RecognizerErrorCode code)
    {
        var header = new JsonObject { ["type"] = "failed" };
        if (id is { } value)
        {
            header["id"] = value;
        }

        header["code"] = code.ToString();
        return header;
    }

    /// <summary>Float32 Little Endian, bitgenau.</summary>
    public static byte[] FloatBytes(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), samples[i]);
        }

        return bytes;
    }

    public static float[] Floats(ReadOnlySpan<byte> bytes)
    {
        var samples = new float[bytes.Length / 4];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 4)..]);
        }

        return samples;
    }

    public static RecognizerErrorCode? Code(PipeFrame frame) =>
        Enum.TryParse<RecognizerErrorCode>(frame.String("code"), out var code) && Enum.IsDefined(code) ? code : null;

    private static async Task<int> ReadSomeAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new FrameException();
        }
        catch (IOException)
        {
            throw new FrameException();
        }
    }
}
