using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Freihand.Core.Recovery;

/// <summary>
/// Speicherformat eines Entwurfs (ER-06). Aufbau der Datei:
/// <c>"FRHD"</c> (4 Byte) | Formatversion (1 Byte) | Länge der Nutzlast (4 Byte, little-endian) |
/// verschlüsselte Nutzlast | SHA-256 der verschlüsselten Nutzlast (32 Byte).
/// Die Nutzlast ist UTF-8-JSON mit Kennung, Zeitpunkten und den festen Abschnitten, nichts sonst.
/// Länge und Prüfsumme erkennen abgebrochene und veränderte Dateien unabhängig von der Verschlüsselung.
/// </summary>
internal static class DraftFile
{
    public const string Extension = ".draft";

    public const string TempExtension = ".draft.tmp";

    public const byte FormatVersion = 1;

    private const int HeaderLength = 9;

    private const int HashLength = 32;

    private static ReadOnlySpan<byte> Magic => "FRHD"u8;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public static string NameOf(Guid id) => id.ToString("N") + Extension;

    public static string TempNameOf(Guid id) => id.ToString("N") + TempExtension;

    /// <summary>Kennung aus einem Dateinamen der Form <c>&lt;guid&gt;.draft</c> oder <c>&lt;guid&gt;.draft.tmp</c>.</summary>
    public static Guid? IdOf(string fileName)
    {
        var stem = fileName.EndsWith(TempExtension, StringComparison.Ordinal)
            ? fileName[..^TempExtension.Length]
            : fileName.EndsWith(Extension, StringComparison.Ordinal) ? fileName[..^Extension.Length] : null;

        return stem is not null && Guid.TryParseExact(stem, "N", out var id) ? id : null;
    }

    public static byte[] Encode(DraftPayload payload, IDataProtector protector)
    {
        var protectedPayload = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));

        var file = new byte[HeaderLength + protectedPayload.Length + HashLength];
        Magic.CopyTo(file);
        file[4] = FormatVersion;
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(5, 4), protectedPayload.Length);
        protectedPayload.CopyTo(file.AsSpan(HeaderLength));
        SHA256.HashData(protectedPayload, file.AsSpan(HeaderLength + protectedPayload.Length));
        return file;
    }

    /// <exception cref="InvalidDataException">Die Datei ist unvollständig, verändert oder nicht lesbar.</exception>
    public static DraftPayload Decode(byte[] file, Guid expectedId, IDataProtector protector)
    {
        if (file.Length < HeaderLength + HashLength || !file.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Keine Sicherungsdatei oder abgeschnitten.");
        }

        if (file[4] != FormatVersion)
        {
            throw new InvalidDataException($"Unbekannte Formatversion {file[4]}.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(5, 4));
        if (length < 0 || length != file.Length - HeaderLength - HashLength)
        {
            throw new InvalidDataException("Die Länge stimmt nicht: Datei abgeschnitten oder verlängert.");
        }

        var protectedPayload = file.AsSpan(HeaderLength, length);
        if (!SHA256.HashData(protectedPayload).AsSpan().SequenceEqual(file.AsSpan(HeaderLength + length)))
        {
            throw new InvalidDataException("Die Prüfsumme stimmt nicht: Datei verändert.");
        }

        DraftPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<DraftPayload>(protector.Unprotect(protectedPayload.ToArray()), JsonOptions);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            throw new InvalidDataException("Die Nutzlast lässt sich nicht entschlüsseln oder lesen.", exception);
        }

        if (payload is null || payload.Id != expectedId)
        {
            throw new InvalidDataException("Die Kennung passt nicht zum Dateinamen.");
        }

        return payload;
    }
}

/// <summary>Inhalt der verschlüsselten Nutzlast. Kein Audio, kein vorläufiger Text (Grundentscheid 2).</summary>
internal sealed record DraftPayload(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? HandedOverAt,
    IReadOnlyList<RecoveredSegment> Segments);
