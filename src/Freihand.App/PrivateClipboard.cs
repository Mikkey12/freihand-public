using System.IO;
using System.Windows;

namespace Freihand.App;

/// <summary>
/// Regel für jedes Kopieren aus Freihand (Prüfung der IT-Unterlagen, 22.11.2026): Diktierter Text gehört nur in die
/// aktuelle Zwischenablage, nicht in den Verlauf von Windows (Win+V) und nicht in die Cloud-Zwischenablage. Dafür kommen
/// die Formate dazu, die Windows dafür auswertet: <c>ExcludeClipboardContentFromMonitorProcessing</c>,
/// <c>CanIncludeInClipboardHistory</c> = 0 und <c>CanUploadToCloudClipboard</c> = 0. Gilt für die Knöpfe (Diktatfenster,
/// gehaltener Text, Vorschlag) über <see cref="SetText"/> und für Strg+C, Strg+X und das Kontextmenü in jedem Textfeld
/// über einen Klassenhandler (<see cref="Register"/>); im Profil „Streng“ wird Kopieren aus Textfeldern abgebrochen (AU-03).
/// Freihand liest die Zwischenablage nicht von sich aus.
/// </summary>
internal static class PrivateClipboard
{
    public const string ExcludeFromMonitor = "ExcludeClipboardContentFromMonitorProcessing";
    public const string HistoryFormat = "CanIncludeInClipboardHistory";
    public const string CloudFormat = "CanUploadToCloudClipboard";

    /// <summary>Inhalt fürs Kopieren: Text samt den drei Ausschlussformaten (je ein DWORD 0).</summary>
    public static DataObject Create(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        data.SetData(ExcludeFromMonitor, Zero());
        data.SetData(HistoryFormat, Zero());
        data.SetData(CloudFormat, Zero());
        return data;
    }

    /// <summary>Legt den Text in die Zwischenablage, ohne Verlauf und Cloud; Ausnahmen wie bei <see cref="Clipboard.SetText(string)"/>.</summary>
    public static void SetText(string text) => Clipboard.SetDataObject(Create(text), copy: true);

    private static int _registered;

    /// <summary>Ob Kopieren aus Textfeldern erlaubt ist (nicht im Profil „Streng“).</summary>
    public static bool CopyAllowed { get; private set; } = true;

    /// <summary>Meldet einmal den Klassenhandler für jedes Textfeld an; <paramref name="copyAllowed"/> gilt ab sofort.</summary>
    public static void Register(bool copyAllowed)
    {
        CopyAllowed = copyAllowed;
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.TextBoxBase), DataObject.CopyingEvent,
                new DataObjectCopyingEventHandler(OnCopying));
        }
    }

    /// <summary>Strg+C, Strg+X und Kontextmenü in einem Textfeld: Ausschlussformate dazu, im Profil „Streng“ abbrechen.</summary>
    internal static void OnCopying(object sender, DataObjectCopyingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!CopyAllowed)
        {
            e.CancelCommand();
            return;
        }

        e.DataObject.SetData(ExcludeFromMonitor, Zero());
        e.DataObject.SetData(HistoryFormat, Zero());
        e.DataObject.SetData(CloudFormat, Zero());
    }

    private static MemoryStream Zero() => new(BitConverter.GetBytes(0));
}
