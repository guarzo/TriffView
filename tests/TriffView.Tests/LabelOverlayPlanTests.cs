using System.Drawing;
using TriffView.Preview;
using Xunit;

namespace TriffView.Tests;

/// <summary>
/// The label overlay draws one small layered form per preview. Moving a form is cheap;
/// repainting one is not, and a partial repaint does not work at all on a layered window.
/// These tests pin down which changes may be handled by a move and which must repaint.
/// </summary>
public class LabelOverlayPlanTests
{
    private static TriffViewLabelOverlayItem Item(
        nint handle = 1,
        int x = 100,
        int y = 100,
        int width = 320,
        int height = 204,
        string text = "Astrella Esubria",
        int fontSize = 9,
        string position = "top",
        int borderThickness = 1)
        => new(
            handle,
            new Rectangle(x, y, width, height),
            text,
            Color.FromArgb(217, 226, 238),
            fontSize,
            position,
            borderThickness);

    private static Dictionary<nint, TriffViewLabelOverlayItem> Previous(params TriffViewLabelOverlayItem[] items)
    {
        var map = new Dictionary<nint, TriffViewLabelOverlayItem>();
        foreach (var item in items) map[item.Handle] = item;
        return map;
    }

    [Fact]
    public void AnUnchangedItemProducesNoAction()
    {
        var item = Item();
        var actions = LabelOverlayPlan.Compute(Previous(item), new[] { item });
        Assert.Empty(actions);
    }

    [Fact]
    public void AnItemWithNoPreviousFormIsCreated()
    {
        var item = Item();
        var actions = LabelOverlayPlan.Compute(Previous(), new[] { item });
        var action = Assert.Single(actions);
        Assert.Equal(LabelOverlayActionKind.Create, action.Kind);
        Assert.Equal((nint)1, action.Handle);
    }

    [Fact]
    public void MovingAPreviewWithoutResizingItIsAMove()
    {
        var before = Item(x: 100, y: 100);
        var after = Item(x: 700, y: 380);
        var action = Assert.Single(LabelOverlayPlan.Compute(Previous(before), new[] { after }));
        Assert.Equal(LabelOverlayActionKind.Move, action.Kind);
    }

    // A resize changes the label's own geometry: its width is Frame.Width - inset*2, and the
    // "center" and "bottom" positions are derived from Frame.Height. Treating a resize as a
    // move would leave the label laid out for the previous size.
    [Fact]
    public void ResizingAPreviewIsARepaintNotAMove()
    {
        var before = Item(width: 320, height: 204);
        var after = Item(width: 480, height: 300);
        var action = Assert.Single(LabelOverlayPlan.Compute(Previous(before), new[] { after }));
        Assert.Equal(LabelOverlayActionKind.Repaint, action.Kind);
    }

    [Fact]
    public void ResizingInOneDimensionOnlyIsStillARepaint()
    {
        var before = Item(width: 320, height: 204);
        var after = Item(width: 320, height: 260);
        var action = Assert.Single(LabelOverlayPlan.Compute(Previous(before), new[] { after }));
        Assert.Equal(LabelOverlayActionKind.Repaint, action.Kind);
    }

    [Fact]
    public void MovingAndResizingAtOnceIsARepaint()
    {
        var before = Item(x: 100, y: 100, width: 320, height: 204);
        var after = Item(x: 700, y: 380, width: 480, height: 300);
        var action = Assert.Single(LabelOverlayPlan.Compute(Previous(before), new[] { after }));
        Assert.Equal(LabelOverlayActionKind.Repaint, action.Kind);
    }

    [Theory]
    [InlineData("Amelio Pellion", 9, "top", 1)]
    [InlineData("Astrella Esubria", 14, "top", 1)]
    [InlineData("Astrella Esubria", 9, "bottom", 1)]
    [InlineData("Astrella Esubria", 9, "top", 4)]
    public void AnyContentChangeIsARepaint(string text, int fontSize, string position, int borderThickness)
    {
        var before = Item();
        var after = Item(text: text, fontSize: fontSize, position: position, borderThickness: borderThickness);
        var action = Assert.Single(LabelOverlayPlan.Compute(Previous(before), new[] { after }));
        Assert.Equal(LabelOverlayActionKind.Repaint, action.Kind);
    }

    [Fact]
    public void AnItemThatDisappearsIsRetired()
    {
        var gone = Item(handle: 1);
        var stays = Item(handle: 2);
        var actions = LabelOverlayPlan.Compute(Previous(gone, stays), new[] { stays });
        var action = Assert.Single(actions);
        Assert.Equal(LabelOverlayActionKind.Retire, action.Kind);
        Assert.Equal((nint)1, action.Handle);
    }

    [Fact]
    public void AnEmptyCurrentListRetiresEverything()
    {
        var actions = LabelOverlayPlan.Compute(
            Previous(Item(handle: 1), Item(handle: 2)),
            Array.Empty<TriffViewLabelOverlayItem>());
        Assert.Equal(2, actions.Count);
        Assert.All(actions, a => Assert.Equal(LabelOverlayActionKind.Retire, a.Kind));
    }

    // Two clients showing the same name must not collide, which is why forms are keyed by
    // handle rather than by label text.
    [Fact]
    public void TwoItemsWithIdenticalTextAreTrackedSeparately()
    {
        var first = Item(handle: 1, text: "Unknown");
        var second = Item(handle: 2, text: "Unknown");
        var actions = LabelOverlayPlan.Compute(Previous(), new[] { first, second });
        Assert.Equal(2, actions.Count);
        Assert.All(actions, a => Assert.Equal(LabelOverlayActionKind.Create, a.Kind));
        Assert.Contains(actions, a => a.Handle == 1);
        Assert.Contains(actions, a => a.Handle == 2);
    }

    // A form sized to the frame alone would clip the label. Previews may be as small as
    // TriffViewPreviewDimensions.Minimum (16px) while a label is at least 18px tall plus
    // inset, so the label overflows. The desktop-spanning form absorbed that overflow.
    [Fact]
    public void FormBoundsContainALabelThatOverflowsATinyFrame()
    {
        var item = Item(x: 50, y: 50, width: 16, height: 16);
        var bounds = LabelOverlayPlan.FormBounds(item, textHeight: 18);
        Assert.True(bounds.Contains(item.Frame), "bounds must always contain the frame");
        Assert.True(bounds.Height > item.Frame.Height, "bounds must grow to fit an overflowing label");
    }

    [Fact]
    public void FormBoundsEqualTheFrameWhenTheLabelFitsInside()
    {
        var item = Item(x: 50, y: 50, width: 320, height: 204);
        var bounds = LabelOverlayPlan.FormBounds(item, textHeight: 18);
        Assert.Equal(item.Frame, bounds);
    }

    [Fact]
    public void FormBoundsContainABottomPositionedLabel()
    {
        var item = Item(x: 50, y: 50, width: 320, height: 20, position: "bottom");
        var bounds = LabelOverlayPlan.FormBounds(item, textHeight: 18);
        Assert.True(bounds.Contains(item.Frame));
        Assert.True(bounds.Top <= item.Frame.Top);
    }
}
