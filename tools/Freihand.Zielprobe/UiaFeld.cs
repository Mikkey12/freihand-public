using System.Runtime.InteropServices;
using Freihand.Core.Platform;
using Freihand.Windows.Input;
using Freihand.Windows.Targets;
using Windows.Win32.UI.Accessibility;

namespace Freihand.Zielprobe;

/// <summary>
/// Der einzige Ort, der Feldinhalt liest. Der Inhalt verlässt diese Klasse nur Richtung Vergleich mit dem erwarteten
/// Testtext; er wird nie geschrieben, ausgegeben oder in die Zwischenablage gelegt.
/// </summary>
internal static class UiaFeld
{
    private const int StrgTaste = 0x11;
    private const int ATaste = 0x41;
    private const int EntfTaste = 0x2E;

    /// <summary>Strg+Ende (<see cref="Taste"/>): ans Ende des ganzen Inhalts, hebt eine Markierung auf, ohne etwas zu löschen.</summary>
    public const int EndeTaste = 0x23;

    /// <summary>Rücktaste: löscht das Zeichen vor der Schreibmarke.</summary>
    public const int RueckTaste = 0x08;

    /// <summary>Das fokussierte Element oder <c>null</c> ohne Fokus.</summary>
    public static IUIAutomationElement? Fokus(UiaAccess uia) => UiaAccess.OnMta(() =>
    {
        try
        {
            return uia.Automation.GetFocusedElement();
        }
        catch (COMException)
        {
            return null;
        }
    });

    /// <summary>RuntimeId als Punktfolge; <c>null</c>, wenn sie nicht lesbar ist.</summary>
    public static string? RuntimeId(UiaAccess uia, IUIAutomationElement element) => UiaAccess.OnMta(() =>
        uia.Read<int[]>(element, UIA_PROPERTY_ID.UIA_RuntimeIdPropertyId) is { Length: > 0 } teile ? string.Join('.', teile) : null);

    /// <summary>Weg des letzten erfolgreichen Lesens: „Value“ oder „Text“; für die Deutung der Ergebnisse.</summary>
    public static string? LetzterLeseweg { get; private set; }

    /// <summary>
    /// Inhalt über ValuePattern (nur das Feld), sonst TextPattern (Dokument; bei Webfeldern wäre das die ganze Seite);
    /// <c>null</c>, wenn das Feld nicht lesbar ist.
    /// </summary>
    public static string? Inhalt(IUIAutomationElement element) => UiaAccess.OnMta<string?>(() =>
    {
        try
        {
            if (element.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId) is IUIAutomationValuePattern wert)
            {
                LetzterLeseweg = "Value";
                return Lesen(wert.CurrentValue);
            }
        }
        catch (COMException)
        {
            // nicht unterstützt oder nicht lesbar: weiter mit TextPattern
        }

        try
        {
            if (element.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextPatternId) is IUIAutomationTextPattern text)
            {
                LetzterLeseweg = "Text";
                return Lesen(text.DocumentRange.GetText(-1));
            }
        }
        catch (COMException)
        {
            // nicht lesbar
        }

        return null;
    });

    /// <summary>Text eines BSTR; der Speicher wird freigegeben.</summary>
    private static unsafe string Lesen(global::Windows.Win32.Foundation.BSTR bstr)
    {
        var text = bstr.ToString();
        Marshal.FreeBSTR((nint)bstr.Value);
        return text;
    }

    /// <summary>
    /// Eine einzelne Taste, nur solange das gebundene Element fokussiert ist. <see cref="EndeTaste"/> wird mit Strg gesendet:
    /// ans Ende des ganzen Inhalts, auch in mehrzeiligen Feldern (eigene Vorprüfung zu PR #70).
    /// </summary>
    /// <param name="nochGleich">Bei löschenden Tasten (Codex zu PR #70): Reihenfolge Inhalt, dann Ziel, dann sofort die Taste.
    /// Im Sende-Callback wird zuerst gelesen; zeigt das Feld nicht mehr genau den Inhalt des Entscheids, oder ist danach nicht
    /// mehr das gebundene Feld fokussiert, wird nichts gesendet (NotBound). Zwischen der letzten Zielprüfung und der Taste liegt
    /// keine Abfrage mehr, derselbe Abstand wie bei jeder diktierten Ausgabe.</param>
    public static TargetCheck Taste(ITargetBinding binding, Guid bindingId, int taste, Func<bool>? nochGleich = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var api = new SendInputApi();
        InputEvent[] einzeln =
        [
            new InputEvent(InputEventKind.VirtualKey, taste, true, null),
            new InputEvent(InputEventKind.VirtualKey, taste, false, null),
        ];
        var druck = taste == EndeTaste
            ?
            [
                new InputEvent(InputEventKind.VirtualKey, StrgTaste, true, null),
                .. einzeln,
                new InputEvent(InputEventKind.VirtualKey, StrgTaste, false, null),
            ]
            : einzeln;
        var gesendet = false;
        var pruefung = binding.CheckAndSend(bindingId, () =>
        {
            if (nochGleich is null || (nochGleich() && binding.CheckBound(bindingId) == TargetCheck.Focused))
            {
                api.Send(druck);
                gesendet = true;
            }
        });
        return pruefung == TargetCheck.Focused && !gesendet ? TargetCheck.NotBound : pruefung;
    }

    /// <summary>
    /// Strg+A, dann Entf über <c>SendInput</c>, über die Prüfung der Bindung: gesendet wird nur, solange das gebundene
    /// Element noch fokussiert ist (<see cref="ITargetBinding.CheckAndSend"/>). Regel für jedes Löschen (Codex zu PR #70):
    /// vor Strg+A und vor Entf gilt dieselbe Reihenfolge wie bei <see cref="Taste"/>: Inhalt lesen (<paramref name="nochGleich"/>),
    /// dann Ziel prüfen, dann sofort senden; sonst wird nichts gesendet.
    /// </summary>
    public static TargetCheck FeldLeeren(ITargetBinding binding, Guid bindingId, Func<bool> nochGleich)
    {
        ArgumentNullException.ThrowIfNull(nochGleich);
        var api = new SendInputApi();
        var markieren = new[]
        {
            new InputEvent(InputEventKind.VirtualKey, StrgTaste, true, null),
            new InputEvent(InputEventKind.VirtualKey, ATaste, true, null),
            new InputEvent(InputEventKind.VirtualKey, ATaste, false, null),
            new InputEvent(InputEventKind.VirtualKey, StrgTaste, false, null),
        };
        var loeschen = new[]
        {
            new InputEvent(InputEventKind.VirtualKey, EntfTaste, true, null),
            new InputEvent(InputEventKind.VirtualKey, EntfTaste, false, null),
        };
        var geschickt = false;
        var pruefung = binding.CheckAndSend(bindingId, () => geschickt = nochGleich() && binding.CheckBound(bindingId) == TargetCheck.Focused && api.Send(markieren) == markieren.Length);
        if (pruefung != TargetCheck.Focused || !geschickt)
        {
            return pruefung == TargetCheck.Focused ? TargetCheck.NotBound : pruefung;
        }

        Thread.Sleep(150);
        geschickt = false;
        pruefung = binding.CheckAndSend(bindingId, () => geschickt = nochGleich() && binding.CheckBound(bindingId) == TargetCheck.Focused && api.Send(loeschen) == loeschen.Length);
        return pruefung == TargetCheck.Focused && !geschickt ? TargetCheck.NotBound : pruefung;
    }
}
