using System.Drawing;

namespace TriffView;

/// <summary>
/// Distinguishes a click from a drag for preview interactions.
///
/// Clicking a preview switches to that client, so the decision has to be forgiving of the small
/// cursor movement that happens during any real click. It is made on the net displacement
/// between press and release rather than on the largest movement seen along the way: a cursor
/// that wanders and comes back has not been dragged anywhere, and swallowing that click would
/// leave the user pressing a preview that never responds.
///
/// The tolerance is per axis, matching the system drag metrics (SM_CXDRAG / SM_CYDRAG) that
/// every other Windows application uses.
/// </summary>
internal static class PreviewPointerGesture
{
    public static bool IsClick(Point start, Point end, Size dragSize)
    {
        // Max(0, ...) so a zero or nonsensical metric degrades to "must not move at all"
        // rather than inverting the comparison and making every click impossible.
        return Math.Abs(end.X - start.X) <= Math.Max(0, dragSize.Width)
            && Math.Abs(end.Y - start.Y) <= Math.Max(0, dragSize.Height);
    }
}
