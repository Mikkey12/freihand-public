using Freihand.Zielprobe;

namespace Freihand.Core.Tests.App;

/// <summary>
/// Die Hinweisprüfung am echten Code mit dem simulierten Feld aus <c>Pr70MeasurementCallSiteCounterTests</c>. Seit dem Audit
/// zu 5b6c7de (F5) tippt sie nie: Ein nichtleerer Wert heisst „belegt“ und bleibt unverändert, auch wenn er nur ein Hinweis ist.
/// </summary>
public sealed class Pr70HintProbeCallSiteTests
{
    [Fact]
    public async Task AFormattedFieldKeepsItsSuffixAndOnlyTheNamedTestCharacterIsLeft()
    {
        var field = new ProbeField("41.1", "42 kg", endBeforeSuffix: 2);
        ProbeField.Current.Value = field;
        try
        {
            var (hint, result) = await Schreiben.HinweisAsync(field.RuntimeId);

            Assert.Null(hint);
            Assert.Equal("belegt", result);
            Assert.Equal("42 kg", field.Text);
        }
        finally
        {
            ProbeField.Current.Value = null;
        }
    }

    [Fact]
    public async Task RealTextIsNeverDeletedFromEvenWhenTheTestCharacterIsLast()
    {
        var field = new ProbeField("41.1", "42 kg");
        ProbeField.Current.Value = field;
        try
        {
            var (hint, result) = await Schreiben.HinweisAsync(field.RuntimeId);

            Assert.Null(hint);
            Assert.Equal("belegt", result);
            Assert.Equal("42 kg", field.Text);
        }
        finally
        {
            ProbeField.Current.Value = null;
        }
    }

    [Fact]
    public async Task AnEmptyFieldIsMeasuredAndClearedAsBefore()
    {
        var field = new ProbeField("41.1", string.Empty);
        ProbeField.Current.Value = field;
        var path = Path.Combine(Path.GetTempPath(), $"freihand-pr70-{Guid.NewGuid():N}.json");
        try
        {
            var options = Optionen.Lesen(["schreiben", "--wartezeit", "0", "--name", "test", "--out", path,
                "--messmodus", "--anker", field.RuntimeId, "--teile", "50", "--laenge", "250"]);
            Assert.Equal(0, await Schreiben.AusfuehrenAsync(options));

            Assert.Equal(1, field.ClearCalls);
            Assert.Equal(string.Empty, field.Text);
        }
        finally
        {
            ProbeField.Current.Value = null;
            File.Delete(path);
        }
    }

    // Sicherheitsprüfung, F5: Meldet Windows den Hinweistext als Wert, ist er ohne Tippen nicht von echtem Inhalt zu unterscheiden;
    // dann wird nichts getippt und nichts gemessen
    [Theory]
    [InlineData("Nachricht eingeben")]
    [InlineData("Nachricht eingeben\r")]
    public async Task AHintReportedAsValueIsNeverTypedInto(string hint)
    {
        var field = new ProbeField("41.1", string.Empty, hint: hint);
        ProbeField.Current.Value = field;
        try
        {
            var (proven, result) = await Schreiben.HinweisAsync(field.RuntimeId);
            Assert.Equal(("belegt", (string?)null), (result, proven));
            Assert.Equal(string.Empty, field.Raw);
            Assert.Equal(0, field.ClearCalls);
        }
        finally
        {
            ProbeField.Current.Value = null;
        }
    }

    // Handtest Referenznotebook (22.11.2026): Ein langsames Ziel zeigt den Text erst nach und nach; bewertet und geleert wird erst,
    // wenn das Feld zweimal gleich gelesen wurde, sonst bricht die Messung grundlos mit „Feld nicht geleert“ ab
    [Fact]
    public async Task ASlowFieldIsJudgedAndClearedOnlyOnceItsContentIsStable()
    {
        var field = new ProbeField("41.1", string.Empty, lagReads: 3);
        ProbeField.Current.Value = field;
        var path = Path.Combine(Path.GetTempPath(), $"freihand-pr70-{Guid.NewGuid():N}.json");
        try
        {
            var options = Optionen.Lesen(["schreiben", "--wartezeit", "0", "--name", "test", "--out", path,
                "--messmodus", "--anker", field.RuntimeId, "--teile", "50", "--laenge", "250"]);
            Assert.Equal(0, await Schreiben.AusfuehrenAsync(options));

            Assert.Equal(1, field.ClearCalls);
            Assert.Equal(string.Empty, field.Raw);
            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"zeichengetreu\": true", json, StringComparison.Ordinal);
            Assert.Contains("\"geleert\": true", json, StringComparison.Ordinal);
        }
        finally
        {
            ProbeField.Current.Value = null;
            File.Delete(path);
        }
    }

    // Codex zu PR #70, eine Regel für jedes Löschen: Lädt das Feld unmittelbar vor dem Leeren etwas nach, wird nichts gelöscht.
    // Gelesen wird viermal: vor dem Schreiben, zweimal nach dem Schreiben (bis stabil), unmittelbar vor dem Leeren
    [Fact]
    public async Task ContentLoadedJustBeforeClearingIsNotDeleted()
    {
        var field = new ProbeField("41.1", string.Empty, changeOnRead: "Entwurf", changeAtRead: 4);
        ProbeField.Current.Value = field;
        var path = Path.Combine(Path.GetTempPath(), $"freihand-pr70-{Guid.NewGuid():N}.json");
        try
        {
            var options = Optionen.Lesen(["schreiben", "--wartezeit", "0", "--name", "test", "--out", path,
                "--messmodus", "--anker", field.RuntimeId, "--teile", "50", "--laenge", "250"]);
            Assert.Equal(1, await Schreiben.AusfuehrenAsync(options));

            Assert.Equal(0, field.ClearCalls);
            Assert.Equal("Entwurf", field.Text);
            Assert.Contains("Feld nicht geleert", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            ProbeField.Current.Value = null;
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\r\n\r\n")]
    [InlineData("\u200B")]
    public async Task AnyPreexistingContentStopsTheMeasurementBeforeWriting(string content)
    {
        await PreexistingContentStops(content);
    }

    [Fact]
    public async Task BlankLinesWithTheHintCheckboxAreRealTextAndNothingIsDeleted()
    {
        var field = new ProbeField("41.1", "\r\n\r\n");
        ProbeField.Current.Value = field;
        try
        {
            var (hint, result) = await Schreiben.HinweisAsync(field.RuntimeId);

            Assert.Null(hint);
            Assert.Equal("belegt", result);
            Assert.Equal("\r\n\r\n", field.Text);
        }
        finally
        {
            ProbeField.Current.Value = null;
        }
    }

    private static async Task PreexistingContentStops(string content)
    {
        var field = new ProbeField("41.1", content);
        ProbeField.Current.Value = field;
        var path = Path.Combine(Path.GetTempPath(), $"freihand-pr70-{Guid.NewGuid():N}.json");
        try
        {
            var options = Optionen.Lesen(["schreiben", "--wartezeit", "0", "--name", "test", "--out", path,
                "--messmodus", "--anker", field.RuntimeId, "--teile", "50", "--laenge", "250"]);
            Assert.Equal(1, await Schreiben.AusfuehrenAsync(options));

            Assert.Equal(content, field.Text);
            Assert.Equal(0, field.ClearCalls);
            Assert.Contains("\"abbruch\": \"Feld nicht leer\"", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            ProbeField.Current.Value = null;
            File.Delete(path);
        }
    }
}
