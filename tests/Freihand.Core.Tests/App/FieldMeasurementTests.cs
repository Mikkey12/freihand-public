using System.Text;
using Freihand.Adapters.Dictation;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;

namespace Freihand.Core.Tests.App;

/// <summary>
/// „Feld messen“ (Bedienregel vom 21.11.2026): Auswertung der Messung, Liste der gemessenen Felder und ihr
/// Vorrang in der Zieltabelle.
/// </summary>
public sealed class FieldMeasurementTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 21);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "freihand-feld-" + Guid.NewGuid().ToString("N"));

    public FieldMeasurementTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // Zusammenfassung des Messwerkzeugs („schlüssel = wert“); Freihand liest kein JSON
    private const string Facts =
        "prozess = ms-teams\nsteuerelement = Edit\nklasse = \noberflaeche = Chrome\nmehrzeilig = ja\nentscheid = Bindable\ngrund = unbekanntes Feld\n";

    private static string Pass(int size, bool? exact, bool readable = true, string? abort = null) =>
        $"durchgang = {size};{(exact is null ? "-" : exact.Value ? "ja" : "nein")};{(readable ? "ja" : "nein")};{abort ?? "-"}\n";

    private static string Json(params string[] entries) => string.Concat(entries);

    private static string Passes(int size, int times, bool exact = true) =>
        string.Concat(Enumerable.Repeat(Pass(size, exact), times));

    // Die grösste Grösse, die dreimal fehlerfrei ankam und nie scheiterte, gilt; die Reihenfolge der Durchgänge zählt nicht
    [Fact]
    public void TheLargestSizeThatCameThroughThreeTimesCounts()
    {
        var result = FieldMeasurement.Evaluate(
            Json(Facts, Pass(1, true), Pass(2, true), Pass(3, true), Pass(5, true), Pass(7, false), Passes(5, 2)), Today);

        Assert.Equal(5, result.Field!.Characters);
        Assert.False(result.ClearByHand);
        Assert.Equal("Dreimal fehlerfrei mit 5 Zeichen am Stück. Freihand schreibt hier ab sofort 5 Zeichen je Schritt.", result.Text);
        Assert.Equal(("ms-teams", "Edit", "", "Chrome", (bool?)true, false),
            (result.Field.Process, result.Field.ControlType, result.Field.ClassName, result.Field.FrameworkId, result.Field.MultiLine, result.Field.Manual));
        Assert.Equal(result.Field with { Characters = 1 }, result.Candidate);
    }

    // Echte Messung im Editor von Windows 11 (21.11.2026, nach der Korrektur des Leerens): jede Stufe wurde geleert, die
    // Suche ging von oben bis 1 hinunter, und 1 kam dreimal fehlerfrei an. Diese Zusammenfassung ist wörtlich übernommen.
    [Fact]
    public void TheRealNotepadMeasurementGoesDownToOneCharacter()
    {
        const string Notepad =
            "prozess = Notepad\nsteuerelement = Document\nklasse = RichEditD2DPT\noberflaeche = Win32\nmehrzeilig = ja\nlesbar = ja\n"
            + "entscheid = Bindable\ngrund = unbekanntes Feld (Standardregel)\n"
            + "durchgang = 50;nein;ja;-\ndurchgang = 20;nein;ja;-\ndurchgang = 10;nein;ja;-\ndurchgang = 5;nein;ja;-\n"
            + "durchgang = 2;nein;ja;-\ndurchgang = 1;ja;ja;-\ndurchgang = 1;ja;ja;-\ndurchgang = 1;ja;ja;-\n";

        var result = FieldMeasurement.Evaluate(Notepad, Today);

        Assert.Equal(("Notepad", "Document", "RichEditD2DPT", 1, false),
            (result.Field!.Process, result.Field.ControlType, result.Field.ClassName, result.Field.Characters, result.ClearByHand));
        Assert.StartsWith("Dreimal fehlerfrei mit 1 Zeichen am Stück.", result.Text, StringComparison.Ordinal);
    }

    // Echte Messung eines Textfelds in Edge (lokale Seite, 21.11.2026, nach der Bindung an die RuntimeId vom Countdown): die
    // RuntimeId blieb nach dem Leeren gleich, 50 kam dreimal fehlerfrei an. Mehrzeiligkeit meldet Edge nicht (gilt nur für so gemeldete Felder)
    [Fact]
    public void TheRealEdgeMeasurementKeepsFiftyCharacters()
    {
        const string Edge =
            "prozess = msedge\nsteuerelement = Edit\nklasse = \noberflaeche = Chrome\nmehrzeilig = -\nlesbar = ja\n"
            + "entscheid = Bindable\ngrund = unbekanntes Feld (Standardregel)\n"
            + "durchgang = 50;ja;ja;-\ndurchgang = 50;ja;ja;-\ndurchgang = 50;ja;ja;-\n";

        var result = FieldMeasurement.Evaluate(Edge, Today);

        Assert.Equal(("msedge", "Edit", "", "Chrome", (bool?)null, 50, false),
            (result.Field!.Process, result.Field.ControlType, result.Field.ClassName, result.Field.FrameworkId, result.Field.MultiLine,
             result.Field.Characters, result.ClearByHand));
        Assert.Equal("Dreimal fehlerfrei mit 50 Zeichen am Stück. Freihand schreibt hier ab sofort 50 Zeichen je Schritt.", result.Text);
    }

    // Echte Messungen vom 21.11.2026 nach dem Nachweis des Hinweistexts: Editor mit wechselhaftem 2er-Durchgang,
    // Edge-Feld, dessen grauen Hinweistext Windows als Inhalt meldet (wie Teams), Edge-Feld mit markiertem echtem Text und
    // Edge-Feld mit Hinweis nach Webart (Windows meldet es leer). Zusammenfassungen wörtlich, ohne grund/entscheid-Zeilen
    [Theory]
    [InlineData("prozess = Notepad\nsteuerelement = Document\nklasse = RichEditD2DPT\noberflaeche = Win32\nmehrzeilig = ja\nlesbar = ja\n"
        + "durchgang = 50;nein;ja;-\ndurchgang = 20;nein;ja;-\ndurchgang = 10;nein;ja;-\ndurchgang = 5;nein;ja;-\ndurchgang = 2;ja;ja;-\n"
        + "durchgang = 3;nein;ja;-\ndurchgang = 2;ja;ja;-\ndurchgang = 2;nein;ja;-\ndurchgang = 1;ja;ja;-\ndurchgang = 1;ja;ja;-\ndurchgang = 1;ja;ja;-\n", 1, false)]
    [InlineData("hinweistext = ja\nprozess = msedge\nsteuerelement = Edit\nklasse = \noberflaeche = Chrome\nmehrzeilig = -\nlesbar = ja\n"
        + "durchgang = 50;ja;ja;-\ndurchgang = 50;ja;ja;-\ndurchgang = 50;ja;ja;-\n", 50, false)]
    [InlineData("hinweistext = echter_text\nprozess = msedge\nsteuerelement = Edit\nklasse = \noberflaeche = Chrome\nmehrzeilig = -\nlesbar = ja\n", 0, true)]
    [InlineData("hinweistext = leer\nprozess = msedge\nsteuerelement = Edit\nklasse = \noberflaeche = Chrome\nmehrzeilig = -\nlesbar = ja\n"
        + "durchgang = 50;ja;ja;-\ndurchgang = 50;ja;ja;-\ndurchgang = 50;ja;ja;-\n", 50, false)]

    // Echte Messung vom 21.11.2026 auf af80478 (Codex zu PR #70): Edge-Feld mit zwei Leerzeilen ohne Häkchen (nichts geschrieben)
    [InlineData("prozess = msedge\nsteuerelement = Edit\nklasse = \noberflaeche = Chrome\nmehrzeilig = -\nlesbar = ja\n"
        + "durchgang = 50;-;ja;Feld nicht leer\n", 0, false)]
    public void TheRealMeasurementsWithHintTextsGiveTheExpectedValues(string summary, int characters, bool clearByHand)
    {
        var result = FieldMeasurement.Evaluate(summary, Today);

        Assert.Equal(characters, result.Field?.Characters ?? 0);
        Assert.Equal(clearByHand, result.ClearByHand);
        Assert.NotNull(result.Candidate);
    }

    // Scheitert eine Wiederholung, gilt die Stufe darunter, sofern auch sie dreimal durchging; höchstens 50
    [Fact]
    public void AFailedRepetitionStepsDownAndTheCapIsFifty()
    {
        Assert.Equal(7, FieldMeasurement.Evaluate(
            Json(Facts, Pass(1, true), Pass(2, true), Pass(3, true), Pass(5, true), Pass(7, true), Pass(10, true), Pass(15, false),
                Pass(10, true), Pass(10, false), Passes(7, 2)), Today).Field!.Characters);
        Assert.Equal(FieldMeasurement.MaxCharacters,
            FieldMeasurement.Evaluate(Json(Facts, Pass(30, true), Passes(50, 3)), Today).Field!.Characters);
    }

    // Codex zu PR #70: Ohne dreimal bestätigte Grösse ist nichts gemessen; es bleibt beim bisherigen Wert, Wert von Hand möglich
    [Fact]
    public void WithoutAThreeTimesExactSizeNothingIsStored()
    {
        var result = FieldMeasurement.Evaluate(Json(Facts, Pass(20, true), Pass(20, false)), Today);

        Assert.Null(result.Field);
        Assert.NotNull(result.Candidate);
        Assert.StartsWith("Keine Stückgrösse kam dreimal fehlerfrei an. Nichts gespeichert, es bleibt beim bisherigen Wert.", result.Text, StringComparison.Ordinal);
    }

    // Handtest vom 21.11.2026 (Editor mit Formatierung): schon Zeichen für Zeichen verändert → das Programm ändert Text selbst;
    // liess sich das Feld danach nicht leeren, steht der Abbruch im Vordergrund
    [Fact]
    public void AFailureAtOneCharacterSaysTheProgramChangesTheText()
    {
        var changed = FieldMeasurement.Evaluate(Json(Facts, Pass(50, false), Pass(20, false), Pass(10, false), Pass(5, false), Pass(2, false), Pass(1, false)), Today);
        Assert.Null(changed.Field);
        Assert.StartsWith("Schon Zeichen für Zeichen kam der Text verändert an", changed.Text, StringComparison.Ordinal);
        Assert.False(changed.ClearByHand);

        var aborted = FieldMeasurement.Evaluate(Json(Facts, Pass(1, false, abort: "Feld nicht geleert, bitte von Hand leeren")), Today);
        Assert.Null(aborted.Field);
        Assert.StartsWith("Die Messung musste abbrechen: Nach dem Durchgang mit 1 Zeichen", aborted.Text, StringComparison.Ordinal);
        Assert.True(aborted.ClearByHand);
    }

    [Fact]
    public void NothingIsStoredWhenTheFieldCannotBeCheckedOrWasNotEmpty()
    {
        var unreadable = FieldMeasurement.Evaluate(Json(Facts, Pass(20, null, readable: false)), Today);
        Assert.Null(unreadable.Field);
        Assert.True(unreadable.ClearByHand);
        Assert.Contains("von Hand löschen", unreadable.Text, StringComparison.Ordinal);
        Assert.NotNull(unreadable.Candidate);

        var notEmpty = FieldMeasurement.Evaluate(Json(Facts, Pass(1, null, abort: "Feld nicht leer")), Today);
        Assert.Null(notEmpty.Field);
        Assert.StartsWith("Das Feld war nicht leer; Freihand hat nichts geschrieben.", notEmpty.Text, StringComparison.Ordinal);
        Assert.EndsWith("Bitte in einem eindeutig leeren Testfeld messen oder „Nur Feld erkennen“ und den Wert von Hand festlegen.", notEmpty.Text, StringComparison.Ordinal);
        Assert.Contains("Auch Leerzeilen und Leerzeichen zählen als Inhalt.", notEmpty.Text, StringComparison.Ordinal);
        Assert.False(notEmpty.ClearByHand);

        // Eigene Vorprüfung: nicht mehr leer erst vor einem späteren Durchgang heisst, Prüftext steht vermutlich noch darin
        var laterNotEmpty = FieldMeasurement.Evaluate(Json(Facts, Pass(50, false), Pass(20, null, abort: "Feld nicht leer")), Today);
        Assert.Null(laterNotEmpty.Field);
        Assert.True(laterNotEmpty.ClearByHand);
        Assert.StartsWith("Vor dem Durchgang mit 20 Zeichen war das Feld nicht mehr leer;", laterNotEmpty.Text, StringComparison.Ordinal);

        // Bedienregel, 21.11.2026: Hinweistext mit einem Zeichen geprüft; nur „ja“ oder „leer“ führt zur Messung
        Assert.Equal(20, FieldMeasurement.Evaluate(Json("hinweistext = ja\n", Facts, Passes(20, 3)), Today).Field!.Characters);
        Assert.Equal(20, FieldMeasurement.Evaluate(Json("hinweistext = leer\n", Facts, Passes(20, 3)), Today).Field!.Characters);
        foreach (var (hint, clearByHand, start) in new[]
        {
            // Eigene Vorprüfung: Verschwand der Inhalt und kam er nach dem Zurücknehmen nicht wieder, war es kein Hinweistext
            ("ersetzt", true, "Nach dem Entfernen des Prüfzeichens zeigte das Feld seinen früheren Inhalt nicht wieder."),
            // Codex zu PR #70: In echtem Text löscht die Probe nie; das Prüfzeichen bleibt, und Freihand nennt es
            ("echter_text", true, "Im Feld steht echter Text, kein grauer Hinweistext: Er blieb beim ersten getippten Zeichen stehen. Freihand hat zum Prüfen „G“ eingefügt und löscht in echtem Text nie selbst"),
            ("nicht_angekommen", true, "Das Prüfzeichen „G“ kam innert drei Sekunden nicht an."),
            ("anderes_feld", true, "Während der Prüfung des Hinweistexts war ein anderes Feld im Fokus."),
            ("nicht_bindbar", false, "In dieses Feld kann Freihand nicht schreiben."),
            ("vorher_abgebrochen", false, "Die Prüfung des Hinweistexts brach ab, bevor etwas getippt wurde"),
            ("unklar", true, "Das Feld reagierte auf das Prüfzeichen „G“ unerwartet."),
            ("etwas_neues", true, "Das Feld reagierte auf das Prüfzeichen „G“ unerwartet."),
        })
        {
            var result = FieldMeasurement.Evaluate(Json($"hinweistext = {hint}\npruefzeichen = G\n", Facts), Today);
            Assert.Null(result.Field);
            Assert.Equal(clearByHand, result.ClearByHand);
            Assert.StartsWith(start, result.Text, StringComparison.Ordinal);
        }

        // Ohne Prüfzeichen in der Zusammenfassung heisst es allgemein „ein einzelnes Zeichen“
        Assert.Contains("ein einzelnes Zeichen", FieldMeasurement.Evaluate(Json("hinweistext = unklar\n", Facts), Today).Text, StringComparison.Ordinal);

        // Eigene Vorprüfung: Änderte sich der Hinweistext nach seiner Prüfung, nicht „Häkchen setzen“ (es ist gesetzt)
        var changedHint = FieldMeasurement.Evaluate(Json("hinweistext = ja\n", Facts, Pass(50, null, abort: "Feld nicht leer")), Today);
        Assert.Null(changedHint.Field);
        Assert.False(changedHint.ClearByHand);
        Assert.StartsWith("Der Inhalt des Felds änderte sich nach der Prüfung des Hinweistexts;", changedHint.Text, StringComparison.Ordinal);

        // Eigene Vorprüfung zu PR #70: „leer“ gilt nach derselben Regel wie beim Schreiben; ist das Feld danach nicht mehr leer,
        // hat sich sein Inhalt nach der Prüfung geändert (das Häkchen ist gesetzt)
        var changedEmpty = FieldMeasurement.Evaluate(Json("hinweistext = leer\n", Facts, Pass(50, null, abort: "Feld nicht leer")), Today);
        Assert.Null(changedEmpty.Field);
        Assert.False(changedEmpty.ClearByHand);
        Assert.StartsWith("Der Inhalt des Felds änderte sich nach der Prüfung des Hinweistexts;", changedEmpty.Text, StringComparison.Ordinal);

        // Eigene Vorprüfung: Fokuswechsel beim Lesen der Merkmale hat einen eigenen Grund, nicht „kein Feld“
        var moved = FieldMeasurement.Evaluate("fehler = Fokuswechsel\n", Today);
        Assert.Null(moved.Field);
        Assert.False(moved.ClearByHand);
        Assert.StartsWith("Während Freihand das Feld las, wechselte der Fokus.", moved.Text, StringComparison.Ordinal);

        // Codex zu PR #70: Ein Abbruch beim Leeren macht die ganze Messung ungültig, auch nach bestätigten Grössen
        var notCleared = FieldMeasurement.Evaluate(Json(Facts, Passes(1, 3), Pass(2, true, abort: "Feld nicht geleert, bitte von Hand leeren")), Today);
        Assert.True(notCleared.ClearByHand);
        Assert.Null(notCleared.Field);
        Assert.StartsWith("Die Messung musste abbrechen: Nach dem Durchgang mit 2 Zeichen", notCleared.Text, StringComparison.Ordinal);

        // Handtest vom 21.11.2026 (Editor): schon der erste Durchgang mit 50 liess sich nicht leeren → Abbruch, nichts speichern
        var aborted = FieldMeasurement.Evaluate(Json(Facts, Pass(50, false, abort: "Feld nicht geleert, bitte von Hand leeren")), Today);
        Assert.Null(aborted.Field);
        Assert.True(aborted.ClearByHand);
        Assert.NotNull(aborted.Candidate);
        Assert.StartsWith("Die Messung musste abbrechen: Nach dem Durchgang mit 50 Zeichen", aborted.Text, StringComparison.Ordinal);
    }

    // Bedienregel vom 21.11.2026: nicht lesbare Felder (WhatsApp) nur erkennen, Wert von Hand festlegen
    [Fact]
    public void IdentifyingOnlyOffersTheFieldForAManualValue()
    {
        var identified = FieldMeasurement.Evaluate("modus = erkennen\n" + Facts.Replace("mehrzeilig = ja\n", "mehrzeilig = ja\nlesbar = nein\n", StringComparison.Ordinal), Today);

        Assert.Null(identified.Field);
        Assert.False(identified.ClearByHand);
        Assert.StartsWith("Feld erkannt.", identified.Text, StringComparison.Ordinal);
        Assert.Equal(("ms-teams", 1), (identified.Candidate!.Process, identified.Candidate.Characters));
        Assert.Null(FieldMeasurement.Evaluate("modus = erkennen\n" + Facts.Replace("ms-teams", "Freihand", StringComparison.Ordinal), Today).Candidate);

        var unreadable = FieldMeasurement.Evaluate(Facts.Replace("mehrzeilig = ja\n", "mehrzeilig = ja\nlesbar = nein\n", StringComparison.Ordinal), Today);
        Assert.NotNull(unreadable.Candidate);
        Assert.Contains("Wert von Hand", unreadable.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AManualValueIsStoredAsSuchAndOldSevenColumnLinesStillLoad()
    {
        var store = new MeasuredFieldStore(Path.Combine(_folder, MeasuredFieldStore.FileName));
        var whatsapp = new MeasuredField("WhatsApp", "Edit", "", "XAML", true, 20, Today, Manual: true);

        store.Save(whatsapp);
        File.AppendAllText(store.Path, "WINWORD | Document | _WwG | Win32 | - | 20 | 2026-11-21\n");

        Assert.Equal([whatsapp, new MeasuredField("WINWORD", "Document", "_WwG", "Win32", null, 20, Today)], store.Load());
        Assert.Contains("WhatsApp | Edit | - | XAML | ja | 20 | 2026-11-21 | von Hand", File.ReadAllText(store.Path), StringComparison.Ordinal);
        Assert.Equal("WhatsApp – von Hand festgelegt", whatsapp.ToRule().Display);

        // Eine spätere Messung ersetzt den Wert von Hand
        store.Save(whatsapp with { Manual = false, Characters = 10 });
        Assert.Equal(whatsapp with { Manual = false, Characters = 10 }, store.Load()[0]);
    }

    [Fact]
    public void NoFieldOwnWindowsAndRefusedFieldsGiveNoEntry()
    {
        Assert.StartsWith("Kein Feld gefunden", FieldMeasurement.Evaluate(string.Empty, Today).Text, StringComparison.Ordinal);
        Assert.StartsWith("Kein Feld gefunden", FieldMeasurement.Evaluate("kein json", Today).Text, StringComparison.Ordinal);
        Assert.Null(FieldMeasurement.Evaluate(Json(Facts.Replace("ms-teams", "Freihand", StringComparison.Ordinal), Pass(20, true)), Today).Field);
        var password = FieldMeasurement.Evaluate(Json(Facts.Replace("entscheid = Bindable", "entscheid = NotBindable", StringComparison.Ordinal)
            .Replace("grund = unbekanntes Feld", "grund = PasswordField", StringComparison.Ordinal)), Today);
        Assert.Null(password.Field);
        Assert.Contains("Passwortfeld", password.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStoreReplacesTheSameFieldAndKeepsOtherLineKinds()
    {
        var store = new MeasuredFieldStore(Path.Combine(_folder, MeasuredFieldStore.FileName));
        var teams = new MeasuredField("ms-teams", "Edit", "", "Chrome", true, 50, Today);

        store.Save(teams);
        store.Save(teams with { Characters = 25 });
        store.Save(teams with { MultiLine = false, Characters = 10 });
        store.Save(new MeasuredField("WINWORD", "Document", "_WwG", "Win32", null, 20, Today));
        File.AppendAllText(store.Path, "   \n# eigene Notiz\n");

        Assert.Equal(
            [teams with { Characters = 25 }, teams with { MultiLine = false, Characters = 10 }, new MeasuredField("WINWORD", "Document", "_WwG", "Win32", null, 20, Today)],
            store.Load());
        Assert.StartsWith(MeasuredFieldStore.Header, File.ReadAllText(store.Path), StringComparison.Ordinal);
        Assert.Contains("ms-teams | Edit | - | Chrome | ja | 25 | 2026-11-21 | gemessen", File.ReadAllText(store.Path), StringComparison.Ordinal);
    }

    // Codex zu PR #70: Eine ungültige Datenzeile macht die Liste ungültig (mit Zeilennummer); auch Speichern unterbleibt
    [Theory]
    [InlineData("kaputt | Zeile")]
    [InlineData("X | Edit | - | - | - | 99 | 2026-11-21")]
    [InlineData("X | Edit | - | - | - | 50 | 21.11.2026")]
    [InlineData("X | Edit | - | - | Ja | 5 | 2026-11-21")]
    [InlineData("X | Edit | - | - | jaa | 5 | 2026-11-21 | gemessen")]
    [InlineData("X | Edit | - | - | ja | 5 | 2026-11-21 | vonhand")]
    public void ABrokenLineMakesTheListInvalidAndNothingIsSaved(string broken)
    {
        var store = new MeasuredFieldStore(Path.Combine(_folder, MeasuredFieldStore.FileName));
        store.Save(new MeasuredField("ms-teams", "Edit", "", "Chrome", true, 50, Today));
        File.AppendAllText(store.Path, broken + "\n");
        var before = File.ReadAllText(store.Path);

        Assert.Equal(4, Assert.Throws<MeasuredListInvalidException>(() => store.Load()).LineNumber);
        Assert.Throws<MeasuredListInvalidException>(() => store.Save(new MeasuredField("WINWORD", "Document", "_WwG", "Win32", null, 20, Today)));
        Assert.Equal(before, File.ReadAllText(store.Path));
    }

    // Eigene Vorprüfung zu Codex-Befund 5: nie still; beim Start gelten nur die festen Werte, danach die zuletzt gelesenen
    [Fact]
    public void AnInvalidListIsReportedWithItsLineAndWhatStillApplies()
    {
        var invalid = new MeasuredListInvalidException(4);
        Assert.Equal(
            "In der Liste der gemessenen Felder ist Zeile 4 ungültig. Bitte über „Feld messen …“ und „Gemessene Felder öffnen“ korrigieren oder die Zeile löschen; bis dahin gelten nur die festen Werte.",
            MeasuredFieldStore.Problem(invalid, atStart: true));
        Assert.EndsWith("bis dahin gelten die zuletzt gelesenen Werte.", MeasuredFieldStore.Problem(invalid, atStart: false), StringComparison.Ordinal);
        Assert.StartsWith("Die Liste der gemessenen Felder lässt sich nicht lesen oder schreiben;",
            MeasuredFieldStore.Problem(new IOException("gesperrt"), atStart: false), StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(invalid);
    }

    // Eigene Vorprüfung: nicht lesbar (nichts geschrieben), Fokuswechsel, Fehler des Werkzeugs, unwiederkennbare Merkmale
    [Fact]
    public void UnreadableFocusChangeFailureAndBadIdentitiesStoreNothing()
    {
        var unreadable = FieldMeasurement.Evaluate(Facts.Replace("mehrzeilig = ja\n", "mehrzeilig = ja\nlesbar = nein\n", StringComparison.Ordinal), Today);
        Assert.Null(unreadable.Field);
        Assert.False(unreadable.ClearByHand);
        Assert.Contains("nichts geschrieben", unreadable.Text, StringComparison.Ordinal);

        var moved = FieldMeasurement.Evaluate(Json(Facts, Pass(20, true), "abweichung = ja\n"), Today);
        Assert.Null(moved.Field);
        Assert.True(moved.ClearByHand);

        var failed = FieldMeasurement.Evaluate("fehler = InvalidDataException\n", Today);
        Assert.Null(failed.Field);
        Assert.StartsWith("Messung gescheitert (InvalidDataException)", failed.Text, StringComparison.Ordinal);

        foreach (var klasse in new[] { " Rand", "Zeile\tTab", "-", "a|b" })
        {
            Assert.Null(FieldMeasurement.Evaluate(Json(Facts.Replace("klasse = \n", $"klasse = {klasse}\n", StringComparison.Ordinal), Pass(20, true)), Today).Field);
        }
    }

    // Eigene Vorprüfung: Eine Messung ändert nur die Geschwindigkeit; Word behält Feldart und Umbrüche
    [Fact]
    public void MeasuringAKnownFieldKeepsItsKindAndLineBreaks()
    {
        var table = new TargetRuleTable(
        [
            new("WINWORD", "Document", "_WwG", "Win32", null, FieldKinds.DocumentBody, "Word – Dokumenttext", LineBreakMode.UnicodeReturn, 20, IdentityMode.RuntimeId),
        ]);
        table.SetMeasured([new MeasuredField("WINWORD", "Document", "_WwG", "Win32", true, 50, Today).ToRule()]);

        var rule = Rule(table, new TargetFacts("WINWORD", "Document", "_WwG", "Win32", false, false, true, true, false, false));
        Assert.Equal((50, FieldKinds.DocumentBody, LineBreakMode.UnicodeReturn, "Word – Dokumenttext"),
            (rule.MaxPartLength, rule.FieldKind, rule.LineBreak, rule.Display));
    }

    // Codex zu PR #70: Ein gemessener Wert ändert nur die Teilgrösse der festen Zeile, die auf das aktuelle Feld passt, und gilt
    // nur für Felder, die sich genau so melden wie bei der Messung, auch mit unbekannter Ein- oder Mehrzeiligkeit
    [Fact]
    public void AMeasuredValueOnlyChangesThePartSizeOfTheRuleForTheCurrentField()
    {
        var table = new TargetRuleTable(
        [
            new("msedge", "Edit", null, "Chrome", true, FieldKinds.MultiLine, "Edge – mehrzeilig", LineBreakMode.Refuse, 20, IdentityMode.RuntimeId),
            new("msedge", "Edit", null, "Chrome", false, FieldKinds.SingleLine, "Edge – einzeilig", LineBreakMode.Refuse, 20, IdentityMode.RuntimeId),
            new("msedge", "Edit", null, "Chrome", null, FieldKinds.Unknown, "Edge – Textfeld", LineBreakMode.Refuse, 10, IdentityMode.RuntimeId),
        ]);
        var unknown = new TargetFacts("msedge", "Edit", "", "Chrome", false, false, true, null, false, false);
        var single = new TargetFacts("msedge", "Edit", "", "Chrome", false, false, true, false, false, false);
        var multi = new TargetFacts("msedge", "Edit", "", "Chrome", false, false, true, true, false, false);

        // Gemessen mit unbekannter Mehrzeiligkeit (echte Edge-Messung): gilt nur für so gemeldete Felder
        table.SetMeasured([new MeasuredField("msedge", "Edit", "", "Chrome", null, 30, Today).ToRule()]);
        Assert.Equal((30, FieldKinds.Unknown, "Edge – Textfeld"), (Rule(table, unknown).MaxPartLength, Rule(table, unknown).FieldKind, Rule(table, unknown).Display));
        Assert.Equal(20, Rule(table, single).MaxPartLength);
        Assert.Equal(20, Rule(table, multi).MaxPartLength);

        // Gemessen als einzeilig: nur einzeilige, mit Feldart und Anzeige der einzeiligen festen Zeile
        table.SetMeasured(
        [
            new MeasuredField("msedge", "Edit", "", "Chrome", null, 30, Today).ToRule(),
            new MeasuredField("msedge", "Edit", "", "Chrome", false, 5, Today).ToRule(),
        ]);
        Assert.Equal((5, FieldKinds.SingleLine, "Edge – einzeilig"), (Rule(table, single).MaxPartLength, Rule(table, single).FieldKind, Rule(table, single).Display));
        Assert.Equal(20, Rule(table, multi).MaxPartLength);
        Assert.Equal(30, Rule(table, unknown).MaxPartLength);
    }

    // Eigene Vorprüfung: Ist die Liste gesperrt, meldet Load den Fehler, statt eine leere Liste zu liefern
    [Fact]
    public void ALockedListIsAnErrorNotAnEmptyList()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new MeasuredFieldStore(Path.Combine(_folder, MeasuredFieldStore.FileName));
        store.Save(new MeasuredField("ms-teams", "Edit", "", "Chrome", true, 50, Today));
        using (new FileStream(store.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => store.Load());
        }

        Assert.Single(store.Load());
    }

    [Fact]
    public void AMissingFileGetsItsHeaderAndAnExistingOneStays()
    {
        var store = new MeasuredFieldStore(Path.Combine(_folder, "neu", MeasuredFieldStore.FileName));
        Assert.Empty(store.Load());

        store.EnsureExists();
        Assert.Equal(MeasuredFieldStore.Header, File.ReadAllText(store.Path));

        File.WriteAllText(store.Path, "# von Hand\n", new UTF8Encoding(false));
        store.EnsureExists();
        Assert.Equal("# von Hand\n", File.ReadAllText(store.Path));
    }

    // Gemessen gilt vor fest, aber nur bei genau denselben Merkmalen: die Adressleiste (eigene Klasse) bleibt bei ihrer Zeile
    [Fact]
    public void AMeasuredFieldWinsOnlyForExactlyTheSameIdentity()
    {
        var table = new TargetRuleTable(
        [
            new("msedge", "Edit", "OmniboxViewViews", "Chrome", null, FieldKinds.SingleLine, "Edge – Adressleiste", LineBreakMode.Refuse, 1, IdentityMode.RuntimeId),
            new("msedge", "Edit", null, "Chrome", null, FieldKinds.MultiLine, "Edge – Textfeld", LineBreakMode.Refuse, 20, IdentityMode.RuntimeId),
        ]);
        table.SetMeasured([new MeasuredField("msedge", "Edit", "", "Chrome", true, 50, Today).ToRule()]);

        Assert.Equal(50, Rule(table, FieldFacts("msedge", "")).MaxPartLength);
        Assert.Equal(1, Rule(table, FieldFacts("msedge", "OmniboxViewViews")).MaxPartLength);
        Assert.Equal(20, Rule(table, FieldFacts("msedge", "IrgendeineKlasse")).MaxPartLength);
        Assert.Equal(LineBreakMode.Refuse, Rule(table, FieldFacts("msedge", "")).LineBreak);

        table.SetMeasured([]);
        Assert.Equal(20, Rule(table, FieldFacts("msedge", "")).MaxPartLength);
    }

    private static TargetRule Rule(TargetRuleTable table, TargetFacts facts) =>
        Assert.IsType<Bindable>(table.Decide(facts, ProfileStrictness.Standard)).Rule;

    private static TargetFacts FieldFacts(string process, string className) =>
        new(process, "Edit", className, "Chrome", false, false, true, true, false, false);
}
