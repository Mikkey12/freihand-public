using System.Globalization;
using Freihand.Zielprobe;

namespace Freihand.Core.Tests.App;

public sealed class Pr73StableReadCounterTests
{
    [Fact]
    public async Task ReturningToTheMeasuredFieldDoesNotAuthorizeClearingItUsingAnotherFieldsContent()
    {
        const string draft = "Entwurf";
        var measured = new ProbeField("41.1", "", hint: null) { ContentAfterInsert = draft };
        var other = new ProbeField("41.2", Schreiben.Pruefzeichen.ToString(),
            focusTargetOnRead: measured, moveFocusAtRead: 2);
        measured.FocusAfterInsert = other;
        ProbeField.Current.Value = measured;
        var path = Path.Combine(Path.GetTempPath(), $"freihand-pr73-{Guid.NewGuid():N}.json");
        try
        {
            var options = Optionen.Lesen(["schreiben", "--wartezeit", "0", "--name", "test", "--out", path,
                "--messmodus", "--anker", measured.RuntimeId, "--teile", "50", "--laenge", "250"]);
            await Schreiben.AusfuehrenAsync(options);

            Assert.Equal(draft, measured.Raw);
            Assert.Equal(0, measured.ClearCalls);
        }
        finally
        {
            ProbeField.Current.Value = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void TheDeadlineDoesNotTurnTheLastChangingValueIntoAStableValue()
    {
        var field = new ProbeField("41.1", "")
        {
            ReadOverride = read => read.ToString(CultureInfo.InvariantCulture),
        };

        Assert.Null(Schreiben.LesenStabil(field));
    }

    [Fact]
    public void AnUnreadableSampleBreaksTheSequenceOfEqualReads()
    {
        string?[] readings = ["a", null, "a", "ab", "ab"];
        var field = new ProbeField("41.1", "")
        {
            ReadOverride = read => readings[Math.Min(read, readings.Length) - 1],
        };

        Assert.Equal("ab", Schreiben.LesenStabil(field));
    }
}