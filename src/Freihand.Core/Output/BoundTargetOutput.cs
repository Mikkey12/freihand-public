using Freihand.Core.Platform;
using Freihand.Core.Segments;

namespace Freihand.Core.Output;

/// <summary>Ausgabe ins gebundene Ziel (P7-Konzept, Kapitel 5.7).</summary>
public sealed class BoundTargetOutput : IConditionalTextOutput, IFocusWaitReporter
{
    private readonly ITargetBinding _binding;
    private readonly ITextInjector _injector;
    private readonly IHotkeyService _hotkeys;
    private readonly OutputOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Aufträge nacheinander in der Reihenfolge der Aufrufe (Schritt 2).</summary>
    private readonly SemaphoreSlim _turn = new(1, 1);

    /// <summary>
    /// Je Bindung der früheste Auftrag, der unvollständig blieb, mit seinem Grund (Schritt 4). Jeder spätere Auftrag dieser
    /// Bindung wird ganz gehalten; eine neue Bindung hat keinen Eintrag. Auch ein vor seiner Reihe abgebrochener Auftrag
    /// trägt sich hier in der Reihenfolge der Aufrufe ein, damit ihn kein späterer überholt.
    /// </summary>
    private readonly Dictionary<Guid, (long Order, OutputReason Reason)> _blocked = [];

    private long _order;

    /// <summary>Fügezustand der Bindung, in die zuletzt geschrieben wurde (Schritt 5).</summary>
    private Guid _joinFor;
    private string _lastWritten = string.Empty;

    public BoundTargetOutput(
        ITargetBinding binding, ITextInjector injector, IHotkeyService hotkeys, OutputOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(injector);
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _binding = binding;
        _injector = injector;
        _hotkeys = hotkeys;
        _options = options;
        _time = time;
    }

    /// <summary>Beginn und Ende jedes Wartens auf die Rückkehr des Fokus; Abonnenten kehren sofort zurück.</summary>
    public event EventHandler<FocusWait>? FocusWaitChanged;

    /// <summary>In fremde Programme gelangt nur fester Text (BE-01): ein vorläufiger Abschnitt wirft sofort.</summary>
    public Task<OutputReport> WriteAsync(TextSegment segment, TargetInfo target, CancellationToken cancellationToken = default) =>
        Write(segment, target, null, cancellationToken);

    /// <inheritdoc cref="IConditionalTextOutput"/>
    public Task<OutputReport> WriteAsync(TextSegment segment, TargetInfo target, Func<bool> condition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return Write(segment, target, condition, cancellationToken);
    }

    private Task<OutputReport> Write(TextSegment segment, TargetInfo target, Func<bool>? condition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(target);
        if (segment.State != SegmentState.Fixed)
        {
            throw new ArgumentException("Nur fester Text gelangt ins Ziel (BE-01).", nameof(segment));
        }

        long order;
        lock (_blocked)
        {
            order = ++_order;
        }

        return WriteInTurnAsync(segment.Text, target.BindingId, order, condition, cancellationToken);
    }

    private async Task<OutputReport> WriteInTurnAsync(
        string text, Guid bindingId, long order, Func<bool>? condition, CancellationToken cancellationToken)
    {
        try
        {
            await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Noch nicht begonnen, aber in der Reihenfolge verbucht: spätere Aufträge dieser Bindung überholen ihn nicht
            return Block(bindingId, order, Held(text, OutputReason.Cancelled));
        }

        try
        {
            return await WriteAsync(text, bindingId, order, condition, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

    private async Task<OutputReport> WriteAsync(
        string text, Guid bindingId, long order, Func<bool>? condition, CancellationToken cancellationToken)
    {
        // 3. Angeforderte Bindung; der Rest eines Auftrags geht nie an eine spätere Bindung
        if (!IsCurrent(bindingId) || _binding.BoundRule is not { } rule)
        {
            return Held(text, OutputReason.NotBound);
        }

        // 4. Nach einem unvollständigen Abschnitt wird jeder spätere ganz gehalten, bis neu gebunden wird
        if (BlockedBefore(bindingId, order) is { } blocked)
        {
            return Held(text, blocked);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Block(bindingId, order, Held(text, OutputReason.Cancelled));
        }

        if (_joinFor != bindingId)
        {
            _joinFor = bindingId;
            _lastWritten = string.Empty;
        }

        // 5. Fügeregel; nach einer neuen Bindung kein Leerzeichen, weil P7 den Text vor der Schreibmarke nicht liest
        var separator = Separator(_lastWritten, text);
        var full = separator + text;

        // 6. Plan vor jeder Ausgabe
        var plan = InjectionPlanner.Plan(full, rule, _options.ControlKeys);
        if (plan is not PlannedParts { Parts: var parts })
        {
            return Block(bindingId, order, new OutputReport(OutputResult.Refused, string.Empty, text, ((PlanRefused)plan).Reason));
        }

        // 7. Eigene Tasten loslassen lassen, dann wieder Schritt 3
        try
        {
            if (!await OwnKeysReleasedAsync(cancellationToken).ConfigureAwait(false))
            {
                return Block(bindingId, order, Held(text, OutputReason.KeysHeld));
            }
        }
        catch (OperationCanceledException)
        {
            return Block(bindingId, order, Held(text, OutputReason.Cancelled));
        }

        if (!IsCurrent(bindingId))
        {
            return Held(text, OutputReason.NotBound);
        }

        // 8. Je Teil prüfen und unter der Sperre der Bindung senden; ein Abbruch während der Prüfung verhindert den Teil.
        // Die Frist für die Rückkehr des Fokus beginnt beim ersten FocusElsewhere und gilt, bis wieder ein Teil gesendet ist;
        // ein kurzes Zurück und wieder Weg startet sie nicht neu (Codex zu PR #50)
        var written = 0;
        long? waitingSince = null;
        int? departures = null;
        for (var index = 0; index < parts.Count; index++)
        {
            // Bedingung nach jedem Warten, unmittelbar vor dem ersten Zeichen (IConditionalTextOutput); ab dann zählt jede
            // Unterbrechung, auch wenn der Fokus inzwischen zurück ist: dann wird nicht weitergeschrieben
            if (condition is not null && written == 0)
            {
                if (!condition())
                {
                    return new OutputReport(OutputResult.Refused, string.Empty, text, OutputReason.ConditionFailed);
                }

                departures = Departures(bindingId);
            }
            else if (condition is not null && Departures(bindingId) != departures)
            {
                return Block(bindingId, order, Partial(full, separator, written, OutputResult.HeldInDictationWindow, OutputReason.FocusElsewhere));
            }

            var part = parts[index];
            InjectionResult? result = null;
            var cancelled = false;
            var check = _binding.CheckAndSend(bindingId, () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    return;
                }

                result = _injector.Inject(part);
            });
            if (cancelled || (check == TargetCheck.Focused && result is null))
            {
                return Block(bindingId, order, Partial(full, separator, written, OutputResult.HeldInDictationWindow, OutputReason.Cancelled));
            }

            // Mit Bedingung und schon Geschriebenem: nicht weiterschreiben, die Stelle ist nicht mehr prüfbar
            if (check == TargetCheck.FocusElsewhere && result is null && condition is not null && written > 0)
            {
                return Block(bindingId, order, Partial(full, separator, written, OutputResult.HeldInDictationWindow, OutputReason.FocusElsewhere));
            }

            // Fokus woanders: auf die Rückkehr ins selbe Feld warten und denselben Teil erneut prüfen und senden
            if (check == TargetCheck.FocusElsewhere && result is null)
            {
                waitingSince ??= _time.GetTimestamp();
                try
                {
                    check = await FocusReturnAsync(bindingId, waitingSince.Value, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Block(bindingId, order, Partial(full, separator, written, OutputResult.HeldInDictationWindow, OutputReason.Cancelled));
                }

                if (check == TargetCheck.Focused)
                {
                    index--;
                    continue;
                }
            }

            if (check != TargetCheck.Focused || result is null)
            {
                var resultKind = check == TargetCheck.PasswordField ? OutputResult.Refused : OutputResult.HeldInDictationWindow;
                return Block(bindingId, order, Partial(full, separator, written, resultKind, Reason(check)));
            }

            written += Math.Clamp(result.Units, 0, part.Text.Length);
            waitingSince = null;
            if (result.KeysMaybeDown.Count > 0)
            {
                return Block(
                    bindingId, order,
                    Partial(full, separator, written, OutputResult.HeldInDictationWindow, OutputReason.KeyReleaseFailed),
                    OutputReason.KeyReleaseFailed);
            }

            if (result.Units < part.Text.Length)
            {
                return Block(bindingId, order, Partial(full, separator, written, OutputResult.HeldInDictationWindow, OutputReason.InjectionBlocked));
            }

            try
            {
                await Task.Delay(_options.PartDelay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (index < parts.Count - 1)
            {
                return Block(bindingId, order, Partial(full, separator, written, OutputResult.HeldInDictationWindow, OutputReason.Cancelled));
            }
            catch (OperationCanceledException)
            {
                // Der letzte Teil ist eingefügt; der Abschnitt ist vollständig geschrieben
            }

            if (index < parts.Count - 1 && !IsCurrent(bindingId))
            {
                return Partial(full, separator, written, OutputResult.HeldInDictationWindow, OutputReason.NotBound);
            }
        }

        // Ein leerer Abschnitt fügt nichts ein und lässt den Fügezustand, wie er ist
        if (full.Length > 0)
        {
            _lastWritten = full;
        }

        return new OutputReport(OutputResult.Written, full, string.Empty, OutputReason.None);
    }

    /// <summary>Wechsel weg vom Feld seit dem Binden, wenn die Bindung sie zählt (<see cref="IFocusDepartures"/>).</summary>
    private int? Departures(Guid bindingId) => (_binding as IFocusDepartures)?.Departures(bindingId);

    /// <summary>
    /// Wartet höchstens <c>FocusReturnLimit</c>, bis die Bindung wieder <see cref="TargetCheck.Focused"/> meldet, und gibt
    /// die letzte Prüfung zurück. Jede andere Meldung als <see cref="TargetCheck.FocusElsewhere"/> (Feld weg, Passwortfeld,
    /// neu gebunden) beendet das Warten sofort und wird zum Grund des Haltens.
    /// </summary>
    private async Task<TargetCheck> FocusReturnAsync(Guid bindingId, long waitingSince, CancellationToken cancellationToken)
    {
        var check = TargetCheck.FocusElsewhere;
        if (_options.FocusReturnLimit <= TimeSpan.Zero)
        {
            return check;
        }

        Report(new FocusWait(bindingId, _options.FocusReturnLimit - _time.GetElapsedTime(waitingSince)));
        try
        {
            while (_time.GetElapsedTime(waitingSince) < _options.FocusReturnLimit)
            {
                await Task.Delay(_options.FocusReturnPoll, _time, cancellationToken).ConfigureAwait(false);
                check = _binding.CheckBound(bindingId);
                if (check != TargetCheck.FocusElsewhere)
                {
                    return check;
                }
            }

            return check;
        }
        finally
        {
            Report(new FocusWait(bindingId, null));
        }
    }

    /// <summary>Ein Fehler der Anzeige stört das Schreiben nicht.</summary>
    private void Report(FocusWait wait)
    {
        try
        {
            FocusWaitChanged?.Invoke(this, wait);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Anzeige fehlerhaft; geschrieben wird trotzdem
        }
    }

    /// <summary>Ruft alle <c>KeyWaitPoll</c> <see cref="IHotkeyService.OwnKeysReleased"/>, höchstens <c>KeyWaitLimit</c> lang.</summary>
    private async Task<bool> OwnKeysReleasedAsync(CancellationToken cancellationToken)
    {
        var start = _time.GetTimestamp();
        while (!_hotkeys.OwnKeysReleased())
        {
            if (_time.GetElapsedTime(start) >= _options.KeyWaitLimit)
            {
                return false;
            }

            await Task.Delay(_options.KeyWaitPoll, _time, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private bool IsCurrent(Guid bindingId) => _binding.Bound?.BindingId == bindingId;

    private OutputReason? BlockedBefore(Guid bindingId, long order)
    {
        lock (_blocked)
        {
            return _blocked.TryGetValue(bindingId, out var block) && block.Order < order ? block.Reason : null;
        }
    }

    /// <summary>Merkt sich den frühesten unvollständigen Auftrag dieser Bindung (Schritt 4).</summary>
    private OutputReport Block(Guid bindingId, long order, OutputReport report, OutputReason reason = OutputReason.PreviousHeld)
    {
        lock (_blocked)
        {
            if (!_blocked.TryGetValue(bindingId, out var block) || order < block.Order)
            {
                _blocked[bindingId] = (order, reason);
            }
        }

        return report;
    }

    /// <summary>Trennzeichen nach <see cref="TextJoin"/> zwischen dem zuletzt geschriebenen Text und dem Abschnitt.</summary>
    private static string Separator(string previous, string text)
    {
        // Ein Teil, der selbst mit einem Leerraum beginnt (Leerzeichen nach dem Diktat, Handtest P9), trennt sich selbst
        if (previous.Length == 0 || text.Length == 0 || char.IsWhiteSpace(text[0]))
        {
            return string.Empty;
        }

        var joined = TextJoin.Join([previous, text]);
        return joined[previous.Length..^text.Length];
    }

    /// <summary>Bericht nach einem Teilerfolg: <c>Written</c> samt Trennzeichen, <c>Held</c> der Rest ohne Trennzeichen.</summary>
    private static OutputReport Partial(string full, string separator, int written, OutputResult result, OutputReason reason) =>
        new(result, full[..written], full[Math.Max(written, separator.Length)..], reason);

    private static OutputReport Held(string text, OutputReason reason) =>
        new(OutputResult.HeldInDictationWindow, string.Empty, text, reason);

    private static OutputReason Reason(TargetCheck check) => check switch
    {
        TargetCheck.NotBound => OutputReason.NotBound,
        TargetCheck.FocusElsewhere => OutputReason.FocusElsewhere,
        TargetCheck.PasswordField => OutputReason.PasswordField,
        TargetCheck.TargetGone => OutputReason.TargetGone,
        TargetCheck.SecureDesktop => OutputReason.SecureDesktop,
        TargetCheck.IdentityUnclear => OutputReason.IdentityUnclear,
        TargetCheck.Elevated => OutputReason.Elevated,
        TargetCheck.ReadOnly => OutputReason.ReadOnly,
        _ => OutputReason.InjectionBlocked,
    };
}
