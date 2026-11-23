using Freihand.Adapters.Dictation;
using Freihand.Core.Platform;
using Freihand.Core.Profiles;

namespace Freihand.Core.Tests.App;

public sealed class Pr70FollowupCounterTests
{
    private static readonly DateOnly Today = new(2026, 11, 21);

    [Fact]
    public void TheThirdExactPassDoesNotCountWhenClearingFailed()
    {
        const string summary = """
            prozess = Notepad
            steuerelement = Document
            lesbar = ja
            entscheid = Bindable
            durchgang = 50;ja;ja;-
            durchgang = 50;ja;ja;-
            durchgang = 50;ja;ja;Feld nicht geleert, bitte von Hand leeren
            """;

        Assert.Null(FieldMeasurement.Evaluate(summary, Today).Field);
    }

    [Fact]
    public void AnUnknownMultilineStatusDoesNotProveAKnownSingleLineTarget()
    {
        var table = new TargetRuleTable(
        [
            new("msedge", "Edit", null, "Chrome", false, FieldKinds.SingleLine,
                "Edge – einzeilig", LineBreakMode.Refuse, 20, IdentityMode.RuntimeId)
        ]);
        table.SetMeasured([new MeasuredField("msedge", "Edit", "", "Chrome", null, 50, Today).ToRule()]);
        var singleLine = new TargetFacts("msedge", "Edit", "", "Chrome", false, false, true, false, false, false);

        Assert.Equal(20, Assert.IsType<Bindable>(table.Decide(singleLine, ProfileStrictness.Standard)).Rule.MaxPartLength);
    }

    [Fact]
    public void ACommentAndAValidRowSeparatedByCrStillLoad()
    {
        var folder = Directory.CreateTempSubdirectory("freihand-pr70-cr-");
        var path = Path.Combine(folder.FullName, "ziele-gemessen.txt");
        try
        {
            var field = new MeasuredField("editor", "Edit", "", "Win32", false, 20, Today);
            File.WriteAllText(path, "# Kommentar\r" + MeasuredFieldStore.Line(field) + "\r");

            Assert.Equal(field, Assert.Single(new MeasuredFieldStore(path).Load()));
        }
        finally
        {
            File.Delete(path);
            folder.Delete();
        }
    }
}