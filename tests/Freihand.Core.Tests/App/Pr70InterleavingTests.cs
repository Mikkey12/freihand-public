using Freihand.Zielprobe;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Eigene Prüfung nach sieben Codex-Runden zu PR #70: statt einzelner Zeitpunkte jeder Lesezeitpunkt. An jedem Lesen k lädt
/// das Feld einen Entwurf nach, lädt ihn während der folgenden Fokusprüfung nach, oder der Fokus wechselt in ein anderes Feld
/// mit einem Entwurf. Regel: Kein Entwurf und kein echter Text verliert je ein Zeichen (die Messung darf nur abbrechen).
/// Bekannte Grenze: Vor jeder löschenden Taste wird erst der Inhalt gelesen, dann das Ziel geprüft und sofort gesendet; ein
/// Nachladen während dieser letzten Zielprüfung lässt sich mit keiner Reihenfolge ausschliessen, weil jede Prüfung selbst eine
/// Abfrage ist. Derselbe Abstand gilt für jede diktierte Ausgabe. Nur in diesem Fall darf die Regel verletzt sein.
/// Aufgeteilt nach Ablauf, damit die Fälle parallel laufen.
/// </summary>
public abstract class Pr70InterleavingTests
{
    private const string Draft = "Entwurf";
    private const int Zeitpunkte = 9;

    public enum Ereignis
    {
        Nachladen,
        NachladenBeiFokusPruefung,
        Fokuswechsel,
    }

    public static TheoryData<Ereignis, int> Faelle()
    {
        var faelle = new TheoryData<Ereignis, int>();
        foreach (var ereignis in Enum.GetValues<Ereignis>())
        {
            for (var k = 1; k <= Zeitpunkte; k++)
            {
                faelle.Add(ereignis, k);
            }
        }

        return faelle;
    }

    private protected static async Task Pruefen(Ereignis ereignis, int k, string text, string? hint, string? echt, Func<ProbeField, Task> ablauf)
    {
        var anderes = new ProbeField("41.2", Draft);
        var feld = ereignis switch
        {
            Ereignis.Nachladen => new ProbeField("41.1", text, hint: hint, changeOnRead: Draft, changeAtRead: k),
            Ereignis.NachladenBeiFokusPruefung => new ProbeField("41.1", text, hint: hint, draftOnFocusAfterRead: Draft, changeOnFocusAfterRead: k),
            _ => new ProbeField("41.1", text, hint: hint, focusTargetOnRead: anderes, moveFocusAtRead: k),
        };
        ProbeField.Current.Value = feld;
        try
        {
            await ablauf(feld);

            Assert.Contains(Draft, anderes.Raw, StringComparison.Ordinal);
            if (feld.DraftLoaded)
            {
                Assert.True(feld.Raw.Contains(Draft, StringComparison.Ordinal) || feld.ReloadDuringFinalCheck,
                    "Entwurf verlor ein Zeichen, ohne dass er während der letzten Zielprüfung nachgeladen wurde");
            }
            else if (echt is not null)
            {
                Assert.Contains(echt, feld.Raw, StringComparison.Ordinal);
            }
        }
        finally
        {
            ProbeField.Current.Value = null;
        }
    }

    protected static async Task Messen(string anker, string? hinweistext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"freihand-pr70-{Guid.NewGuid():N}.json");
        try
        {
            var options = Optionen.Lesen(["schreiben", "--wartezeit", "0", "--name", "test", "--out", path,
                "--messmodus", "--anker", anker, "--teile", "50", "--laenge", "250"]);
            await Schreiben.AusfuehrenAsync(options, hinweistext);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class Pr70InterleavingHintProbeTests : Pr70InterleavingTests
{
    [Theory]
    [MemberData(nameof(Faelle))]
    public Task TheHintProbeNeverDeletesADraft(Ereignis ereignis, int k) =>
        Pruefen(ereignis, k, string.Empty, "Eingeben", null, feld => Schreiben.HinweisAsync(feld.RuntimeId));
}

public sealed class Pr70InterleavingRealTextTests : Pr70InterleavingTests
{
    [Theory]
    [MemberData(nameof(Faelle))]
    public Task TheHintProbeNeverDeletesRealText(Ereignis ereignis, int k) =>
        Pruefen(ereignis, k, "Gruss Nilo", null, "Gruss Nilo", feld => Schreiben.HinweisAsync(feld.RuntimeId));
}

public sealed class Pr70InterleavingEmptyFieldTests : Pr70InterleavingTests
{
    [Theory]
    [MemberData(nameof(Faelle))]
    public Task AMeasurementPassNeverDeletesADraft(Ereignis ereignis, int k) =>
        Pruefen(ereignis, k, string.Empty, null, null, feld => Messen(feld.RuntimeId, null));
}

public sealed class Pr70InterleavingProvenHintTests : Pr70InterleavingTests
{
    [Theory]
    [MemberData(nameof(Faelle))]
    public Task AMeasurementPassWithAProvenHintNeverDeletesADraft(Ereignis ereignis, int k) =>
        Pruefen(ereignis, k, string.Empty, "Eingeben", null, feld => Messen(feld.RuntimeId, "Eingeben"));
}
