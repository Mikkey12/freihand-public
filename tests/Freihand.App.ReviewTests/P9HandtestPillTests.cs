namespace Freihand.App;

/// <summary>
/// Handtest P9 vom 11.11.2026, Platz der Pille. Regel: Die Pille hängt an der Ecke, die dem nächsten Bildschirmrand
/// zugewandt ist, wächst von dort weg und bleibt immer ganz im Arbeitsbereich ihres Bildschirms.
/// </summary>
public sealed class P9HandtestPillTests
{
    private static readonly (double Left, double Top, double Right, double Bottom) Area = (0, 0, 1920, 1032);

    // Alle vier Ecken: je der nächste Rand bleibt fest, beim Wachsen ändert sich nur die Gegenseite
    [Theory]
    [InlineData(1700, 960, false, false)]
    [InlineData(20, 960, true, false)]
    [InlineData(1700, 20, false, true)]
    [InlineData(20, 20, true, true)]
    public void ThePillGrowsAwayFromTheNearestEdges(double left, double top, bool fromLeft, bool fromTop)
    {
        var anchor = PillPresenter.AnchorOf(left, top, 200, 52, Area);
        Assert.Equal(fromLeft, anchor.FromLeft);
        Assert.Equal(fromTop, anchor.FromTop);

        var small = PillPresenter.PlaceAt(anchor, 200, 52, Area);
        var large = PillPresenter.PlaceAt(anchor, 420, 140, Area);

        Assert.Equal((left, top), small);
        Assert.Equal(fromLeft ? left : left + 200, fromLeft ? large.Left : large.Left + 420);
        Assert.Equal(fromTop ? top : top + 52, fromTop ? large.Top : large.Top + 140);
        Assert.True(large.Left >= Area.Left && large.Left + 420 <= Area.Right, "waagrecht ganz auf dem Bildschirm");
        Assert.True(large.Top >= Area.Top && large.Top + 140 <= Area.Bottom, "senkrecht ganz auf dem Bildschirm");
    }

    [Fact]
    public void AGrowingPillIsPulledBackOnScreenEvenFromTheMiddle()
    {
        // Knapp links der Mitte, links fest: eine sehr breite Pille stösst rechts an und wird zurückgeholt
        var anchor = PillPresenter.AnchorOf(900, 500, 100, 52, Area);
        var (left, _) = PillPresenter.PlaceAt(anchor, 1500, 52, Area);

        Assert.True(anchor.FromLeft);
        Assert.Equal(420d, left);
    }

    [Fact]
    public void APillLargerThanTheAreaStandsAtItsTopLeft()
    {
        var anchor = PillPresenter.DefaultAnchor(Area);

        Assert.Equal((0d, 0d), PillPresenter.PlaceAt(anchor, 2500, 1200, Area));
    }

    [Fact]
    public void TheDefaultIsBottomRightAboveTheTaskbar()
    {
        var anchor = PillPresenter.DefaultAnchor(Area);

        Assert.False(anchor.FromLeft);
        Assert.False(anchor.FromTop);
        Assert.Equal((1612d, 972d), PillPresenter.PlaceAt(anchor, 300, 52, Area));
    }

    [Fact]
    public void ASecondScreenWithItsOwnAreaIsRespected()
    {
        var right = (Left: 1920d, Top: 0d, Right: 3840d, Bottom: 1080d);

        var anchor = PillPresenter.AnchorOf(1940, 20, 300, 52, right);

        Assert.True(anchor.FromLeft && anchor.FromTop);
        Assert.Equal((1940d, 20d), PillPresenter.PlaceAt(anchor, 500, 140, right));
    }

    [Fact]
    public void TheAnchorIsSavedAndReadBackWithoutTexts()
    {
        var anchor = new PillAnchor(25, 40, FromLeft: true, FromTop: false);

        var text = PillPresenter.FormatAnchor(anchor);

        Assert.Equal(anchor, PillPresenter.ParseAnchor(text.Split('\n')));
        Assert.Contains("pille_anker = 25;40;links;unten", text, StringComparison.Ordinal);
        Assert.Null(PillPresenter.ParseAnchor(["pille_ecke = 1900;1000"]));
        Assert.Null(PillPresenter.ParseAnchor(["pille_anker = 1;2;mitte;unten"]));
        Assert.Null(PillPresenter.ParseAnchor([]));
    }
}
