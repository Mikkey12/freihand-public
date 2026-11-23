using System.Text;
using System.Text.Json;
using Freihand.Core.Recognition;
using Freihand.Core.Recovery;

namespace Freihand.Core.Tests.Recovery;

/// <summary>Sichern von festem Text (ER-06, Grundentscheid 2).</summary>
public sealed class SavingTests : RecoveryTestBase
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(500)]
    [InlineData(999)]
    public void FixedTextIsSavedWithinOneSecond(int offsetMilliseconds)
    {
        var store = Open();
        var draft = store.StartDraft();
        Time.Advance(TimeSpan.FromMilliseconds(offsetMilliseconds));

        draft.Append(Fixed("Guten Tag"));
        Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("Guten Tag", Assert.Single(CrashAndRestart().Scan().Drafts).Text);
    }

    [Fact]
    public void StoreFlushSavesOpenDraftsAndKeepsRecording()
    {
        // Ende ohne Schliesspfad (Codex-Prüfung PR #58): sofort sichern, die Ablage nimmt danach weiter auf
        var store = Open();
        var draft = store.StartDraft();
        draft.Append(Fixed("Guten Tag"));

        store.Flush();
        using (var other = new RecoveryStore(Files, Protector, Time))
        {
            Assert.Equal("Guten Tag", Assert.Single(other.Scan().Drafts).Text);
        }

        draft.Append(Fixed("und adieu"));
        store.Flush();
        Assert.Contains("adieu", Assert.Single(CrashAndRestart().Scan().Drafts).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsWrittenWithoutFixedText()
    {
        var draft = Open().StartDraft();

        draft.Apply(new RecognitionUpdate([], [Provisional("Guten")]));
        Time.Advance(TimeSpan.FromSeconds(5));
        draft.Flush();

        Assert.Empty(Files.Files);
    }

    [Fact]
    public void ProvisionalTextNeverReachesStorage()
    {
        var draft = Open().StartDraft();

        draft.Apply(new RecognitionUpdate([Fixed("fest")], [Provisional("MARKE-VORLAEUFIG")]));
        Time.Advance(TimeSpan.FromSeconds(1));
        draft.Flush();

        Assert.NotEmpty(Protector.Plaintexts);
        Assert.DoesNotContain(Protector.PlaintextStrings, plaintext => plaintext.Contains("MARKE-VORLAEUFIG", StringComparison.Ordinal));
        Assert.Equal("fest", Assert.Single(CrashAndRestart().Scan().Drafts).Text);
    }

    [Fact]
    public void ProvisionalSegmentsAreRejected()
    {
        var draft = Open().StartDraft();

        Assert.Throws<ArgumentException>(() => draft.Append(Provisional("vorläufig")));
        Assert.Throws<ArgumentException>(() => draft.Replace([Fixed("fest"), Provisional("vorläufig")]));
        Assert.Throws<ArgumentException>(() => draft.Apply(new RecognitionUpdate([Provisional("vorläufig")], [])));

        Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(Files.Files);
    }

    [Fact]
    public void StoredTextIsOnlyWrittenThroughTheProtector()
    {
        var draft = Open().StartDraft();

        draft.Append(Fixed("Grüezi mitenand, 200 CHF"));
        Time.Advance(TimeSpan.FromSeconds(1));

        var raw = Files.Files[SingleFileName()].Data;
        Assert.Single(Protector.Plaintexts);
        Assert.DoesNotContain("Grüezi", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadHoldsOnlyIdTimesAndFixedText()
    {
        // Kein Audio, keine Zeitmarken im Audio, kein vorläufiger Text: nur diese Felder (Grundentscheid 2)
        var draft = Open().StartDraft();
        draft.Apply(new RecognitionUpdate([Fixed("Hallo")], [Provisional("Welt")]));
        Time.Advance(TimeSpan.FromSeconds(1));

        using var json = JsonDocument.Parse(Protector.Plaintexts[^1]);
        var root = json.RootElement;

        Assert.Equal(
            ["createdAt", "handedOverAt", "id", "segments", "updatedAt"],
            root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var segment = Assert.Single(root.GetProperty("segments").EnumerateArray());
        Assert.Equal(["id", "text"], segment.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CorrectionsReplaceTheStoredText()
    {
        var draft = Open().StartDraft();
        var first = Fixed("Das ist");
        draft.Replace([first, Fixed("falsch")]);
        Time.Advance(TimeSpan.FromSeconds(1));

        draft.Replace([first, Fixed("richtig")]);
        Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("Das ist richtig", Assert.Single(CrashAndRestart().Scan().Drafts).Text);
    }

    [Fact]
    public void SegmentWithSameIdIsUpdatedInPlace()
    {
        var draft = Open().StartDraft();
        var segment = Fixed("eins");
        draft.Append(segment);
        draft.Append(Fixed("zwei"));

        draft.Append(segment with { Text = "Eins" });
        draft.Flush();

        Assert.Equal("Eins zwei", Assert.Single(CrashAndRestart().Scan().Drafts).Text);
    }

    [Fact]
    public void UnchangedDraftIsNotRewritten()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("Hallo"));
        Time.Advance(TimeSpan.FromSeconds(1));

        Time.Advance(TimeSpan.FromSeconds(10));

        Assert.Single(Protector.Plaintexts);
    }
}
