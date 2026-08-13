using System.Drawing;
using TriffView;
using Xunit;

namespace TriffView.Tests;

public class RememberedPreviewFramesTests
{
    private static readonly Rectangle Frame = new(400, 500, 320, 180);
    private static readonly Rectangle OtherFrame = new(100, 200, 320, 180);
    private static readonly Rectangle Primary = new(0, 0, 2560, 1440);

    private static readonly PreviewWindowKey ClientA = new(7, 100);
    private static readonly PreviewWindowKey ClientB = new(9, 101);

    // ComposeSignature's own separators, spelled by code point so they stay legible in this file.
    private const char FieldSeparator = (char)0x1E;
    private const char ItemSeparator = (char)0x1F;
    private const string Backslash = "\\";

    /// <summary>
    /// Stand-in for the form's DefaultFrameRect: a stack of non-overlapping slots, one per index.
    /// </summary>
    private static Rectangle Slot(int index) => new(1900, 82 + index * 190, 320, 180);

    private static string Signature(
        string profileId = "profile-1",
        int width = 320,
        int height = 180,
        string[]? characterOrder = null,
        string[]? hiddenClients = null,
        Rectangle? primaryScreen = null)
    {
        return RememberedPreviewFrames.ComposeSignature(
            profileId,
            width,
            height,
            characterOrder ?? Array.Empty<string>(),
            hiddenClients ?? Array.Empty<string>(),
            primaryScreen ?? Primary);
    }

    private static RememberedPreviewFrames Seeded(string? signature = null)
    {
        var frames = new RememberedPreviewFrames();
        frames.ResetIfChanged(signature ?? Signature());
        frames.Remember(ClientA, Frame);
        frames.Remember(ClientB, OtherFrame);
        return frames;
    }

    [Fact]
    public void RememberedFrameIsReturnedForTheSameWindow()
    {
        var frames = Seeded();

        Assert.True(frames.TryGet(ClientA, out var frame));
        Assert.Equal(Frame, frame);
    }

    [Fact]
    public void RecycledHandleWithADifferentProcessIsNotTheSameWindow()
    {
        // Windows reuses HWNDs. A client that closes and is replaced on the same handle between
        // polls has no saved layout to match (it is at character select, so nameless) and would
        // otherwise silently inherit the dead client's rectangle.
        var frames = Seeded();

        Assert.False(frames.TryGet(new PreviewWindowKey(ClientA.Handle, ClientA.ProcessId + 1), out _));
    }

    [Fact]
    public void IdenticalSignatureKeepsRememberedFrames()
    {
        var frames = Seeded();

        Assert.False(frames.ResetIfChanged(Signature()));
        Assert.Equal(2, frames.Count);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("order")]
    [InlineData("hidden")]
    [InlineData("screen")]
    public void ChangingAnySignatureInputForgetsEverything(string change)
    {
        var frames = Seeded();

        var changed = change switch
        {
            "profile" => Signature(profileId: "profile-2"),
            "width" => Signature(width: 400),
            "height" => Signature(height: 240),
            // CharacterOrder and HiddenClients both feed the sort that decides which default-stack
            // slot each preview gets, and a remembered frame outranks the default stack.
            "order" => Signature(characterOrder: new[] { "Pilot One", "Pilot Two" }),
            "hidden" => Signature(hiddenClients: new[] { "Pilot Two" }),
            // The default stack is laid out against the primary screen, so previews that have never
            // been dragged must be recomputed rather than left on stale absolute coordinates.
            "screen" => Signature(primaryScreen: new Rectangle(0, 0, 1920, 1080)),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        Assert.True(frames.ResetIfChanged(changed));
        Assert.Equal(0, frames.Count);
        Assert.False(frames.TryGet(ClientA, out _));
    }

    [Fact]
    public void SignatureFieldsDoNotBleedIntoEachOther()
    {
        Assert.NotEqual(
            Signature(characterOrder: new[] { "A", "B" }),
            Signature(characterOrder: new[] { "A" }, hiddenClients: new[] { "B" }));
    }

    [Fact]
    public void ControlCharactersInAListItemCannotForgeAFieldBoundary()
    {
        // U+001E and U+001F are the separators, and nothing upstream removes them: CleanList only
        // trims whitespace and drops empties and duplicates, SplitLines splits on newlines and
        // commas, and neither filters control characters. A raw separator inside a character name
        // would otherwise let two different configurations produce the same signature, suppressing
        // a reset that should have fired and leaving a stale frame in place.
        Assert.NotEqual(
            Signature(characterOrder: new[] { $"A{ItemSeparator}B" }),
            Signature(characterOrder: new[] { "A", "B" }));

        Assert.NotEqual(
            Signature(characterOrder: new[] { $"A{FieldSeparator}B" }),
            Signature(characterOrder: new[] { "A" }, hiddenClients: new[] { "B" }));
    }

    [Fact]
    public void ControlCharactersInTheProfileIdCannotForgeAFieldBoundary()
    {
        // profile.Id gets no normalization at all on the settings import path.
        Assert.NotEqual(
            Signature(profileId: $"p{FieldSeparator}320x180"),
            Signature(profileId: "p"));
    }

    [Fact]
    public void EscapingKeepsInputsThatDifferOnlyByControlCharactersDistinct()
    {
        // Escaped rather than stripped: stripping would conflate these two, which is the same
        // missed reset by another route.
        Assert.NotEqual(
            Signature(characterOrder: new[] { $"A{(char)0x01}B" }),
            Signature(characterOrder: new[] { "AB" }));

        // And the escape must not be forgeable by text that merely looks like one.
        Assert.NotEqual(
            Signature(characterOrder: new[] { $"A{ItemSeparator}B" }),
            Signature(characterOrder: new[] { "A" + Backslash + "u001FB" }));
    }

    [Fact]
    public void ResetIsIdempotentOnceApplied()
    {
        var frames = Seeded();
        var next = Signature(width: 400);

        Assert.True(frames.ResetIfChanged(next));
        frames.Remember(ClientA, Frame);
        Assert.False(frames.ResetIfChanged(next));
        Assert.Equal(1, frames.Count);
    }

    [Fact]
    public void PruneDropsWindowsThatAreGoneAndKeepsThoseStillPresent()
    {
        var frames = Seeded();

        frames.PruneTo(new[] { ClientA });

        Assert.True(frames.TryGet(ClientA, out _));
        Assert.False(frames.TryGet(ClientB, out _));
    }

    [Fact]
    public void PruneKeepsAClientThatCurrentlyHasNoPreview()
    {
        // With HideActivePreview the focused client's preview state is disposed and recreated on
        // every sync. Pruning is driven by the client list precisely so that the focused client
        // does not lose its held position while it has no preview state.
        var frames = Seeded();

        frames.PruneTo(new[] { ClientA, ClientB });

        Assert.True(frames.TryGet(ClientA, out _));
        Assert.True(frames.TryGet(ClientB, out _));
    }

    [Fact]
    public void PruneDropsAHandleThatCameBackUnderADifferentProcess()
    {
        var frames = Seeded();

        frames.PruneTo(new[] { new PreviewWindowKey(ClientA.Handle, ClientA.ProcessId + 1), ClientB });

        Assert.False(frames.TryGet(ClientA, out _));
        Assert.True(frames.TryGet(ClientB, out _));
    }

    [Fact]
    public void PruneToAnEmptyClientListForgetsEverything()
    {
        var frames = Seeded();

        frames.PruneTo(Array.Empty<PreviewWindowKey>());

        Assert.Equal(0, frames.Count);
    }

    [Fact]
    public void RememberOverwritesThePreviousFrameForTheSameWindow()
    {
        var frames = Seeded();

        frames.Remember(ClientA, OtherFrame);

        Assert.True(frames.TryGet(ClientA, out var frame));
        Assert.Equal(OtherFrame, frame);
    }

    [Fact]
    public void FreeSlotIsTheRequestedOneWhenNothingIsRemembered()
    {
        var frames = new RememberedPreviewFrames();

        Assert.Equal(2, frames.FindFreeSlot(2, ClientA, Slot));
    }

    [Fact]
    public void SlotHeldByAnotherWindowIsSkipped()
    {
        // The repro: three clients default-stack at slots 0, 1 and 2 and all three frames are
        // remembered. The middle client closes; the outer two hold slots 0 and 2 because nothing in
        // the reset signature changed. A new client sorts last, so the visible index puts it at
        // slot 2 - byte for byte the rectangle the third client is still sitting on.
        var frames = new RememberedPreviewFrames();
        frames.Remember(ClientA, Slot(0));
        frames.Remember(ClientB, Slot(2));

        Assert.Equal(3, frames.FindFreeSlot(2, new PreviewWindowKey(11, 102), Slot));
    }

    [Fact]
    public void ProbingSkipsRunsOfHeldSlots()
    {
        var frames = new RememberedPreviewFrames();
        frames.Remember(ClientA, Slot(1));
        frames.Remember(ClientB, Slot(2));
        frames.Remember(new PreviewWindowKey(11, 102), Slot(3));

        Assert.Equal(4, frames.FindFreeSlot(1, new PreviewWindowKey(13, 103), Slot));
    }

    [Fact]
    public void AWindowDoesNotAvoidItsOwnRememberedFrame()
    {
        // Otherwise a client that already occupies its natural slot would be pushed off it every
        // time the slot happened to be recomputed.
        var frames = new RememberedPreviewFrames();
        frames.Remember(ClientA, Slot(2));

        Assert.Equal(2, frames.FindFreeSlot(2, ClientA, Slot));
    }

    [Fact]
    public void OnlyAnExactRectangleMatchCountsAsACollision()
    {
        // Overlap is deliberately not a collision: users place previews so they overlap on purpose,
        // and displacing a newcomer for that would be a change nobody asked for.
        var frames = new RememberedPreviewFrames();
        var overlapping = Slot(2);
        overlapping.Offset(4, 4);
        frames.Remember(ClientA, overlapping);

        Assert.Equal(2, frames.FindFreeSlot(2, ClientB, Slot));
    }

    [Fact]
    public void ProbingGivesUpAndReturnsTheRequestedSlot()
    {
        // A slot generator that returns the same rectangle for every index stands in for the
        // default stack's column wrap, whose arithmetic is not injective. Without the probe limit
        // this would never terminate.
        var frames = new RememberedPreviewFrames();
        frames.Remember(ClientA, Slot(0));

        Assert.Equal(5, frames.FindFreeSlot(5, ClientB, _ => Slot(0)));
    }
}
