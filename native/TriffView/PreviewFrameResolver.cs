using System.Drawing;

namespace TriffView;

/// <summary>
/// Decides which rectangle a preview should occupy on a client sync.
///
/// The ordering exists because an EVE client that returns to character select loses its character
/// name, and with it the stable key its saved layout is filed under. Without a memory of where the
/// preview already was, that lookup misses and the preview jumps to the default stack in front of
/// the user. A remembered frame keyed by window keeps it exactly where it is until a character is
/// selected that has a usable saved layout of its own.
///
/// The remembered frame deliberately applies to named clients too, not just to nameless ones at
/// character select: selecting a character that has never been positioned must leave the preview
/// where it is rather than send it to the default stack.
///
/// The default frame and the title fallback stay with the caller: both depend on screen geometry.
/// They are passed in as deferred functions so that neither is computed when an earlier source
/// wins, and so the ordering itself has no Windows dependency and can be tested.
/// </summary>
internal static class PreviewFrameResolver
{
    /// <param name="titleFallback">Returns null when the caller has no title-based rectangle.</param>
    public static Rectangle Resolve(
        string stableKey,
        PreviewWindowKey windowKey,
        IReadOnlyDictionary<string, TriffViewRect> savedLayouts,
        RememberedPreviewFrames rememberedFrames,
        Func<Rectangle?> titleFallback,
        Func<Rectangle> defaultFrame)
    {
        if (savedLayouts.TryGetValue(stableKey, out var saved) && saved.IsUsable)
        {
            return saved.ToRectangle();
        }

        if (rememberedFrames.TryGet(windowKey, out var remembered))
        {
            return remembered;
        }

        return titleFallback() ?? defaultFrame();
    }
}
