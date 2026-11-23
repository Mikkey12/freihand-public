using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Freihand.Adapters.Recognition;

namespace Freihand.Core.Tests.Recognition.Kit;

/// <summary>Ein Rahmen nach Kapitel 4.7, wie ihn die Testseite gelesen hat; <see cref="Raw"/> sind seine Bytes.</summary>
internal sealed record Frame(JsonObject Header, byte[] Payload, byte[] Raw)
{
    public string? Type => String("type");

    public long? Id => Long("id");

    public string? String(string name) =>
        Header[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public long? Long(string name) =>
        Header[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    /// <summary>
    /// Der Fehlercode eines <c>failed</c>. Kapitel 4.7 legt nicht fest, ob <c>code</c> als Name oder als Zahl steht; die
    /// Testseite liest darum beides (Name ohne Gross- und Kleinschreibung).
    /// </summary>
    public RecognizerErrorCode? Code
    {
        get
        {
            if (Header["code"] is not JsonValue value)
            {
                return null;
            }

            if (value.TryGetValue<string>(out var name)
                && Enum.TryParse<RecognizerErrorCode>(name, ignoreCase: true, out var named)
                && Enum.IsDefined(named)
                && !int.TryParse(name, out _))
            {
                return named;
            }

            return value.TryGetValue<int>(out var number) && Enum.IsDefined((RecognizerErrorCode)number)
                ? (RecognizerErrorCode)number
                : null;
        }
    }

    /// <summary>Wörter einer <c>hypothesis</c> als (Text, Start, Ende, Wahrscheinlichkeit) in Ticks.</summary>
    public IReadOnlyList<(string Text, long Start, long End, double Probability)> Words =>
        [.. (Header["words"] as JsonArray ?? []).Select(word => (
            (string)word!["text"]!,
            (long)word["startTicks"]!,
            (long)word["endTicks"]!,
            (double)word["probability"]!))];

    /// <summary>Bereiche einer <c>regions</c> in Ticks.</summary>
    public IReadOnlyList<(long Start, long End)> Regions =>
        [.. (Header["regions"] as JsonArray ?? []).Select(region => ((long)region!["startTicks"]!, (long)region["endTicks"]!))];

    public override string ToString() => $"{Type}#{Id}";
}

/// <summary>
/// Rahmen-Codec der Testseite nach Kapitel 4.7, bewusst unabhängig vom Produkt: 4 Byte Kopflänge (vorzeichenlos,
/// Little Endian), UTF-8-JSON-Kopf, dann <c>payloadBytes</c> Byte Nutzlast; Audio als float32 Little Endian. Damit
/// sendet ein Test auch absichtlich falsche Rahmen.
/// </summary>
internal static class FrameCodec
{
    public const int MaxHeaderLength = 65_536;

    public const int MaxPayloadBytes = 16_777_216;

    /// <summary>Kodiert einen Rahmen; bei Nutzlast setzt es <c>payloadBytes</c>, sofern der Kopf es nicht schon nennt.</summary>
    public static byte[] Encode(JsonObject header, ReadOnlySpan<byte> payload = default)
    {
        if (!payload.IsEmpty && !header.ContainsKey("payloadBytes"))
        {
            header["payloadBytes"] = payload.Length;
        }

        return Raw(Encoding.UTF8.GetBytes(header.ToJsonString()), payload);
    }

    /// <summary>Ein Rahmen mit der echten Kopflänge und beliebigen Kopfbytes.</summary>
    public static byte[] Raw(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload = default) =>
        Raw((uint)header.Length, header, payload);

    /// <summary>Ein Rahmen mit frei gewählter Kopflänge, auch einer falschen.</summary>
    public static byte[] Raw(uint headerLength, ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload = default)
    {
        var bytes = new byte[4 + header.Length + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, headerLength);
        header.CopyTo(bytes.AsSpan(4));
        payload.CopyTo(bytes.AsSpan(4 + header.Length));
        return bytes;
    }

    /// <summary>Liest einen Rahmen; <c>null</c> beim Ende der Verbindung an einer Rahmengrenze.</summary>
    /// <exception cref="InvalidDataException">Abgeschnittener oder ungültiger Rahmen der Gegenseite.</exception>
    public static async Task<Frame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        var first = await stream.ReadAtLeastAsync(prefix, 4, throwOnEndOfStream: false, cancellationToken);
        if (first == 0)
        {
            return null;
        }

        if (first < 4)
        {
            throw new InvalidDataException("Rahmen der Gegenseite abgeschnitten (Kopflänge).");
        }

        var headerLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (headerLength is 0 or > MaxHeaderLength)
        {
            throw new InvalidDataException($"Kopflänge {headerLength} der Gegenseite ausserhalb der Grenzen.");
        }

        var header = new byte[headerLength];
        await ReadExactlyOrThrowAsync(stream, header, cancellationToken);
        var node = JsonNode.Parse(header) as JsonObject
            ?? throw new InvalidDataException("Kopf der Gegenseite ist kein JSON-Objekt.");
        var payloadBytes = node["payloadBytes"] is JsonValue value && value.TryGetValue<long>(out var length) ? length : 0;
        if (payloadBytes is < 0 or > MaxPayloadBytes)
        {
            throw new InvalidDataException($"Nutzlast {payloadBytes} der Gegenseite ausserhalb der Grenzen.");
        }

        var payload = new byte[payloadBytes];
        await ReadExactlyOrThrowAsync(stream, payload, cancellationToken);
        return new Frame(node, payload, [.. prefix, .. header, .. payload]);
    }

    /// <summary>Zerlegt aufgezeichnete Bytes in ganze Rahmen; ein abgeschnittener Rest am Ende bleibt weg.</summary>
    public static IReadOnlyList<Frame> Parse(ReadOnlySpan<byte> bytes) => [.. ParseWithEnds(bytes).Select(entry => entry.Frame)];

    /// <summary>Wie <see cref="Parse"/>, mit der Position des letzten Bytes jedes Rahmens.</summary>
    public static IReadOnlyList<(Frame Frame, int LastByte)> ParseWithEnds(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<(Frame, int)>();
        var position = 0;
        while (bytes.Length - position >= 4)
        {
            var headerLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]);
            if (headerLength is <= 0 or > MaxHeaderLength || bytes.Length - position - 4 < headerLength)
            {
                break;
            }

            var header = bytes.Slice(position + 4, headerLength);
            if (JsonNode.Parse(header) is not JsonObject node)
            {
                break;
            }

            var payloadBytes = node["payloadBytes"] is JsonValue value && value.TryGetValue<int>(out var length) ? length : 0;
            var total = 4 + headerLength + payloadBytes;
            if (payloadBytes < 0 || bytes.Length - position < total)
            {
                break;
            }

            var payload = bytes.Slice(position + 4 + headerLength, payloadBytes).ToArray();
            frames.Add((new Frame(node, payload, bytes.Slice(position, total).ToArray()), position + total - 1));
            position += total;
        }

        return frames;
    }

    /// <summary>Samples als float32 Little Endian, bitgenau.</summary>
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

    /// <summary>Der Text eines Rahmens als UTF-8 (für die Prüfung, dass nie Text oder Audio in Meldungen steht).</summary>
    public static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static async Task ReadExactlyOrThrowAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        if (await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken) < buffer.Length)
        {
            throw new InvalidDataException("Rahmen der Gegenseite abgeschnitten.");
        }
    }
}

/// <summary>Nachrichten der Testseite nach der Tabelle in Kapitel 4.7.</summary>
internal static class Messages
{
    public static JsonObject Hello(int protocol, string channel) =>
        new() { ["type"] = "hello", ["protocol"] = protocol, ["channel"] = channel };

    /// <summary><c>decode</c> mit Nutzlast aus den Samples; <c>prompt</c> leer, wie Kapitel 4.7 „oder leer“ zulässt.</summary>
    public static byte[] Decode(long id, long offsetTicks, float[] samples, string language = "de", string prompt = "") =>
        FrameCodec.Encode(
            new JsonObject
            {
                ["type"] = "decode",
                ["id"] = id,
                ["offsetTicks"] = offsetTicks,
                ["sampleCount"] = samples.Length,
                ["language"] = language,
                ["prompt"] = prompt,
                ["payloadBytes"] = samples.Length * 4,
            },
            FrameCodec.FloatBytes(samples));

    public static byte[] Detect(long id, long offsetTicks, float[] samples) =>
        FrameCodec.Encode(
            new JsonObject
            {
                ["type"] = "detect",
                ["id"] = id,
                ["offsetTicks"] = offsetTicks,
                ["sampleCount"] = samples.Length,
                ["payloadBytes"] = samples.Length * 4,
            },
            FrameCodec.FloatBytes(samples));

    /// <summary>Eine Anfrage mit frei gewählten Feldern (Inhaltsfehler).</summary>
    public static byte[] Request(string type, long id, long offsetTicks, long sampleCount, int payloadBytes)
    {
        var header = new JsonObject
        {
            ["type"] = type,
            ["id"] = id,
            ["offsetTicks"] = offsetTicks,
            ["sampleCount"] = sampleCount,
            ["payloadBytes"] = payloadBytes,
        };
        if (type == "decode")
        {
            header["language"] = "de";
            header["prompt"] = string.Empty;
        }

        return FrameCodec.Encode(header, new byte[payloadBytes]);
    }

    public static byte[] Cancel(long id) => FrameCodec.Encode(new JsonObject { ["type"] = "cancel", ["id"] = id });

    public static byte[] Bare(string type) => FrameCodec.Encode(new JsonObject { ["type"] = type });

    public static byte[] Json(string json) => FrameCodec.Raw(Encoding.UTF8.GetBytes(json));
}

/// <summary>Ein Ende einer Verbindung aus Sicht der Testseite: Rahmen senden und lesen, mit Schutz gegen Hänger.</summary>
internal sealed class FrameChannel(Stream stream)
{
    public Stream Stream { get; } = stream;

    public Task SendAsync(JsonObject header, byte[]? payload = null) => SendRawAsync(FrameCodec.Encode(header, payload));

    public async Task SendRawAsync(byte[] bytes) => await Stream.WriteAsync(bytes);

    /// <summary>Nächster Rahmen oder <c>null</c> beim Ende der Verbindung.</summary>
    public Task<Frame?> ReadAsync() => FrameCodec.ReadAsync(Stream, CancellationToken.None).WaitAsync(RecognitionTestKit.Guard);

    public async Task<Frame> ExpectAsync(string type)
    {
        var frame = await ReadAsync();
        Assert.True(frame is not null, $"Verbindung beendet, erwartet war „{type}“.");
        Assert.Equal(type, frame.Type);
        return frame;
    }

    /// <summary>Liest bis zum Ende der Verbindung; schlägt fehl, wenn sie nicht endet (Schutz gegen Hänger).</summary>
    public async Task<IReadOnlyList<Frame>> ReadToEndAsync()
    {
        var frames = new List<Frame>();
        while (await ReadAsync() is { } frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>Liest bis zum Ende; Lesefehler wegen harter Trennung gelten als Ende.</summary>
    public async Task<IReadOnlyList<Frame>> ReadToEndOrBreakAsync()
    {
        var frames = new List<Frame>();
        try
        {
            while (await ReadAsync() is { } frame)
            {
                frames.Add(frame);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Hart getrennt: das zählt als Ende der Verbindung.
        }

        return frames;
    }
}
