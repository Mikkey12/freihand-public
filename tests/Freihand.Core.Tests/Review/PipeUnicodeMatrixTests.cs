using System.Text;
using Freihand.Adapters.Recognition;
using Freihand.Core.Tests.Recognition.Kit;
using Freihand.Core.Tests.TestKit;
using static Freihand.Core.Tests.Recognition.Kit.RecognitionTestKit;

namespace Freihand.Core.Tests.Review;

/// <summary>
/// Einzelne Ersatzzeichen im JSON-Kopf der Pipe (P8, Kapitel 4.7 Regel 1; Wächter <see cref="JsonInputUnicodeMatrixTests"/>):
/// Kreuzprodukt aus Richtung, Stelle und Form. Die Pipe hat genau einen JSON-Leser (<c>PipeFraming.ParseHeader</c>, dazu
/// <c>PipeFraming.Read</c> auf dem schon geprüften Kopf); jeder Verstoss ist ein Rahmenfehler.
/// <list type="bullet">
/// <item><b>Richtung:</b> Anfrage <c>decode</c> und <c>detect</c> an den Dienst, Antwort <c>hypothesis</c> an den Client.</item>
/// <item><b>Stelle:</b> bekannter Stringwert (<c>prompt</c> bzw. Wort <c>text</c>; bei <c>detect</c> gibt es keinen und
/// es zählt der unbekannte Wert), Wert und Schlüssel eines unbekannten Felds, Listeneintrag, Wert und Schlüssel in einem
/// verschachtelten Objekt.</item>
/// <item><b>Form:</b> hohe, tiefe Hälfte und umgekehrtes Paar, je als Escape (klein und gross geschrieben) und als rohe
/// ungültige UTF-8-Bytes, dazu gemischt aus rohem und Escape-Teil.</item>
/// <item><b>Erwartung:</b> Dienst: Verbindung zu, keine Antwort, Whisper bzw. VAD nicht aufgerufen. Client: der wartende
/// Aufruf endet mit <see cref="RecognizerUnavailableException"/>, nie ein Hänger oder eine andere Ausnahme.</item>
/// </list>
/// Kontrollen je Richtung und Stelle: ASCII, gültiges Paar als Escape und gültiges rohes Paar werden beantwortet bzw.
/// angenommen. Rotnachweis: fehlt die Prüfung in <c>ParseHeader</c>, beantwortet der Dienst die Escape-Formen in
/// unbekannten Feldern, und der Client übernimmt einzelne Hälften in den Worttext.
/// </summary>
public sealed class PipeUnicodeMatrixTests
{
    private const string Placeholder = "PLACEHOLDERTEXT";

    public enum Direction { ServiceDecode, ServiceDetect, Client }

    public enum Place { KnownValue, UnknownValue, UnknownKey, ListItem, NestedValue, NestedKey }

    private static readonly Dictionary<string, byte[]> Faulty = new()
    {
        ["high-escaped"] = Ascii(@"\ud800"),
        ["low-escaped"] = Ascii(@"\udc00"),
        ["reversed-escaped"] = Ascii(@"\udc00\ud800"),
        ["high-escaped-upper"] = Ascii(@"\uD800"),
        ["low-escaped-upper"] = Ascii(@"\uDC00"),
        ["high-raw"] = [0xED, 0xA0, 0x80],
        ["low-raw"] = [0xED, 0xB0, 0x80],
        ["reversed-raw"] = [0xED, 0xB0, 0x80, 0xED, 0xA0, 0x80],
        ["mixed-raw-high"] = [0xED, 0xA0, 0x80, .. Ascii(@"\udc00")],
        ["mixed-raw-low"] = [.. Ascii(@"\ud800"), 0xED, 0xB0, 0x80],
    };

    private static readonly Dictionary<string, byte[]> Valid = new()
    {
        ["ascii"] = Ascii("A"),
        ["pair-escaped"] = Ascii(@"😀"),
        ["pair-raw"] = [0xF0, 0x9F, 0x98, 0x80],
    };

    public static TheoryData<Direction, Place, string, bool> Cases()
    {
        var cases = new TheoryData<Direction, Place, string, bool>();
        foreach (var direction in Enum.GetValues<Direction>())
        {
            foreach (var place in Enum.GetValues<Place>())
            {
                // detect hat keinen bekannten Stringwert; die Stelle fällt mit UnknownValue zusammen
                if (direction == Direction.ServiceDetect && place == Place.KnownValue)
                {
                    continue;
                }

                foreach (var form in Faulty.Keys)
                {
                    cases.Add(direction, place, form, false);
                }

                foreach (var form in Valid.Keys)
                {
                    cases.Add(direction, place, form, true);
                }
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task LoneSurrogatesInThePipeHeaderAreFrameErrors(Direction direction, Place place, string form, bool valid)
    {
        var bytes = valid ? Valid[form] : Faulty[form];
        if (direction == Direction.Client)
        {
            await ClientAsync(place, bytes, valid);
        }
        else
        {
            await ServiceAsync(direction == Direction.ServiceDecode, place, bytes, valid);
        }
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>Kopf als JSON-Text mit der Stelle als Platzhalter; der Platzhalter wird durch die rohen Bytes ersetzt.</summary>
    private static byte[] Header(string fixedFields, string? knownField, Place place, byte[] form)
    {
        var extra = place switch
        {
            Place.KnownValue => $",\"{knownField}\":\"{Placeholder}\"",
            Place.UnknownValue => $",\"extra\":\"{Placeholder}\"",
            Place.UnknownKey => $",\"{Placeholder}\":1",
            Place.ListItem => $",\"extra\":[\"{Placeholder}\"]",
            Place.NestedValue => $",\"extra\":{{\"inner\":\"{Placeholder}\"}}",
            Place.NestedKey => $",\"extra\":{{\"{Placeholder}\":1}}",
            _ => throw new ArgumentOutOfRangeException(nameof(place)),
        };
        return Fill("{" + fixedFields + extra + "}", form);
    }

    /// <summary>Ersetzt den Platzhalter im JSON-Text durch die rohen Bytes der Form.</summary>
    private static byte[] Fill(string json, byte[] form)
    {
        var text = Encoding.ASCII.GetBytes(json);
        var at = text.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Placeholder));
        return [.. text[..at], .. form, .. text[(at + Placeholder.Length)..]];
    }

    private static async Task ServiceAsync(bool decode, Place place, byte[] form, bool valid)
    {
        using var files = ModelFiles.Create();
        await using var service = await ServiceHarness.ReadyAsync(files.Configuration());
        var samples = Samples(1_600);
        var payload = FrameCodec.FloatBytes(samples);
        var type = decode ? "decode" : "detect";
        var fixedFields = $"\"type\":\"{type}\",\"id\":1,\"offsetTicks\":0,\"sampleCount\":{samples.Length},\"payloadBytes\":{payload.Length}"
            + (decode ? ",\"language\":\"de\"" : string.Empty);
        var channel = service.Channel(decode);
        await channel.SendRawAsync(FrameCodec.Raw(Header(fixedFields, "prompt", place, form), payload));

        if (valid)
        {
            var answer = await channel.ExpectAsync(decode ? "hypothesis" : "regions");
            Assert.Equal(1, answer.Id);
            return;
        }

        Assert.Empty(await channel.ReadToEndAsync());
        Assert.Empty(service.Whisper.Calls);
        Assert.Empty(service.Vad.Calls);
    }

    private static async Task ClientAsync(Place place, byte[] form, bool valid)
    {
        var decodePipe = new DuplexPipe("P8-PIPE-UNICODE");
        var detectPipe = new DuplexPipe("P8-PIPE-UNICODE");
        var recognizer = new FrameChannel(decodePipe.Recognizer);
        await using var client = new RecognizerClient(decodePipe.App, detectPipe.App, ClientTimeouts.Default, new ManualClock());

        var call = client.DecodeAsync(Samples(16_000), Sec(1), German());
        var request = await recognizer.ExpectAsync("decode");

        // Wort „Hallo“ [1 s, 1,5 s); bei KnownValue steht der Platzhalter im Worttext selbst
        var word = place == Place.KnownValue
            ? $"{{\"text\":\"{Placeholder}\",\"startTicks\":{Ticks(1)},\"endTicks\":{Ticks(1.5)},\"probability\":0.9}}"
            : $"{{\"text\":\"Hallo\",\"startTicks\":{Ticks(1)},\"endTicks\":{Ticks(1.5)},\"probability\":0.9}}";
        var fixedFields = $"\"type\":\"hypothesis\",\"id\":{request.Id},\"audioStartTicks\":{Ticks(1)},\"audioEndTicks\":{Ticks(2)},\"words\":[{word}]";
        var header = place == Place.KnownValue ? Fill("{" + fixedFields + "}", form) : Header(fixedFields, null, place, form);
        await recognizer.SendRawAsync(FrameCodec.Raw(header));

        if (valid)
        {
            var hypothesis = await call.WaitAsync(Guard);
            Assert.Single(hypothesis.Words);
            return;
        }

        await Throws<RecognizerUnavailableException>(call);
    }
}
