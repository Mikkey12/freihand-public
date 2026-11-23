using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Freihand.Core.Output;
using Freihand.Core.Recognition;
using Freihand.Core.Recovery;
using Freihand.Core.Segments;

namespace Freihand.Adapters.Dictation;

/// <summary>Beginnt je Diktat eine Prüfaufnahme.</summary>
public interface IReviewRecorder
{
    /// <summary>Null, wenn Prüfaufnahmen aus oder abgelaufen sind.</summary>
    IReviewRecording? Begin(int number);
}

/// <summary>Eine Prüfaufnahme: Ton ab dem ersten Block und je fester Abschnitt, was erkannt und was geschrieben wurde.</summary>
public interface IReviewRecording
{
    void Audio(AudioChunk chunk);

    void Section(TextSegment segment, OutputReport report);

    /// <summary>Schreibt die Aufnahme verschlüsselt; liefert den Dateinamen oder null, wenn nichts geschrieben wurde.</summary>
    string? Complete();
}

/// <summary>
/// Prüfaufnahmen nur in der Privatausgabe und nur auf Wunsch (Entscheid P11-E1): je Diktat Ton und Text, verschlüsselt mit DPAPI
/// im Benutzerkontext wie die Wiederherstellung, im eigenen Ordner. Sie klären je Fehler, ob er in der Erkennung oder beim
/// Schreiben entstand (Kapitel 1). Eingeschaltet gelten sie <see cref="Duration"/> lang; beim Ablauf (Zeitgeber), beim
/// Ausschalten und beim Start nach Ablauf löscht Freihand den ganzen Ordner. Der Zustand steht im Ordner selbst (<see cref="StateFile"/>), nicht
/// in den Einstellungen. Ein Schreib- oder Löschfehler wirft nie ins Diktat.
/// <para>
/// Format, Fassung 1: DPAPI-geschützt; darin 4 Byte Länge (Little Endian) des JSON-Kopfs <see cref="ReviewHeader"/>, der
/// Kopf in UTF-8, danach der Ton als PCM 16 Bit mono. Freihand liest die Dateien nie; nur die Auswertung (S2) liest sie.
/// </para>
/// </summary>
public sealed class ReviewRecorder : IReviewRecorder, IDisposable
{
    /// <summary>Gültigkeit nach dem Einschalten (Entscheid P11-E1).</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromDays(7);

    /// <summary>Höchstens so viel Ton je Diktat (rund 10 Minuten bei 16 kHz); der Rest wird nur noch gezählt.</summary>
    public const int MaxAudioBytes = 20_000_000;

    /// <summary>Endung der verschlüsselten Aufnahmen.</summary>
    public const string Extension = ".fpa";

    /// <summary>Freigabe: Ablauf (ISO 8601, UTC) und Kennung, je eine Zeile; fehlt sie, sind Prüfaufnahmen aus.</summary>
    public const string StateFile = "aktiv-bis.txt";

    /// <summary>
    /// Widerruf: steht diese Datei, sind Prüfaufnahmen aus, gleich was in <see cref="StateFile"/> steht. Nur ein gelungenes
    /// Einschalten entfernt sie; gelöscht wird sie sonst erst zusammen mit der Freigabe.
    /// </summary>
    public const string RevokedFile = "widerrufen.txt";

    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    /// <summary>Ausgeschaltet in diesem Prozess, bis ein Einschalten gelingt; gilt auch, wenn auf der Platte nichts geht.</summary>
    private bool _disabled;
    private ITimer? _expiry;

    public ReviewRecorder(string folder, IDataProtector protector, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(time);
        Folder = Path.GetFullPath(folder);
        _protector = protector;
        _time = time;
        lock (_gate)
        {
            if (ReadConsent() is { } consent)
            {
                ScheduleExpiry(consent);
            }
        }
    }

    public string Folder { get; }

    /// <summary>Ablauf der eingeschalteten Prüfaufnahmen; null, wenn aus, widerrufen oder abgelaufen (dann wird gelöscht).</summary>
    public DateTimeOffset? ActiveUntil
    {
        get
        {
            lock (_gate)
            {
                return Valid()?.Until;
            }
        }
    }

    /// <summary>Schaltet für <see cref="Duration"/> mit neuer Kennung ein; ein Fehler beim Schreiben lässt sie aus.</summary>
    public bool Enable()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var consent = new Consent(Guid.NewGuid().ToString("N"), _time.GetUtcNow() + Duration);
                File.WriteAllLines(Path.Combine(Folder, StateFile),
                    [consent.Until.ToString("O", CultureInfo.InvariantCulture), consent.Id]);

                // Erst eine neue Freigabe hebt den Widerruf auf; lässt er sich nicht entfernen, bleibt es aus
                if (!TryDelete(Path.Combine(Folder, RevokedFile)))
                {
                    return false;
                }

                _disabled = false;
                ScheduleExpiry(consent);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Schaltet aus: Jede bisherige Freigabe ist sofort widerrufen, im Prozess und über <see cref="RevokedFile"/>,
    /// unabhängig davon, ob sich die Freigabedatei lesen oder löschen lässt (Codex-Deltaprüfung PR #60). Liefert, ob alle
    /// Aufnahmen gelöscht sind und der Widerruf feststeht.
    /// </summary>
    public bool Disable()
    {
        lock (_gate)
        {
            _disabled = true;
            var persisted = TryWriteRevocation();

            _expiry?.Dispose();
            _expiry = null;
            return DeleteAll() && persisted;
        }
    }

    /// <summary>Beim Start und beim Ablauf: ohne gültige Freigabe alles löschen, sonst nichts.</summary>
    public void Cleanup()
    {
        lock (_gate)
        {
            Valid();
        }
    }

    public IReviewRecording? Begin(int number)
    {
        Consent? consent;
        lock (_gate)
        {
            consent = Valid();
        }

        return consent is null ? null : new Recording(this, number, _time.GetUtcNow(), consent.Id);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _expiry?.Dispose();
            _expiry = null;
        }
    }

    /// <summary>
    /// Regel (Codex-Prüfung PR #60): Eine Aufnahme wird nur veröffentlicht, wenn genau die Freigabe, unter der sie begann,
    /// beim Schreiben noch gilt; geprüft vor und nach dem Verschlüsseln und vor dem Umbenennen. Sonst bleibt nichts von ihr.
    /// </summary>
    private bool Save(string name, byte[] plain, string consentId)
    {
        lock (_gate)
        {
            if (!Allows(consentId))
            {
                return false;
            }
        }

        // Verschlüsseln ausserhalb der Sperre: Ausschalten wartet nicht darauf
        var protectedData = _protector.Protect(plain);
        lock (_gate)
        {
            if (!Allows(consentId))
            {
                return false;
            }

            var path = Path.Combine(Folder, name);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, protectedData);
            if (!Allows(consentId))
            {
                TryDelete(temporary);
                return false;
            }

            File.Move(temporary, path, overwrite: true);
            return true;
        }
    }

    private bool Allows(string consentId) => Valid() is { } consent && string.Equals(consent.Id, consentId, StringComparison.Ordinal);

    /// <summary>Unter der Sperre: die gültige Freigabe; ohne sie wird alles gelöscht, was sich löschen lässt.</summary>
    private Consent? Valid()
    {
        var consent = ReadConsent();
        if (consent is not null && _time.GetUtcNow() < consent.Until && !_disabled && !File.Exists(Path.Combine(Folder, RevokedFile)))
        {
            return consent;
        }

        DeleteAll();
        return null;
    }

    private void ScheduleExpiry(Consent consent)
    {
        _expiry?.Dispose();
        var due = consent.Until - _time.GetUtcNow();
        _expiry = _time.CreateTimer(static state => ((ReviewRecorder)state!).Cleanup(), this,
            due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private Consent? ReadConsent()
    {
        try
        {
            var path = Path.Combine(Folder, StateFile);
            if (!File.Exists(path))
            {
                return null;
            }

            var lines = File.ReadAllLines(path);
            return lines.Length >= 2 && lines[1].Trim() is { Length: > 0 } id
                && DateTimeOffset.TryParse(lines[0].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until)
                ? new Consent(id, until)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Löscht Aufnahmen und Reste einzeln, dann die Freigabe; nur wenn sie weg ist, auch Widerrufsliste und Ordner. Bleibt
    /// die Freigabe stehen (gesperrt), bleibt der Widerruf daneben gültig. Liefert, ob keine Aufnahme mehr da ist.
    /// </summary>
    private bool DeleteAll()
    {
        if (!Directory.Exists(Folder))
        {
            return true;
        }

        var remaining = false;
        foreach (var pattern in new[] { "*" + Extension, "*.tmp" })
        {
            foreach (var file in SafeFiles(pattern))
            {
                remaining |= !TryDelete(file);
            }
        }

        if (TryDelete(Path.Combine(Folder, StateFile)))
        {
            TryDelete(Path.Combine(Folder, RevokedFile));
            try
            {
                Directory.Delete(Folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Leere Reste stören nicht; der nächste Zugriff versucht es erneut
            }
        }

        return !remaining;
    }

    private string[] SafeFiles(string pattern)
    {
        try
        {
            return Directory.GetFiles(Folder, pattern);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Widerruf auf der Platte; ohne Ordner gibt es nichts zu widerrufen. Liefert, ob er feststeht.</summary>
    private bool TryWriteRevocation()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return true;
            }

            File.WriteAllText(Path.Combine(Folder, RevokedFile), "aus");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Der Widerruf im Prozess gilt trotzdem; die Rückmeldung zeigt, dass nicht alles feststeht
            return false;
        }
    }

    private sealed record Consent(string Id, DateTimeOffset Until);

    private sealed class Recording(ReviewRecorder owner, int number, DateTimeOffset start, string consentId) : IReviewRecording
    {
        private readonly Lock _gate = new();
        private readonly MemoryStream _pcm = new();
        private readonly List<ReviewChunk> _chunks = [];
        private readonly List<ReviewSection> _sections = [];
        private int _sampleRate;
        private long _droppedBytes;
        private bool _completed;

        public void Audio(AudioChunk chunk)
        {
            lock (_gate)
            {
                if (_completed || chunk.SampleRate <= 0)
                {
                    return;
                }

                _sampleRate = _sampleRate == 0 ? chunk.SampleRate : _sampleRate;
                if (_pcm.Length + chunk.Pcm16Mono.Length > MaxAudioBytes || chunk.SampleRate != _sampleRate)
                {
                    _droppedBytes += chunk.Pcm16Mono.Length;
                    return;
                }

                _chunks.Add(new ReviewChunk((long)chunk.Offset.TotalMilliseconds, _pcm.Length, chunk.Pcm16Mono.Length));
                _pcm.Write(chunk.Pcm16Mono.Span);
            }
        }

        public void Section(TextSegment segment, OutputReport report)
        {
            lock (_gate)
            {
                if (!_completed)
                {
                    _sections.Add(new ReviewSection(
                        (long)segment.Start.TotalMilliseconds, (long)segment.End.TotalMilliseconds, segment.Text,
                        report.Written, report.Held, report.Result.ToString(), report.Reason.ToString()));
                }
            }
        }

        public string? Complete()
        {
            byte[] plain;
            lock (_gate)
            {
                if (_completed)
                {
                    return null;
                }

                _completed = true;
                if (_chunks.Count == 0 && _sections.Count == 0)
                {
                    return null;
                }

                var header = new ReviewHeader(1, number, start, _sampleRate, _droppedBytes, _chunks, _sections);
                var json = JsonSerializer.SerializeToUtf8Bytes(header, ReviewJson.Default.ReviewHeader);
                plain = new byte[4 + json.Length + _pcm.Length];
                BinaryPrimitives.WriteInt32LittleEndian(plain, json.Length);
                json.CopyTo(plain, 4);
                _pcm.GetBuffer().AsSpan(0, (int)_pcm.Length).CopyTo(plain.AsSpan(4 + json.Length));
                _pcm.Dispose();
            }

            var name = start.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                + "-" + number.ToString(CultureInfo.InvariantCulture) + Extension;
            try
            {
                return owner.Save(name, plain, consentId) ? name : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException)
            {
                return null;
            }
        }
    }
}

/// <summary>Ein Audioblock: Sitzungszeit, Lage und Länge im PCM-Teil (16 Bit mono).</summary>
public sealed record ReviewChunk(long OffsetMs, long Position, int Bytes);

/// <summary>Ein fester Abschnitt: Zeit, erkannter Text, geschriebener und gehaltener Teil, Ergebnis und Grund.</summary>
public sealed record ReviewSection(long StartMs, long EndMs, string Text, string Written, string Held, string Result, string Reason);

/// <summary>Kopf einer Prüfaufnahme, Fassung 1.</summary>
public sealed record ReviewHeader(
    int Version, int Number, DateTimeOffset Start, int SampleRate, long DroppedBytes,
    IReadOnlyList<ReviewChunk> Chunks, IReadOnlyList<ReviewSection> Sections);

[JsonSerializable(typeof(ReviewHeader))]
internal sealed partial class ReviewJson : JsonSerializerContext;
