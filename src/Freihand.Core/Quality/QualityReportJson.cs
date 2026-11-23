using System.Text.Json;
using System.Text.Json.Serialization;

namespace Freihand.Core.Quality;

/// <summary>
/// JSON-Export des Messergebnisses (QM-02). Format: Eigenschaften in camelCase, Aufzählungen als Text in
/// camelCase, alle Zeitdauern als Zahl in Millisekunden, fehlende Werte als <c>null</c>. Abgeleitete Werte
/// wie <c>rate</c> und <c>errors</c> werden mitgeschrieben und beim Einlesen neu berechnet. Das Feld
/// <c>schema</c> nennt die Fassung des Formats (<see cref="QualityReport.CurrentSchema"/>).
/// </summary>
public static class QualityReportJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize(QualityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, Options);
    }

    public static QualityReport Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        // Der ganze Text zuerst, wie beim Import von Profilen: System.Text.Json überspringt unbekannte Felder und
        // decodiert ihre Werte nicht, ein escaptes einzelnes Ersatzzeichen dort bliebe sonst unbemerkt (W01).
        if (JsonUnicode.HasLoneSurrogate(json))
        {
            throw new JsonException("Der Messbericht enthält ein ungültiges Unicode-Zeichen (einzelnes Ersatzzeichen).");
        }

        QualityReport? report;
        try
        {
            report = JsonSerializer.Deserialize<QualityReport>(json, Options);
        }
        catch (ArgumentException exception)
        {
            // Ein einzelnes Ersatzzeichen (halbes Zeichenpaar) im Text lässt sich nicht als UTF-8 lesen; der Fehler
            // des Formats ist wie sonst eine JsonException, nicht eine Ausnahme des Zeichensatzes.
            throw new JsonException("Der Messbericht enthält ein ungültiges Unicode-Zeichen (einzelnes Ersatzzeichen).", exception);
        }

        if (report is null)
        {
            throw new JsonException("Leerer Messbericht.");
        }

        return report.Schema == QualityReport.CurrentSchema
            ? report
            : throw new JsonException($"Unbekanntes Format „{report.Schema}“, erwartet „{QualityReport.CurrentSchema}“.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new MillisecondsConverter());
        return options;
    }

    /// <summary>Zeitdauer als Millisekunden, auf ganze Ticks gerundet.</summary>
    private sealed class MillisecondsConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            TimeSpan.FromTicks((long)Math.Round(reader.GetDouble() * TimeSpan.TicksPerMillisecond));

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
            writer.WriteNumberValue((double)value.Ticks / TimeSpan.TicksPerMillisecond);
    }
}
