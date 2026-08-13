using System.Drawing;
using TriffView;
using Xunit;

namespace TriffView.Tests;

public class PreviewFrameResolverTests
{
    private static readonly Rectangle Default = new(1900, 82, 320, 180);
    private static readonly Rectangle Remembered = new(400, 500, 320, 180);
    private static readonly Rectangle TitleFallback = new(700, 300, 320, 180);

    private static readonly PreviewWindowKey Client7 = new(7, 100);

    private static RememberedPreviewFrames Frames(params (PreviewWindowKey Key, Rectangle Frame)[] entries)
    {
        var frames = new RememberedPreviewFrames();
        foreach (var entry in entries) frames.Remember(entry.Key, entry.Frame);
        return frames;
    }

    private static Dictionary<string, TriffViewRect> Saved(params (string Key, Rectangle Rect)[] entries)
    {
        var saved = new Dictionary<string, TriffViewRect>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) saved[entry.Key] = TriffViewRect.FromRectangle(entry.Rect);
        return saved;
    }

    private static Rectangle Resolve(
        string stableKey,
        PreviewWindowKey windowKey,
        Dictionary<string, TriffViewRect>? saved = null,
        RememberedPreviewFrames? remembered = null,
        Rectangle? titleFallback = null)
    {
        return PreviewFrameResolver.Resolve(
            stableKey,
            windowKey,
            saved ?? new Dictionary<string, TriffViewRect>(StringComparer.OrdinalIgnoreCase),
            remembered ?? new RememberedPreviewFrames(),
            () => titleFallback,
            () => Default);
    }

    [Fact]
    public void SavedLayoutWinsOverRememberedFrame()
    {
        var saved = Saved(("Pilot One", new Rectangle(10, 20, 320, 180)));

        Assert.Equal(
            new Rectangle(10, 20, 320, 180),
            Resolve("Pilot One", Client7, saved, Frames((Client7, Remembered))));
    }

    [Fact]
    public void RememberedFrameIsUsedWhenNoSavedLayoutExists()
    {
        // The logoff case: the client dropped back to character select, so the stable key degraded
        // to the window handle in hex and no longer matches anything saved.
        var saved = Saved(("Pilot One", new Rectangle(10, 20, 320, 180)));

        Assert.Equal(Remembered, Resolve("7", Client7, saved, Frames((Client7, Remembered))));
    }

    [Fact]
    public void NamedClientWithNoSavedLayoutOfItsOwnHoldsTheRememberedFrame()
    {
        // The requirement this whole mechanism exists for: a character has just been selected on a
        // client whose preview the user had placed by hand, but that character has never been
        // positioned. It must stay put rather than jump to the default stack. This is why the
        // remembered frame is not restricted to nameless clients.
        var saved = Saved(("Someone Else", new Rectangle(10, 20, 320, 180)));

        Assert.Equal(Remembered, Resolve("Pilot One", Client7, saved, Frames((Client7, Remembered))));
    }

    [Fact]
    public void SavedLayoutKeyedByHexHandleStillOutranksTheRememberedFrame()
    {
        // Older builds persisted layouts for nameless clients under their handle in hex, and those
        // orphan entries are not pruned from settings. Such a key can collide with a live handle,
        // in which case it wins - it is reached by the ordinary saved-layout lookup. Documented
        // rather than defended against: pruning pre-existing orphans is out of scope here.
        var saved = Saved(("7", new Rectangle(10, 20, 320, 180)));

        Assert.Equal(
            new Rectangle(10, 20, 320, 180),
            Resolve("7", Client7, saved, Frames((Client7, Remembered))));
    }

    [Fact]
    public void RecycledHandleWithADifferentProcessDoesNotInheritTheFrame()
    {
        var recycled = new PreviewWindowKey(Client7.Handle, Client7.ProcessId + 1);

        Assert.Equal(Default, Resolve("7", recycled, remembered: Frames((Client7, Remembered))));
    }

    [Fact]
    public void UnusableSavedLayoutIsIgnored()
    {
        // Defence in depth only: TriffViewProfile.Normalize clamps Width/Height up to Minimum on
        // both load and save, so an unusable saved layout is unreachable for anything that came
        // from settings. The guard is kept because the resolver does not own that invariant.
        var saved = new Dictionary<string, TriffViewRect>(StringComparer.OrdinalIgnoreCase)
        {
            ["Pilot One"] = new TriffViewRect { X = 10, Y = 20, Width = 0, Height = 0 },
        };

        Assert.Equal(Remembered, Resolve("Pilot One", Client7, saved, Frames((Client7, Remembered))));
        Assert.Equal(Default, Resolve("Pilot One", Client7, saved));
    }

    [Fact]
    public void DefaultIsUsedWhenThereIsNeitherSavedNorRemembered()
    {
        Assert.Equal(Default, Resolve("Pilot One", Client7));
    }

    [Fact]
    public void NewHandleWithNoRememberedFrameGetsTheDefault()
    {
        Assert.Equal(
            Default,
            Resolve("9", new PreviewWindowKey(9, 101), remembered: Frames((Client7, Remembered))));
    }

    [Fact]
    public void RememberedFrameWinsOverTitleFallback()
    {
        Assert.Equal(
            Remembered,
            Resolve("7", Client7, remembered: Frames((Client7, Remembered)), titleFallback: TitleFallback));
    }

    [Fact]
    public void TitleFallbackIsUsedWhenNothingIsRemembered()
    {
        Assert.Equal(TitleFallback, Resolve("7", Client7, titleFallback: TitleFallback));
    }

    [Fact]
    public void EarlierSourcesShortCircuitTheDeferredFallbacks()
    {
        var saved = Saved(("Pilot One", new Rectangle(10, 20, 320, 180)));

        PreviewFrameResolver.Resolve(
            "Pilot One",
            Client7,
            saved,
            new RememberedPreviewFrames(),
            () => throw new InvalidOperationException("title fallback must not be evaluated"),
            () => throw new InvalidOperationException("default must not be evaluated"));
    }
}
