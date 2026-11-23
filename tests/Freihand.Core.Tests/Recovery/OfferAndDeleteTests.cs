using System.Reflection;
using Freihand.Core.Output;
using Freihand.Core.Platform;
using Freihand.Core.Recognition;
using Freihand.Core.Recovery;

namespace Freihand.Core.Tests.Recovery;

/// <summary>Anbieten nach Neustart (AU-12, BE-18) und Löschen einzeln und gesamt (SI-11).</summary>
public sealed class OfferAndDeleteTests : RecoveryTestBase
{
    [Fact]
    public void OpenDraftIsOfferedAfterRestartNewestFirst()
    {
        var store = Open();
        var older = store.StartDraft();
        older.Append(Fixed("älter"));
        Time.Advance(TimeSpan.FromSeconds(1));
        var newer = store.StartDraft();
        newer.Append(Fixed("neuer"));
        Time.Advance(TimeSpan.FromSeconds(1));

        var scan = CrashAndRestart().Scan();

        Assert.Equal(["neuer", "älter"], scan.Drafts.Select(d => d.Text));
        Assert.Equal(newer.Id, scan.Drafts[0].Id);
        Assert.Null(scan.Drafts[0].HandedOverAt);
        Assert.Empty(scan.Issues);
    }

    [Fact]
    public void DraftClosedOnShutdownIsKeptAndOffered()
    {
        var store = Open();
        var draft = store.StartDraft();
        draft.Append(Fixed("beim Beenden offen"));

        draft.Dispose();
        store.Dispose();

        Assert.Equal("beim Beenden offen", Assert.Single(Open().Scan().Drafts).Text);
    }

    [Fact]
    public void DisposingTheStoreSavesPendingText()
    {
        var store = Open();
        store.StartDraft().Append(Fixed("noch nicht im Takt"));

        store.Dispose();

        Assert.Single(Open().Scan().Drafts);
    }

    [Fact]
    public void RunningDraftIsNotOffered()
    {
        var store = Open();
        store.StartDraft().Append(Fixed("läuft gerade"));
        Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(store.Scan().Drafts);
    }

    [Fact]
    public void HandedOverDraftAcceptsNoMoreText()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("fertig"));
        draft.MarkHandedOver();

        Assert.Throws<InvalidOperationException>(() => draft.Append(Fixed("mehr")));
        Assert.Throws<InvalidOperationException>(draft.MarkHandedOver);
        Assert.False(draft.IsRecording);
    }

    [Fact]
    public void DiscardDeletesImmediately()
    {
        var draft = Open().StartDraft();
        draft.Append(Fixed("abbrechen"));
        Time.Advance(TimeSpan.FromSeconds(1));

        draft.Discard();

        Assert.Empty(Files.Files);
    }

    [Fact]
    public void DraftsCanBeDeletedOneByOneAndAll()
    {
        var store = Open();
        var first = store.StartDraft();
        first.Append(Fixed("eins"));
        var second = store.StartDraft();
        second.Append(Fixed("zwei"));
        var third = store.StartDraft();
        third.Append(Fixed("drei"));
        Time.Advance(TimeSpan.FromSeconds(1));

        store = CrashAndRestart();
        store.Delete(first.Id);
        Assert.Equal(["zwei", "drei"], store.Scan().Drafts.Select(d => d.Text).Order(StringComparer.Ordinal).Reverse());

        store.DeleteAll();
        Assert.Empty(store.Scan().Drafts);
        Assert.Empty(Files.Files);
    }

    [Fact]
    public void DeletingStopsARunningDraftFromWritingAgain()
    {
        var store = Open();
        var draft = store.StartDraft();
        draft.Append(Fixed("vorher"));
        Time.Advance(TimeSpan.FromSeconds(1));

        store.DeleteAll();
        draft.Append(Fixed("nachher"));
        Time.Advance(TimeSpan.FromSeconds(2));
        draft.Flush();
        draft.Dispose();

        Assert.False(draft.IsRecording);
        Assert.Empty(Files.Files);
    }

    [Fact]
    public void RecoveryNeverInsertsAndNeverTakesAudio()
    {
        // AU-12: nur anbieten, nie einfügen; Grundentscheid 2: Audio nie
        Type[] forbidden = [typeof(ITextInjector), typeof(ITextOutput), typeof(ITargetBinding), typeof(AudioChunk), typeof(IAudioCapture)];
        var types = typeof(RecoveryStore).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(RecoveryStore).Namespace)
            .ToList();

        var used = types
            .SelectMany(t => t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Select(f => f.FieldType)
                .Concat(t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
                .Concat(t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).SelectMany(m => m.GetParameters()).Select(p => p.ParameterType)))
            .SelectMany(Unwrap)
            .ToHashSet();

        Assert.NotEmpty(types);
        Assert.Empty(used.Intersect(forbidden));
    }

    private static IEnumerable<Type> Unwrap(Type type) =>
        type.IsGenericType ? [type, .. type.GetGenericArguments().SelectMany(Unwrap)] : [type];
}
