using Freihand.Core.Profiles;

namespace Freihand.Core.Platform;

/// <summary>Bindung an genau ein Feld mit Prüfung vor jedem Teil (P7-Konzept, Kapitel 5.4).</summary>
public sealed class TargetBinding : ITargetBinding, ITargetMarker, IFocusDepartures
{
    private readonly object _gate = new();
    private readonly ITargetInspector _inspector;
    private readonly TargetRuleTable _table;
    private TargetInfo? _bound;
    private TargetRule? _rule;
    private ElementIdentity? _identity;
    private bool _left;
    private int _departures;

    public TargetBinding(ITargetInspector inspector, TargetRuleTable table)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(table);
        _inspector = inspector;
        _table = table;
    }

    public TargetInfo? Bound
    {
        get
        {
            lock (_gate)
            {
                return _bound;
            }
        }
    }

    public TargetRule? BoundRule
    {
        get
        {
            lock (_gate)
            {
                return _rule;
            }
        }
    }

    /// <summary>Bindet das fokussierte Feld mit neuer Kennung; eine abgewiesene Bindung löst auch die bisherige.</summary>
    public BindResult BindFocused(ProfileStrictness strictness)
    {
        lock (_gate)
        {
            Unbind();
            var mark = Mark(strictness);
            return mark.Mark is { } focused ? Bind(focused) : new BindResult(null, mark.Refusal);
        }
    }

    /// <summary>Dieselben Regeln wie beim Binden, aber ohne Bindung und ohne Abonnement (P9 Diktatfenster, Kapitel 3.4).</summary>
    public MarkResult MarkFocused(ProfileStrictness strictness)
    {
        lock (_gate)
        {
            return Mark(strictness);
        }
    }

    /// <summary>Bindet nur genau das gemerkte Feld, mit den Regeln von heute, nicht denen beim Merken (AU-10).</summary>
    public BindResult BindMarked(TargetMark mark, ProfileStrictness strictness)
    {
        ArgumentNullException.ThrowIfNull(mark);
        lock (_gate)
        {
            Unbind();
            var focused = Mark(strictness);
            if (focused.Mark is not { } now)
            {
                return new BindResult(null, focused.Refusal);
            }

            // Gleiches Feld nach der Identitätsregel der gemerkten Zeile; eine andere Zeile heisst ein anderes Feld
            return now.Rule == mark.Rule && SameElement(mark.Rule, mark.Identity, now.Identity)
                ? Bind(now)
                : new BindResult(null, BindRefusal.NotMarkedField);
        }
    }

    /// <summary>Gesicherter Desktop, Fokus, Zieltabelle, lesbare und eindeutige Identität (Kapitel 5.4); unter der Sperre.</summary>
    private MarkResult Mark(ProfileStrictness strictness)
    {
        if (_inspector.OnSecureDesktop())
        {
            return new MarkResult(null, BindRefusal.SecureDesktop);
        }

        if (_inspector.Focused() is not { } focused)
        {
            return new MarkResult(null, BindRefusal.NoFocus);
        }

        var decision = _table.Decide(focused.Facts, strictness);
        if (decision is not Bindable { Rule: var rule })
        {
            return new MarkResult(null, ((NotBindable)decision).Reason);
        }

        if (!Readable(rule, focused.Identity) || (rule.Identity == IdentityMode.AutomationId && !UniqueAutomationId(focused.Identity)))
        {
            return new MarkResult(null, BindRefusal.IdentityUnclear);
        }

        return new MarkResult(new TargetMark(focused.Facts.ProcessName, rule.FieldKind, rule.Display, focused.Identity, rule), null);
    }

    private BindResult Bind(TargetMark focused)
    {
        _bound = new TargetInfo(focused.ProcessName, focused.FieldKind, focused.Display, IsPassword: false)
        {
            BindingId = Guid.NewGuid(),
        };
        _rule = focused.Rule;
        _identity = focused.Identity;
        _left = false;
        _departures = 0;
        _inspector.FocusChanged += OnFocusChanged;
        return new BindResult(_bound, null);
    }

    public bool IsFocused(TargetMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        lock (_gate)
        {
            return _inspector.Focused() is { } focused && SameElement(mark.Rule, mark.Identity, focused.Identity);
        }
    }

    public int? Departures(Guid bindingId)
    {
        lock (_gate)
        {
            return _bound?.BindingId == bindingId ? _departures : null;
        }
    }

    public TargetCheck CheckBound(Guid bindingId)
    {
        lock (_gate)
        {
            return Check(bindingId);
        }
    }

    public TargetCheck CheckAndSend(Guid bindingId, Action send)
    {
        ArgumentNullException.ThrowIfNull(send);
        lock (_gate)
        {
            var check = Check(bindingId);
            if (check == TargetCheck.Focused)
            {
                send();
            }

            return check;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            Unbind();
        }
    }

    /// <summary>Reihenfolge aus Kapitel 5.4; die Sicherheitsmerkmale werden vor jedem Teil neu gelesen.</summary>
    private TargetCheck Check(Guid bindingId)
    {
        if (_bound is null || _bound.BindingId != bindingId || _identity is not { } identity || _rule is not { } rule)
        {
            return TargetCheck.NotBound;
        }

        if (_inspector.OnSecureDesktop())
        {
            return TargetCheck.SecureDesktop;
        }

        // Bei AutomationId darf sich die RuntimeId ändern: Das Feld besteht, solange die Kennung im Fenster vorkommt
        if (!_inspector.Exists(identity) && !(rule.Identity == IdentityMode.AutomationId &&
                _inspector.CountByAutomationId(identity.TopLevelWindow, identity.AutomationId) > 0))
        {
            return TargetCheck.TargetGone;
        }

        if (rule.Identity == IdentityMode.AutomationId && !UniqueAutomationId(identity))
        {
            return TargetCheck.IdentityUnclear;
        }

        if (_inspector.Focused() is not { } focused)
        {
            _departures++;
            return TargetCheck.FocusElsewhere;
        }

        if (!SameElement(rule, identity, focused.Identity))
        {
            _left |= !focused.Facts.IsOwnProcess;
            _departures++;
            return TargetCheck.FocusElsewhere;
        }

        // Zurück im gebundenen Feld: die Bindung gilt wieder (Produktregel vom 11.11.2026; vorher bis zur
        // neuen Bindung gesperrt). Merkmale und Passwortfeld werden unten wie vor jedem Teil neu geprüft.
        _left = false;

        // Gesperrt wird nur, was das Feld nachweislich meldet (Entscheid vom 11.11.2026, SI-04)
        var facts = focused.Facts;
        if (facts.IsPassword == true)
        {
            return TargetCheck.PasswordField;
        }

        return facts.IsElevated == true ? TargetCheck.Elevated : TargetCheck.Focused;
    }

    private void OnFocusChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_identity is not { } identity || _rule is not { } rule)
            {
                return;
            }

            if (_inspector.Focused() is not { } focused)
            {
                // Jeder Wechsel weg vom Feld zählt, auch in ein eigenes Fenster (IFocusDepartures)
                _departures++;
                return;
            }

            if (!SameElement(rule, identity, focused.Identity))
            {
                _departures++;
                _left |= !focused.Facts.IsOwnProcess;
            }
        }
    }

    private bool UniqueAutomationId(ElementIdentity identity) =>
        identity.AutomationId.Length > 0 && _inspector.CountByAutomationId(identity.TopLevelWindow, identity.AutomationId) == 1;

    /// <summary>
    /// Die Bestandteile der Identität sind gelesen, nicht Ersatzwerte eines Lesefehlers: Prozess und Prozessstart, bei
    /// <see cref="IdentityMode.RuntimeId"/> eine RuntimeId, bei <see cref="IdentityMode.AutomationId"/> Fenster und Kennung.
    /// </summary>
    private static bool Readable(TargetRule rule, ElementIdentity identity) =>
        identity.ProcessId > 0 && identity.ProcessStartTicks > 0 &&
        (rule.Identity == IdentityMode.RuntimeId
            ? identity.RuntimeId.Length > 0
            : identity.TopLevelWindow != 0 && identity.AutomationId.Length > 0);

    /// <summary>Feldidentität nach <see cref="IdentityMode"/>; Fenster, Art und Klasse allein reichen nie, Ersatzwerte nie.</summary>
    private static bool SameElement(TargetRule rule, ElementIdentity bound, ElementIdentity focused) =>
        Readable(rule, focused) && bound.ProcessId == focused.ProcessId && bound.ProcessStartTicks == focused.ProcessStartTicks &&
        (rule.Identity == IdentityMode.RuntimeId
            ? bound.RuntimeId == focused.RuntimeId
            : bound.TopLevelWindow == focused.TopLevelWindow && bound.ControlType == focused.ControlType &&
              bound.ClassName == focused.ClassName && bound.AutomationId.Length > 0 && bound.AutomationId == focused.AutomationId);

    private void Unbind()
    {
        if (_bound is not null)
        {
            _inspector.FocusChanged -= OnFocusChanged;
        }

        _bound = null;
        _rule = null;
        _identity = null;
        _left = false;
        _departures = 0;
    }
}
