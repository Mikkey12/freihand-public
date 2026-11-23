namespace Freihand.Core.Quality;

/// <summary>
/// Eine Testäusserung mit erwartetem und erkanntem Befehl (Kennung aus Anhang A); <c>null</c> heisst:
/// kein Befehl. Nur Kennungen, nie der gesprochene Text.
/// </summary>
public sealed record CommandObservation(string UtteranceId, string? Expected, string? Recognized);

/// <summary>
/// Falsche und verpasste Befehle (QM-02).
/// <list type="bullet">
/// <item><see cref="FalseTriggered"/>: Befehl erkannt, wo keiner erwartet war (Fehlauslösung).</item>
/// <item><see cref="Missed"/>: Befehl erwartet, keiner erkannt.</item>
/// <item><see cref="Confused"/>: Befehl erwartet, ein anderer erkannt.</item>
/// </list>
/// Eine Verwechslung ist zugleich falsch (ein nicht gewollter Befehl wirkt) und verpasst (der gewollte
/// fehlt); darum zählt sie in <see cref="FalseCommands"/> und in <see cref="MissedCommands"/>.
/// </summary>
public sealed record CommandErrors(
    int Utterances,
    int ExpectedCommands,
    int Correct,
    int Missed,
    int FalseTriggered,
    int Confused,
    TimeSpan? DictationDuration)
{
    public int FalseCommands => FalseTriggered + Confused;

    public int MissedCommands => Missed + Confused;

    /// <summary>Fehlauslösungen pro Stunde Diktat (Grundentscheid 5); <c>null</c> ohne Dauer.</summary>
    public double? FalseTriggeredPerHour =>
        DictationDuration is { } d && d > TimeSpan.Zero ? FalseTriggered / d.TotalHours : null;

    /// <summary>Anteil verpasster an erwarteten Befehlen; <c>null</c>, wenn keiner erwartet war.</summary>
    public double? MissedRate => ExpectedCommands > 0 ? (double)MissedCommands / ExpectedCommands : null;

    public static CommandErrors Evaluate(IEnumerable<CommandObservation> observations, TimeSpan? dictationDuration)
    {
        ArgumentNullException.ThrowIfNull(observations);

        int utterances = 0, expected = 0, correct = 0, missed = 0, falseTriggered = 0, confused = 0;
        foreach (var o in observations)
        {
            utterances++;
            expected += o.Expected is null ? 0 : 1;

            switch (o.Expected, o.Recognized)
            {
                case (null, null):
                    break;
                case (null, _):
                    falseTriggered++;
                    break;
                case (_, null):
                    missed++;
                    break;
                default:
                    if (string.Equals(o.Expected, o.Recognized, StringComparison.Ordinal))
                    {
                        correct++;
                    }
                    else
                    {
                        confused++;
                    }

                    break;
            }
        }

        return new CommandErrors(utterances, expected, correct, missed, falseTriggered, confused, dictationDuration);
    }
}
