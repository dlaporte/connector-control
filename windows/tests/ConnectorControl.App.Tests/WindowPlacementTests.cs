using System.Drawing;
using ConnectorControl.App.Views;

namespace ConnectorControl.App.Tests;

public class WindowPlacementTests
{
    private static readonly Size Flyout = new(300, 400);

    [Fact]
    public void BottomTaskbarPlacesTheFlyoutAboveTheWorkAreaEdge()
    {
        var rect = WindowPlacement.PlaceNear(new Point(1800, 1060), new Rectangle(0, 0, 1920, 1040), Flyout);
        Assert.Equal(new Rectangle(1612, 632, 300, 400), rect);   // x clamped to the right edge minus the margin
    }

    [Fact]
    public void TopTaskbarPlacesTheFlyoutBelowTheWorkAreaEdge()
    {
        var rect = WindowPlacement.PlaceNear(new Point(100, 20), new Rectangle(0, 40, 1920, 1040), Flyout);
        Assert.Equal(new Rectangle(8, 48, 300, 400), rect);
    }

    [Fact]
    public void LeftTaskbarPlacesTheFlyoutAtTheLeftEdge()
    {
        var rect = WindowPlacement.PlaceNear(new Point(30, 900), new Rectangle(60, 0, 1800, 1080), Flyout);
        Assert.Equal(new Rectangle(68, 492, 300, 400), rect);
    }

    [Fact]
    public void RightTaskbarPlacesTheFlyoutAtTheRightEdge()
    {
        var rect = WindowPlacement.PlaceNear(new Point(1890, 500), new Rectangle(0, 0, 1860, 1080), Flyout);
        Assert.Equal(new Rectangle(1552, 92, 300, 400), rect);
    }

    [Fact]
    public void AnchorInsideTheWorkAreaOpensAboveTheAnchor()
    {
        var rect = WindowPlacement.PlaceNear(new Point(960, 1000), new Rectangle(0, 0, 1920, 1080), Flyout);
        Assert.Equal(new Rectangle(810, 592, 300, 400), rect);
    }

    // Spec §7.1 anchoring order: the tray's own corner, then the cursor, then the work-area corner.

    [Fact]
    public void AnchorPrefersTheTrayPosition()
    {
        var anchor = WindowPlacement.Anchor(new Point(1900, 1035), new Point(400, 400), new Rectangle(0, 0, 1920, 1040));
        Assert.Equal(new Point(1900, 1035), anchor);
    }

    [Fact]
    public void AnchorFallsBackToTheCursorWhenTheShellReportsNoTray()
    {
        Assert.Equal(new Point(400, 400), WindowPlacement.Anchor(null, new Point(400, 400), new Rectangle(0, 0, 1920, 1040)));
        Assert.Equal(new Point(400, 400), WindowPlacement.Anchor(Point.Empty, new Point(400, 400), new Rectangle(0, 0, 1920, 1040)));   // (0,0) means "not reported"
    }

    [Fact]
    public void AnchorFallsBackToTheWorkAreaCornerWhenNeitherIsReported()
    {
        var workArea = new Rectangle(0, 0, 1920, 1040);
        var anchor = WindowPlacement.Anchor(null, null, workArea);
        Assert.Equal(new Point(1920, 1040), anchor);
        Assert.Equal(new Rectangle(1612, 632, 300, 400), WindowPlacement.PlaceNear(anchor, workArea, Flyout));   // bottom-right, inset by the margin
    }
}
