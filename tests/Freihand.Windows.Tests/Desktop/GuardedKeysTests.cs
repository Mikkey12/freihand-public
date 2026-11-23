using Freihand.Core.Platform;

namespace Freihand.Windows.Tests.Desktop;

/// <summary>Der Eingabehelfer der Desktoptests mit Attrappen für Vordergrund und Sender (Prüfung PR #34, U34-11).</summary>
public sealed class GuardedKeysTests
{
    [Fact]
    public void NoDownEventLeavesWhenTheOwnWindowIsNotInForeground()
    {
        var sender = new RecordingSender();
        using var keys = new GuardedKeys(() => false, sender);

        Assert.Throws<ForegroundLostException>(() => keys.Down(0x59));
        Assert.Empty(sender.Events);
    }

    [Fact]
    public void ForegroundIsCheckedBeforeEveryDownAndPressedKeysAreReleasedOnAbort()
    {
        var foreground = true;
        var sender = new RecordingSender();
        var keys = new GuardedKeys(() => foreground, sender);
        keys.Down(0x11);
        keys.Down(0x12);

        // Nach einer Wartephase ist ein fremdes Fenster vorn: kein weiteres Ab, die eigenen Tasten gehen auf
        foreground = false;
        Assert.Throws<ForegroundLostException>(() => keys.Down(0x59));
        keys.Dispose();

        Assert.Equal(
            [(0x11, true), (0x12, true), (0x12, false), (0x11, false)],
            sender.Events.Select(e => (e.Code, e.Down)));
        Assert.Empty(keys.Pressed);
    }

    [Fact]
    public void ReleasedKeysAreNotReleasedTwice()
    {
        var sender = new RecordingSender();
        var keys = new GuardedKeys(() => true, sender);
        keys.Tap(0x45);
        keys.Dispose();

        Assert.Equal([(0x45, true), (0x45, false)], sender.Events.Select(e => (e.Code, e.Down)));
    }

    [Fact]
    public void AFailedDownIsReportedAndNotRecordedAsPressed()
    {
        var sender = new RecordingSender { Accept = _ => false };
        using var keys = new GuardedKeys(() => true, sender);

        Assert.Throws<InputNotInsertedException>(() => keys.Down(0x11));
        Assert.Empty(keys.Pressed);
    }

    [Fact]
    public void AFailedReleaseStaysRecordedAndDisposeRetriesAllAndThenReports()
    {
        var failing = new HashSet<(int Code, bool Down)> { (0x12, false) };
        var sender = new RecordingSender { Accept = e => !failing.Contains((e.Code, e.Down)) };
        var keys = new GuardedKeys(() => true, sender);
        keys.Down(0x11);
        keys.Down(0x12);

        Assert.Throws<InputNotInsertedException>(() => keys.Up(0x12));
        Assert.Equal([0x11, 0x12], keys.Pressed);

        // Dispose versucht jede verbleibende Freigabe und meldet danach, was offen blieb
        var error = Assert.Throws<InputNotInsertedException>(keys.Dispose);
        Assert.Contains("0x12", error.Message, StringComparison.Ordinal);
        Assert.Equal([(0x12, false), (0x11, false)], sender.Events.Skip(3).Select(e => (e.Code, e.Down)));
        Assert.Equal([0x12], keys.Pressed);
    }

    [Fact]
    public void RepeatedDownOfTheSameKeyIsRecordedOnce()
    {
        var sender = new RecordingSender();
        var keys = new GuardedKeys(() => true, sender);
        keys.Down(0x59);
        keys.Down(0x59);
        keys.Down(0x59);

        Assert.Equal([0x59], keys.Pressed);
        keys.Dispose();
        Assert.Equal(1, sender.Events.Count(e => !e.Down));
    }

    private sealed class RecordingSender : ISendInputApi
    {
        public List<InputEvent> Events { get; } = [];

        /// <summary>Ob <c>SendInput</c> das Ereignis einfügt; Standard ja.</summary>
        public Func<InputEvent, bool> Accept { get; set; } = _ => true;

        public int Send(IReadOnlyList<InputEvent> events)
        {
            Events.AddRange(events);
            return events.Count(item => Accept(item));
        }
    }
}
