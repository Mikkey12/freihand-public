using System.Text.Json.Serialization;
using Freihand.Core.Routing;

namespace Freihand.Core.Platform;

/// <summary>Fremder Inhalt, nur im Arbeitsspeicher und nur über <see cref="Reveal"/> erreichbar (SI-12, Kapitel 5.9).</summary>
public sealed class ForeignText
{
    private readonly string _content;

    public ForeignText(string content, ReadPurpose purpose, DataClass dataClass)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(dataClass);
        _content = content;
        Purpose = purpose;
        DataClass = dataClass;
    }

    /// <summary>Als JSON erscheinen nur Zweck und Klasse.</summary>
    [JsonInclude]
    public ReadPurpose Purpose { get; }

    [JsonInclude]
    public DataClass DataClass { get; }

    /// <summary>Einziger Zugang zum Inhalt.</summary>
    public string Reveal() => _content;

    /// <summary>„[Fremdtext]“.</summary>
    public override string ToString() => "[Fremdtext]";
}
