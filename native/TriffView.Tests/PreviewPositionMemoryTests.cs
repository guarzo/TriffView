using System.Drawing;
using TriffView.Preview;
using Xunit;

namespace TriffView.Tests;

/// <summary>
/// Preview position memory: the order a preview's rectangle is chosen in, and the slot probing
/// that stops two previews landing on the same rectangle.
///
/// These live here rather than in the cross-platform suite under tests/ because
/// <see cref="TriffViewPreviewPositionMemory"/> references EveClientWindow and ScreenPixelInfo,
/// which are Windows-side types. This project already carries a ProjectReference to the app and
/// InternalsVisibleTo, so it is the only place they compile.
/// </summary>
public class PreviewPositionMemoryTests
{
    private static readonly Rectangle Saved = new(10, 10, 320, 180);
    private static readonly Rectangle Remembered = new(20, 20, 320, 180);
    private static readonly Rectangle Title = new(30, 30, 320, 180);
    private static readonly Rectangle Default = new(40, 40, 320, 180);

    private static PreviewClientIdentity Window(int handle, uint pid = 1000)
    {
        return new PreviewClientIdentity(handle, pid);
    }

    // ---- Resolution order ----

    [Fact]
    public void SavedLayoutWinsOverEverythingElse()
    {
        Assert.Equal(Saved, TriffViewPreviewPositionMemory.Resolve(Saved, Remembered, Title, Default));
    }

    [Fact]
    public void RememberedFrameIsUsedWhenThereIsNoSavedLayout()
    {
        Assert.Equal(Remembered, TriffViewPreviewPositionMemory.Resolve(null, Remembered, Title, Default));
    }

    [Fact]
    public void RememberedFrameOutranksTheTitleFallback()
    {
        // This is what holds a preview in place when its client drops to character select and
        // loses the character name its saved layout is filed under.
        Assert.Equal(Remembered, TriffViewPreviewPositionMemory.Resolve(null, Remembered, Title, Default));
    }

    [Fact]
    public void TitleFallbackIsUsedWhenNothingIsRemembered()
    {
        Assert.Equal(Title, TriffViewPreviewPositionMemory.Resolve(null, null, Title, Default));
    }

    [Fact]
    public void DefaultIsUsedWhenThereIsNothingElse()
    {
        Assert.Equal(Default, TriffViewPreviewPositionMemory.Resolve(null, null, null, Default));
    }

    // ---- Remembering ----

    [Fact]
    public void ARememberedFrameComesBackForTheSameWindow()
    {
        var memory = new TriffViewPreviewPositionMemory();
        memory.Remember(Window(1), Remembered);

        Assert.True(memory.TryGet(Window(1), out var frame));
        Assert.Equal(Remembered, frame);
    }

    [Fact]
    public void ARecycledHandleUnderADifferentProcessDoesNotInheritTheFrame()
    {
        // Windows recycles window handles. A new client on a dead one's HWND must not silently
        // take over its rectangle.
        var memory = new TriffViewPreviewPositionMemory();
        memory.Remember(Window(1, pid: 1000), Remembered);

        Assert.False(memory.TryGet(Window(1, pid: 2000), out _));
    }

    [Fact]
    public void PurgingDropsWindowsThatAreGoneAndKeepsThoseStillPresent()
    {
        var memory = new TriffViewPreviewPositionMemory();
        memory.Remember(Window(1), Remembered);
        memory.Remember(Window(2), Title);

        memory.PurgeExcept(new HashSet<PreviewClientIdentity> { Window(2) });

        Assert.False(memory.TryGet(Window(1), out _));
        Assert.True(memory.TryGet(Window(2), out _));
    }

    // ---- Context invalidation ----

    [Fact]
    public void AnUnchangedContextKeepsRememberedFrames()
    {
        var memory = new TriffViewPreviewPositionMemory();
        var topology = new[] { new ScreenPixelInfo("\\\\.\\DISPLAY1", true, new Rectangle(0, 0, 2560, 1440)) };

        memory.BeginContext("profile", 320, 180, topology);
        memory.Remember(Window(1), Remembered);
        memory.BeginContext("profile", 320, 180, topology);

        Assert.True(memory.TryGet(Window(1), out _));
    }

    [Fact]
    public void AChangedPreviewSizeForgetsEverything()
    {
        var memory = new TriffViewPreviewPositionMemory();
        var topology = new[] { new ScreenPixelInfo("\\\\.\\DISPLAY1", true, new Rectangle(0, 0, 2560, 1440)) };

        memory.BeginContext("profile", 320, 180, topology);
        memory.Remember(Window(1), Remembered);

        Assert.True(memory.BeginContext("profile", 400, 180, topology));
        Assert.False(memory.TryGet(Window(1), out _));
    }

    // ---- Free-slot probing ----
    //
    // The default stack assigns slots by position in the visible client list, which the context
    // signature cannot see. A slot can therefore be "next in line" and still held by a client that
    // outlived a closure earlier in the stack. Without probing, two previews land on one rectangle.

    private static Rectangle Slot(int index) => new(0, index * 200, 320, 180);

    [Fact]
    public void TheRequestedSlotIsUsedWhenNothingIsRemembered()
    {
        var memory = new TriffViewPreviewPositionMemory();

        Assert.Equal(3, memory.FindFreeSlot(3, Window(1), Slot));
    }

    [Fact]
    public void ASlotHeldByAnotherWindowIsSkipped()
    {
        var memory = new TriffViewPreviewPositionMemory();
        memory.Remember(Window(2), Slot(3));

        Assert.Equal(4, memory.FindFreeSlot(3, Window(1), Slot));
    }

    [Fact]
    public void ProbingSkipsARunOfHeldSlots()
    {
        var memory = new TriffViewPreviewPositionMemory();
        memory.Remember(Window(2), Slot(3));
        memory.Remember(Window(3), Slot(4));
        memory.Remember(Window(4), Slot(5));

        Assert.Equal(6, memory.FindFreeSlot(3, Window(1), Slot));
    }

    [Fact]
    public void AWindowDoesNotAvoidItsOwnRememberedFrame()
    {
        // Otherwise a preview would walk down the stack on every sync.
        var memory = new TriffViewPreviewPositionMemory();
        memory.Remember(Window(1), Slot(3));

        Assert.Equal(3, memory.FindFreeSlot(3, Window(1), Slot));
    }

    [Fact]
    public void OnlyAnExactRectangleMatchCountsAsACollision()
    {
        var memory = new TriffViewPreviewPositionMemory();
        memory.Remember(Window(2), new Rectangle(1, 600, 320, 180));

        Assert.Equal(3, memory.FindFreeSlot(3, Window(1), Slot));
    }

    [Fact]
    public void ProbingGivesUpAndReturnsTheRequestedSlot()
    {
        // The stack wraps into columns and its arithmetic is not injective, so a free slot may not
        // exist. Returning the requested slot is better than spinning.
        var memory = new TriffViewPreviewPositionMemory();
        memory.Remember(Window(2), Slot(0));

        Assert.Equal(7, memory.FindFreeSlot(7, Window(1), _ => Slot(0)));
    }

    // ---- Title-fallback collision (character-select clients) ----
    //
    // ResolveFrameRect (TriffViewSubsystem.cs) derives a title-based candidate rectangle for
    // nameless (character-select) clients from a same-title index that only sees the live client
    // list. That index can't see who currently holds a rectangle in _positionMemory, so a client
    // further down the order can land on a rect another client is already pinned to. ResolveFrameRect
    // discards the candidate in that case via IsHeldByAnother and falls through to the
    // collision-probed default stack instead - these tests exercise that same decision.

    [Fact]
    public void ATitleFallbackHeldByAnotherClientFallsThroughToTheDefaultStack()
    {
        var memory = new TriffViewPreviewPositionMemory();
        memory.Remember(Window(2), Title);

        Rectangle? titleFallback = Title;
        if (memory.IsHeldByAnother(titleFallback.Value, Window(1))) titleFallback = null;

        Assert.Equal(Default, TriffViewPreviewPositionMemory.Resolve(null, null, titleFallback, Default));
    }

    [Fact]
    public void ATitleFallbackHeldByNobodyIsStillUsed()
    {
        var memory = new TriffViewPreviewPositionMemory();

        Rectangle? titleFallback = Title;
        if (memory.IsHeldByAnother(titleFallback.Value, Window(1))) titleFallback = null;

        Assert.Equal(Title, TriffViewPreviewPositionMemory.Resolve(null, null, titleFallback, Default));
    }

    [Fact]
    public void TheFirstClientOntoATitleRectKeepsItsTitlePosition()
    {
        // Window(1) is the first client onto this rect - nobody else is remembered there yet, so
        // it must keep the title-derived position rather than being pushed to the default stack.
        var memory = new TriffViewPreviewPositionMemory();

        Rectangle? titleFallback = Title;
        if (memory.IsHeldByAnother(titleFallback.Value, Window(1))) titleFallback = null;
        Assert.Equal(Title, TriffViewPreviewPositionMemory.Resolve(null, null, titleFallback, Default));

        // Once Window(1) is remembered there, a second client computing the same title rect must
        // fall through instead of doubling up on it.
        memory.Remember(Window(1), Title);
        Rectangle? secondTitleFallback = Title;
        if (memory.IsHeldByAnother(secondTitleFallback.Value, Window(2))) secondTitleFallback = null;
        Assert.Equal(Default, TriffViewPreviewPositionMemory.Resolve(null, null, secondTitleFallback, Default));
    }
}
