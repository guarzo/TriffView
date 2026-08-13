using System.Drawing;

namespace TriffView.Preview;

/// <summary>
/// One label to be drawn over a preview. Carries the owning client's window handle so the
/// overlay controller can key its per-preview forms by the same identity <c>_previews</c> uses.
/// Keying by label text instead would collide whenever two clients show the same name, or
/// while a name is briefly blank during a state transition.
///
/// <para><c>Frame</c> is in client coordinates relative to the virtual desktop origin, as
/// produced by <c>ToClientRect</c>.</para>
/// </summary>
internal sealed record TriffViewLabelOverlayItem(
    nint Handle,
    Rectangle Frame,
    string Text,
    Color TextColor,
    int FontSize,
    string Position,
    int BorderThickness);

internal enum LabelOverlayActionKind
{
    Create,
    Move,
    Repaint,
    Retire,
}

/// <summary>
/// One thing the overlay controller should do to one per-preview label form.
/// <c>Item</c> is null only for <see cref="LabelOverlayActionKind.Retire"/>.
/// </summary>
internal sealed record LabelOverlayAction(
    LabelOverlayActionKind Kind,
    nint Handle,
    TriffViewLabelOverlayItem? Item);

/// <summary>
/// Decides what each per-preview label form needs, given the labels drawn last time and the
/// labels wanted now.
///
/// <para>The distinction that matters is move versus repaint. Repainting is expensive - the
/// form must repaint its whole surface, because a partial repaint does not reach a layered
/// window's composited surface - while moving a window is nearly free. Dragging a preview
/// changes only the frame's location, so it can be handled entirely by moving.</para>
///
/// <para>A resize is NOT a move: the label's width is derived from the frame width, and its
/// vertical placement from the frame height, so a resized preview must repaint.</para>
/// </summary>
internal static class LabelOverlayPlan
{
    public static IReadOnlyList<LabelOverlayAction> Compute(
        IReadOnlyDictionary<nint, TriffViewLabelOverlayItem> previous,
        IReadOnlyList<TriffViewLabelOverlayItem> current)
    {
        var actions = new List<LabelOverlayAction>();
        var seen = new HashSet<nint>();

        foreach (var item in current)
        {
            seen.Add(item.Handle);

            if (!previous.TryGetValue(item.Handle, out var before))
            {
                actions.Add(new LabelOverlayAction(LabelOverlayActionKind.Create, item.Handle, item));
                continue;
            }

            if (before == item) continue;

            var kind = ContentEquals(before, item) && before.Frame.Size == item.Frame.Size
                ? LabelOverlayActionKind.Move
                : LabelOverlayActionKind.Repaint;

            actions.Add(new LabelOverlayAction(kind, item.Handle, item));
        }

        foreach (var handle in previous.Keys)
        {
            if (seen.Contains(handle)) continue;
            actions.Add(new LabelOverlayAction(LabelOverlayActionKind.Retire, handle, null));
        }

        return actions;
    }

    /// <summary>
    /// The bounds a form needs so it can draw its label without clipping.
    ///
    /// <para>Usually this is just the preview frame, but not always: previews may be as small
    /// as 16px while a label is at least 18px tall plus its inset, so on a small preview the
    /// label spills outside the frame. The old desktop-spanning form absorbed that silently
    /// because it owned the whole desktop; a form sized to the frame would clip it.</para>
    ///
    /// <para><paramref name="textHeight"/> must be the same value the painter uses, so bounds
    /// and drawing cannot disagree.</para>
    /// </summary>
    public static Rectangle FormBounds(TriffViewLabelOverlayItem item, int textHeight)
    {
        var label = LabelRect(item, textHeight);

        // The painter draws a drop shadow offset by one pixel down and right.
        label.Width += 1;
        label.Height += 1;

        return Rectangle.Union(item.Frame, label);
    }

    /// <summary>
    /// Where the label text is drawn, in the same coordinate space as <c>item.Frame</c>.
    /// Mirrors the painter exactly; change both together or labels will clip.
    /// </summary>
    public static Rectangle LabelRect(TriffViewLabelOverlayItem item, int textHeight)
    {
        var inset = Math.Max(6, item.BorderThickness + 6);
        var labelTop = item.Position switch
        {
            "bottom" => item.Frame.Bottom - inset - textHeight,
            "center" => item.Frame.Top + (item.Frame.Height - textHeight) / 2,
            _ => item.Frame.Top + inset,
        };

        return new Rectangle(
            item.Frame.Left + inset,
            labelTop,
            Math.Max(1, item.Frame.Width - inset * 2),
            textHeight);
    }

    private static bool ContentEquals(TriffViewLabelOverlayItem a, TriffViewLabelOverlayItem b)
        => a.Text == b.Text
            && a.TextColor == b.TextColor
            && a.FontSize == b.FontSize
            && a.Position == b.Position
            && a.BorderThickness == b.BorderThickness;
}
