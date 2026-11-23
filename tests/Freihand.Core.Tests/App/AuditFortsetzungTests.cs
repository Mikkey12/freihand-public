using Freihand.Adapters.Dictation;
using Freihand.Zielprobe;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Sicherheitsprüfung, F2: der ganze Fortsetzungsweg von „Feld messen“. Eine grosse Stufe kommt verändert an, das Werkzeug löscht
/// nicht selbst, der Nutzer leert das Testfeld, die Messung geht kleiner weiter und speichert nur den kleineren Wert. Ein
/// Fokuswechsel während der Pause setzt nie fort, auch nicht in ein leeres anderes Feld.
/// </summary>
public sealed class AuditFortsetzungTests
{
    [Fact]
    public async Task AFailedLargeStepWaitsForTheUserAndOnlyTheSmallerValueIsSaved()
    {
        var field = new ProbeField("41.1", string.Empty);
        var verloren = 1;
        field.InsertFilter = text => verloren-- > 0 ? text[1..] : text;
        var path = Path.Combine(Path.GetTempPath(), $"freihand-audit-{Guid.NewGuid():N}.json");
        ProbeField.Current.Value = field;
        try
        {
            var lauf = Messen.AusfuehrenAsync(Optionen.Lesen(["messen", "--wartezeit", "0", "--out", path, "--leeren-frist", "30"]));
            await Warten(() => File.Exists(Messen.PausePfad(path)) || lauf.IsCompleted);
            Assert.False(lauf.IsCompleted);

            // Kein automatisches Löschen des abweichenden Inhalts; der Nutzer leert selbst
            Assert.Equal(0, field.ClearCalls);
            Assert.NotEqual(string.Empty, field.Raw);
            field.ClearByUser();

            Assert.Equal(0, await lauf);
            var summary = await File.ReadAllTextAsync(Messen.ZusammenfassungPfad(path));
            Assert.Contains("durchgang = 50;nein;ja;-", summary, StringComparison.Ordinal);
            var result = FieldMeasurement.Evaluate(summary, new DateOnly(2026, 11, 22));
            Assert.NotNull(result.Field);
            Assert.True(result.Field.Characters < 50);
            Assert.False(result.Field.Manual);
            Assert.Equal(string.Empty, field.Raw);
            Assert.False(File.Exists(Messen.PausePfad(path)));
        }
        finally
        {
            ProbeField.Current.Value = null;
            Aufraeumen(path);
        }
    }

    [Fact]
    public async Task AFocusChangeDuringThePauseNeverContinuesInTheOtherField()
    {
        var field = new ProbeField("41.1", string.Empty) { InsertFilter = text => text[1..] };
        var other = new ProbeField("41.9", string.Empty);
        var path = Path.Combine(Path.GetTempPath(), $"freihand-audit-{Guid.NewGuid():N}.json");
        ProbeField.Current.Value = field;
        try
        {
            var lauf = Messen.AusfuehrenAsync(Optionen.Lesen(["messen", "--wartezeit", "0", "--out", path, "--leeren-frist", "5"]));
            await Warten(() => File.Exists(Messen.PausePfad(path)) || lauf.IsCompleted);

            // Der Nutzer leert das Testfeld und klickt dann in ein anderes, leeres Feld
            field.ClearByUser();
            field.FocusRedirect = other;

            Assert.NotEqual(0, await lauf);
            var result = FieldMeasurement.Evaluate(await File.ReadAllTextAsync(Messen.ZusammenfassungPfad(path)), new DateOnly(2026, 11, 22));
            Assert.Null(result.Field);
            Assert.True(result.ClearByHand);
            Assert.Equal(string.Empty, other.Raw);
            Assert.Equal(0, other.ClearCalls);
        }
        finally
        {
            ProbeField.Current.Value = null;
            Aufraeumen(path);
        }
    }

    private static async Task Warten(Func<bool> bedingung)
    {
        var bis = DateTime.UtcNow.AddSeconds(60);
        while (!bedingung())
        {
            Assert.True(DateTime.UtcNow < bis, "Zeitgrenze");
            await Task.Delay(50);
        }
    }

    private static void Aufraeumen(string path)
    {
        foreach (var datei in new[] { path, Messen.ZusammenfassungPfad(path), Messen.PausePfad(path) })
        {
            File.Delete(datei);
        }
    }
}
