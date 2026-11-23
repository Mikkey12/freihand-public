namespace Freihand.Core.Recovery;

/// <summary>Einstellungen der Wiederherstellung (ER-06, Grundentscheid 2).</summary>
public sealed record RecoveryOptions
{
    /// <summary>Gesicherter Text wird spätestens so lange nach dem Anlegen gelöscht (ER-06).</summary>
    public static readonly TimeSpan MaxRetention = TimeSpan.FromHours(24);

    /// <summary>Fester Text wird spätestens nach dieser Zeit gesichert (Abnahme ER-06, AU-12).</summary>
    public static readonly TimeSpan MaxSaveInterval = TimeSpan.FromSeconds(1);

    /// <summary>Startwert der Haltefrist (Grundentscheid 2, Anhang B).</summary>
    public static readonly TimeSpan DefaultHoldPeriod = TimeSpan.FromMinutes(15);

    /// <summary>Die ganze Sicherung ein oder aus. Aus heisst: nichts wird geschrieben, Vorhandenes gelöscht.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Haltefrist nach der Übernahme. <see cref="TimeSpan.Zero"/> schaltet sie ab: Der Text wird mit der
    /// Übernahme gelöscht. Nie länger als <see cref="MaxRetention"/>.
    /// </summary>
    public TimeSpan HoldPeriod { get; init; } = DefaultHoldPeriod;

    /// <summary>Takt für Sichern und Löschen nach Frist; höchstens <see cref="MaxSaveInterval"/>.</summary>
    public TimeSpan SaveInterval { get; init; } = MaxSaveInterval;

    public void Validate()
    {
        if (HoldPeriod < TimeSpan.Zero || HoldPeriod > MaxRetention)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HoldPeriod), HoldPeriod, "Die Haltefrist liegt zwischen 0 und 24 Stunden (ER-06).");
        }

        if (SaveInterval <= TimeSpan.Zero || SaveInterval > MaxSaveInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SaveInterval), SaveInterval, "Gesichert wird spätestens jede Sekunde (ER-06).");
        }
    }
}
