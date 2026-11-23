using Freihand.Adapters.Dictation;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;

namespace Freihand.Core.Tests.App;

public sealed class Pr70FieldCounterTests
{
    private static readonly DateOnly Today = new(2026, 11, 21);

    [Theory]
    [InlineData("-")]
    [InlineData("Feld nicht geleert, bitte von Hand leeren")]
    public void WithoutAConfirmedSizeTheMeasurementCannotReplaceTheOldValue(string abort)
    {
        var summary = $"""
            prozess = Notepad
            steuerelement = Document
            klasse = RichEditD2DPT
            oberflaeche = Win32
            lesbar = ja
            entscheid = Bindable
            durchgang = 50;nein;ja;-
            durchgang = 20;nein;ja;-
            durchgang = 10;nein;ja;-
            durchgang = 5;nein;ja;-
            durchgang = 2;nein;ja;-
            durchgang = 1;nein;ja;{abort}
            """;

        Assert.Null(FieldMeasurement.Evaluate(summary, Today).Field);
    }

    [Fact]
    public void AMeasuredMultilineFieldDoesNotOverrideASingleLineField()
    {
        var table = new TargetRuleTable(
        [
            new("msedge", "Edit", null, "Chrome", null, FieldKinds.MultiLine,
                "Edge – Textfeld", LineBreakMode.Refuse, 20, IdentityMode.RuntimeId),
        ]);
        table.SetMeasured([new MeasuredField("msedge", "Edit", "", "Chrome", true, 50, Today).ToRule()]);
        var singleLine = new TargetFacts("msedge", "Edit", "", "Chrome", false, false, true, false, false, false);

        Assert.Equal(20, Assert.IsType<Bindable>(table.Decide(singleLine, ProfileStrictness.Standard)).Rule.MaxPartLength);
    }

    [Fact]
    public void AnInvalidEditedValueKeepsTheLastLoadedRule()
    {
        var folder = Directory.CreateTempSubdirectory("freihand-pr70-");
        var path = Path.Combine(folder.FullName, "ziele-gemessen.txt");
        try
        {
            var store = new MeasuredFieldStore(path);
            store.Save(new MeasuredField("msedge", "Edit", "", "Chrome", true, 50, Today));
            var table = new TargetRuleTable([]);
            table.SetMeasured(store.Load().Select(field => field.ToRule()));
            File.WriteAllText(path, File.ReadAllText(path).Replace(" | 50 | ", " | 500 | "));

            try { table.SetMeasured(store.Load().Select(field => field.ToRule())); }
            catch (IOException) { } // Shell behält bei einem Lesefehler die letzte Tabelle.

            var facts = new TargetFacts("msedge", "Edit", "", "Chrome", false, false, true, true, false, false);
            Assert.Equal(50, Assert.IsType<Bindable>(table.Decide(facts, ProfileStrictness.Standard)).Rule.MaxPartLength);
        }
        finally
        {
            File.Delete(path);
            folder.Delete();
        }
    }
}