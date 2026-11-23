namespace Freihand.Core.Platform;

/// <summary>Ereignisplan, ein <c>Send</c> je Teil, Freigabe nach Teilerfolg (P7-Konzept, Kapitel 5.7).</summary>
public sealed class SendInputInjector : ITextInjector
{
    private readonly ISendInputApi _api;

    public SendInputInjector(ISendInputApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    /// <summary>
    /// Fügt <c>Send</c> nicht alle Ereignisse ein, gibt ein zweites <c>Send</c> genau die selbst gedrückten Tasten frei;
    /// bleibt etwas offen, wird der Rest genau einmal wiederholt und sonst in <see cref="InjectionResult.KeysMaybeDown"/> gemeldet.
    /// </summary>
    public InjectionResult Inject(InjectionPart part)
    {
        var events = InputEventPlanner.Events(part);
        var inserted = events.Count == 0 ? 0 : _api.Send(events);
        var units = InputEventPlanner.UnitsInserted(events, inserted);
        if (inserted >= events.Count)
        {
            return new InjectionResult(units, []);
        }

        IReadOnlyList<InputEvent> open = InputEventPlanner.ReleaseAfter(events, inserted);
        for (var attempt = 0; attempt < 2 && open.Count > 0; attempt++)
        {
            open = [.. open.Skip(_api.Send(open))];
        }

        return new InjectionResult(units, [.. open.Select(item => item.Code)]);
    }
}
