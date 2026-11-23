using Freihand.Core.Live;
using Freihand.Core.Routing;

namespace Freihand.Core.Profiles;

/// <summary>Warum ein Modus gilt; liefert die Anzeige nach ER-19.</summary>
public enum ModeReason
{
    /// <summary>Der Modus aus dem Profil gilt.</summary>
    ProfileChoice,

    /// <summary>Erkennungsweg nicht „device“: dafür gibt es keine Live-Abnahme.</summary>
    RouteNotApproved,

    /// <summary>Dieses Gerät hat keine Kennung.</summary>
    DeviceUnknown,

    /// <summary>Modell und Laufzeit der laufenden Erkennung sind nicht bekannt.</summary>
    RecognizerUnknown,

    /// <summary>Gerät, Sprache, Modell und Laufzeit ohne bestandene Live-Abnahme (ER-19).</summary>
    NotApproved,
}

/// <summary>Der wirksame Modus eines Diktats samt Grund; unveränderlich.</summary>
/// <param name="Mode">Wirksamer Modus für das ganze Diktat (an <c>LiveOptions.Mode</c> von P3).</param>
/// <param name="Requested">Der Modus, den das Profil wünscht.</param>
/// <param name="Reason">Warum der wirksame Modus gilt.</param>
public sealed record ModeDecision(LiveMode Mode, RecognitionMode Requested, ModeReason Reason)
{
    /// <summary>ER-19 sichtbar machen: das Profil wollte live, es läuft satzweise.</summary>
    public bool OverridesProfile => Requested == RecognitionMode.Live && Mode == LiveMode.Sentence;
}

/// <summary>Bestimmt aus Profil, Gerät, laufender Erkennung und Einstellung den wirksamen Modus (ER-19, ER-20).</summary>
public static class RecognitionModeResolver
{
    /// <summary>
    /// Beim Diktatstart aufzurufen, sobald P8 „ready“ gemeldet hat; das Ergebnis gilt für das ganze Diktat.
    /// </summary>
    /// <param name="profile">Das gewählte Profil.</param>
    /// <param name="deviceId">Kennung dieses Geräts aus den Einstellungen, null wenn nicht gesetzt.</param>
    /// <param name="recognizer">Tatsächlich geladenes Modell und tatsächliche Laufzeit, null wenn nicht bekannt.</param>
    /// <param name="approvals">Einstellung, wo live abgenommen ist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> oder <paramref name="approvals"/> ist null.</exception>
    public static ModeDecision Resolve(Profile profile, string? deviceId, RecognizerIdentity? recognizer, LiveApprovals approvals)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(approvals);

        var requested = profile.RecognitionMode;

        // Fail-safe: Die erste zutreffende Zeile gilt, und jede fehlende Angabe führt zu satzweise (ER-19).
        if (requested == RecognitionMode.Sentence)
        {
            return new ModeDecision(LiveMode.Sentence, requested, ModeReason.ProfileChoice);
        }

        if (profile.Recognition.Route != RecognitionRoute.Device)
        {
            return new ModeDecision(LiveMode.Sentence, requested, ModeReason.RouteNotApproved);
        }

        if (deviceId is null)
        {
            return new ModeDecision(LiveMode.Sentence, requested, ModeReason.DeviceUnknown);
        }

        if (recognizer is null)
        {
            return new ModeDecision(LiveMode.Sentence, requested, ModeReason.RecognizerUnknown);
        }

        return approvals.Covers(deviceId, profile.Language, recognizer)
            ? new ModeDecision(LiveMode.Live, requested, ModeReason.ProfileChoice)
            : new ModeDecision(LiveMode.Sentence, requested, ModeReason.NotApproved);
    }
}
