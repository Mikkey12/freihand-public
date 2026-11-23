using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Segments;
using Freihand.Zielprobe;
using Windows.Win32.UI.Accessibility;

namespace Freihand.Core.Tests.App
{
    public sealed class Pr70MeasurementCallSiteCounterTests
    {
        // Codex-Gegentest AFormattedFieldCannotLoseASuffixDuringHintDetection nicht übernommen, ersetzt durch
        // Pr70HintProbeCallSiteTests: Er verlangt nach der Probe genau „42 kg“; bestehen könnte ihn nur eine Probe, die in kein
        // Feld mit Inhalt tippt (Produktregel vom 21.11.2026: Felder mit Hinweistext bleiben messbar). Die Probe
        // tippt ein Zeichen und löscht in echtem Text nie; der Codex-Gegentest aus der vierten Prüfung (Pr70ThirdReviewCallSiteTests) erlaubt genau das

        [Fact]
        public async Task UserEnteredBlankParagraphsAreNotCleared()
        {
            var field = new ProbeField("41.1", "\r\n\r\n");
            ProbeField.Current.Value = field;
            var path = Path.Combine(Path.GetTempPath(), $"freihand-pr70-{Guid.NewGuid():N}.json");
            try
            {
                var options = Optionen.Lesen(["schreiben", "--wartezeit", "0", "--name", "test", "--out", path,
                    "--messmodus", "--anker", field.RuntimeId, "--teile", "50", "--laenge", "250"]);
                await Schreiben.AusfuehrenAsync(options);

                Assert.Equal(0, field.ClearCalls);
                Assert.StartsWith("\r\n\r\n", field.Text, StringComparison.Ordinal);
            }
            finally
            {
                ProbeField.Current.Value = null;
                File.Delete(path);
            }
        }
    }
}

namespace Windows.Win32.UI.Accessibility
{
    internal interface IUIAutomationElement;
}

namespace Freihand.Windows.Targets
{
    internal sealed class UiaAccess;

    internal sealed class UiaTargetInspector : ITargetInspector, IDisposable
    {
        public UiaTargetInspector(UiaAccess uia) { }

        public event EventHandler? FocusChanged { add { } remove { } }

        public (ElementIdentity Identity, TargetFacts Facts)? Focused()
        {
            var field = ProbeField.Current.Value;
            field?.CompletePendingReloadDuringFocusCheck();
            return (new(1, 2, 3, field?.RuntimeId ?? "41.1", "Edit", "", ""),
                    new("editor", "Edit", "", "Win32", false, false, true, false, false, false));
        }

        public bool Exists(ElementIdentity identity) => true;
        public int CountByAutomationId(long topLevelWindow, string automationId) => 0;
        public bool OnSecureDesktop() => false;
        public void Dispose() { }
    }
}

namespace Freihand.Windows
{
    internal sealed class WindowsTargets : IDisposable
    {
        public WindowsTargets(TargetRuleTable table, OutputOptions options)
        {
            Binding = new TargetBinding(new Targets.UiaTargetInspector(new Targets.UiaAccess()), table);
            Output = new ProbeOutput(Binding);
        }

        public TargetBinding Binding { get; }
        public ITargetMarker Marker => Binding;
        public ITextOutput Output { get; }
        public void Dispose() => Binding.Release();

        // Eigene Ergänzung: wie die echte Ausgabe nur ins gebundene, fokussierte Feld (Prüfung der Bindung je Teil)
        private sealed class ProbeOutput(TargetBinding binding) : ITextOutput
        {
            public Task<OutputReport> WriteAsync(TextSegment segment, TargetInfo target, CancellationToken cancellationToken)
            {
                var check = binding.CheckAndSend(target.BindingId, () => ProbeField.Current.Value!.Insert(segment.Text));
                return Task.FromResult(check == TargetCheck.Focused
                    ? new OutputReport(OutputResult.Written, segment.Text, "", OutputReason.None)
                    : new OutputReport(OutputResult.HeldInDictationWindow, "", segment.Text, OutputReason.FocusElsewhere));
            }
        }
    }
}

namespace Freihand.Zielprobe
{
    // Eigene Ergänzung: hint ist der graue Hinweistext, den das leere Feld als Inhalt meldet (wie Teams)
    internal sealed class ProbeField(string runtimeId, string text, int? endBeforeSuffix = null, bool ignoresControlEnd = false, int? caretAfterInsert = null, string? hint = null, string? draftOnSecondEnd = null, string clearResult = "", string? changeOnRead = null, int changeAtRead = 0, string? draftOnFocusAfterRead = null, int changeOnFocusAfterRead = 0, ProbeField? focusTargetOnRead = null, int moveFocusAtRead = 0, int lagReads = 0) : IUIAutomationElement
    {
        public static AsyncLocal<ProbeField?> Current { get; } = new();

        private int _caret = text.Length;
        private int _endCalls;
        private string _text = text;
        public string RuntimeId { get; } = runtimeId;
        private int _reads;

        // Eigene Ergänzung (Handtest Referenznotebook): ein langsames Ziel zeigt getippten Text erst über lagReads Lesevorgänge verteilt
        private string _pending = string.Empty;
        private int _lagLeft;
        private bool _reloadedOnFocus;

        public void CompletePendingReloadDuringFocusCheck()
        {
            if (!_reloadedOnFocus && _reads == changeOnFocusAfterRead && draftOnFocusAfterRead is not null)
            {
                _reloadedOnFocus = true;
                _text = draftOnFocusAfterRead;
                _caret = _text.Length;
                DraftLoaded = true;
                ReloadDuringFinalCheck = InFinalCheck;
            }
        }

        // Eigene Ergänzung: beim changeAtRead-ten Lesen lädt das Feld changeOnRead nach (Lesen unmittelbar vor dem Leeren)
        public Func<int, string?>? ReadOverride { get; init; }
        public ProbeField? FocusAfterInsert { get; set; }

        // Eigene Ergänzung (Sicherheitsprüfung): verändert eingefügten Text (ein Ziel, das Zeichen verliert)
        public Func<string, string>? InsertFilter { get; set; }

        // Eigene Ergänzung (Sicherheitsprüfung): Der Nutzer klickt in ein anderes Feld; UiaFeld.Fokus liefert dann dieses
        public ProbeField? FocusRedirect { get; set; }

        // Eigene Ergänzung (Sicherheitsprüfung): Der Nutzer leert das Feld von Hand; zählt nicht als Leeren durch das Werkzeug
        public void ClearByUser()
        {
            _pending = string.Empty;
            _text = string.Empty;
            _caret = 0;
        }
        public string? ContentAfterInsert { get; set; }
        public string? Text
        {
            get
            {
                if (++_reads == changeAtRead && changeOnRead is not null)
                {
                    _text = changeOnRead;
                    _caret = _text.Length;
                    DraftLoaded = true;
                }

                if (_reads == moveFocusAtRead && focusTargetOnRead is not null)
                {
                    Current.Value = focusTargetOnRead;
                }

                if (_pending.Length > 0)
                {
                    var count = _lagLeft <= 1 ? _pending.Length : (int)Math.Ceiling(_pending.Length / (double)_lagLeft);
                    InsertNow(_pending[..count]);
                    _pending = _pending[count..];
                    _lagLeft--;
                }

                if (ReadOverride is { } read)
                {
                    return read(_reads);
                }

                return _text.Length == 0 && hint is not null ? hint : _text;
            }
        }
        public int ClearCalls { get; private set; }

        // Eigene Ergänzung für Pr70InterleavingTests: Rohinhalt ohne Nebenwirkung, und ob das Feld einen Entwurf nachgeladen hat
        public string Raw => _text;
        public bool DraftLoaded { get; private set; }

        // Eigene Ergänzung: Nachladen während der letzten Zielprüfung unmittelbar vor einer Taste (die bekannte Grenze)
        public bool InFinalCheck { get; set; }
        public bool ReloadDuringFinalCheck { get; private set; }

        public void End()
        {
            if (++_endCalls == 2 && draftOnSecondEnd is not null)
            {
                _text = draftOnSecondEnd;
            }
            if (!ignoresControlEnd)
            {
                _caret = endBeforeSuffix ?? _text.Length;
            }
        }

        public void Insert(string value)
        {
            if (lagReads > 0)
            {
                _pending += value;
                _lagLeft = lagReads;
                return;
            }

            InsertNow(value);
        }

        private void InsertNow(string value)
        {
            value = InsertFilter?.Invoke(value) ?? value;
            _text = _text.Insert(_caret, value);
            // A controlled editor moves the caret after its fixed unit suffix when it renders the changed value.
            if (ContentAfterInsert is { } content)
            {
                _text = content;
            }

            _caret = caretAfterInsert ?? _text.Length;
            if (FocusAfterInsert is { } target)
            {
                Current.Value = target;
            }
        }

        public void Backspace()
        {
            if (_caret > 0)
                _text = _text.Remove(--_caret, 1);
        }

        public void Clear()
        {
            ClearCalls++;
            _pending = string.Empty;
            _text = clearResult;
            _caret = _text.Length;
        }
    }

    internal static class UiaFeld
    {
        public const int EndeTaste = 0x23;
        public const int RueckTaste = 0x08;
        public static string LetzterLeseweg => "Value";

        public static IUIAutomationElement Fokus(Freihand.Windows.Targets.UiaAccess uia) =>
            ProbeField.Current.Value is { } feld ? feld.FocusRedirect ?? feld : new ProbeField("41.2", string.Empty);
        public static string RuntimeId(Freihand.Windows.Targets.UiaAccess uia, IUIAutomationElement element) =>
            ((ProbeField)element).RuntimeId;
        public static string? Inhalt(IUIAutomationElement element) => ((ProbeField)element).Text;
        // Eigene Ergänzung: nochGleich wie im echten UiaFeld im Sende-Callback: Inhalt lesen, dann Ziel prüfen, dann senden
        public static TargetCheck Taste(ITargetBinding binding, Guid bindingId, int taste, Func<bool>? nochGleich = null) =>
            Senden(binding, bindingId, () =>
            {
                // SendInput dispatches to the field focused when input is sent.
                var focused = ProbeField.Current.Value!;
                if (taste == EndeTaste)
                    focused.End();
                else
                    focused.Backspace();
            }, nochGleich);
        // Wie das echte FeldLeeren in zwei Schritten: Strg+A (löscht nichts), dann Entf, vor jedem Schritt dieselbe Prüfung
        public static TargetCheck FeldLeeren(ITargetBinding binding, Guid bindingId, Func<bool> nochGleich) =>
            Senden(binding, bindingId, () => { }, nochGleich) is var markieren && markieren != TargetCheck.Focused
                ? markieren
                : Senden(binding, bindingId, () => ProbeField.Current.Value!.Clear(), nochGleich);

        private static TargetCheck Senden(ITargetBinding binding, Guid bindingId, Action taste, Func<bool>? nochGleich)
        {
            var gesendet = false;
            var pruefung = binding.CheckAndSend(bindingId, () =>
            {
                if (nochGleich is null || (nochGleich() && LetztePruefung(binding, bindingId) == TargetCheck.Focused))
                {
                    taste();
                    gesendet = true;
                }
            });
            return pruefung == TargetCheck.Focused && !gesendet ? TargetCheck.NotBound : pruefung;
        }

        private static TargetCheck LetztePruefung(ITargetBinding binding, Guid bindingId)
        {
            var feld = ProbeField.Current.Value;
            if (feld is not null)
            {
                feld.InFinalCheck = true;
            }

            try
            {
                return binding.CheckBound(bindingId);
            }
            finally
            {
                if (feld is not null)
                {
                    feld.InFinalCheck = false;
                }
            }
        }
    }
}