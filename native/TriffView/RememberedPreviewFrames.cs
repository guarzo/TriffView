using System.Drawing;
using System.Globalization;
using System.Text;

namespace TriffView;

/// <summary>
/// Identifies a live client window for the purpose of remembering where its preview sat.
///
/// The process id is part of the key because Windows recycles window handles: a client can close
/// and an unrelated one be created on the same HWND between two polls. Keyed on the handle alone,
/// the new client - nameless at character select, so no saved layout matches it - would silently
/// inherit the dead client's rectangle.
/// </summary>
internal readonly record struct PreviewWindowKey(nint Handle, uint ProcessId);

/// <summary>
/// The frame each client window was last shown at, keyed by window rather than by character.
///
/// This is what lets a preview hold its position when its client returns to character select and
/// loses the character name its saved layout is filed under. It deliberately keeps applying to
/// named clients too: a character that has just been selected but has no saved layout of its own
/// must stay where the preview already was rather than jump to the default stack.
///
/// Because a remembered frame outranks the default stack, anything that feeds the default stack -
/// its size, and the ordering that decides which slot a preview gets - has to be able to invalidate
/// the whole memory. That is what <see cref="ComposeSignature"/> and <see cref="ResetIfChanged"/>
/// are for: a single global "forget everything and recompute" when the inputs change. It is not,
/// and must not become, a per-preview test of a position against monitor bounds; the primary screen
/// rectangle enters the signature only as opaque text, never as something a preview is compared to.
/// </summary>
internal sealed class RememberedPreviewFrames
{
    private const char FieldSeparator = '\u001e';
    private const char ItemSeparator = '\u001f';

    private readonly Dictionary<PreviewWindowKey, Rectangle> _frames = new();
    private string _signature = "";

    /// <summary>
    /// How many default-stack slots <see cref="FindFreeSlot"/> will try before giving up. The stack
    /// wraps into columns and its column/row arithmetic is not injective, so there is no guarantee
    /// that a free slot exists at all; the bound stops a pathological state from spinning.
    /// </summary>
    private const int SlotProbeLimit = 32;

    public int Count => _frames.Count;

    /// <summary>
    /// Builds the value that, when it changes, invalidates every remembered frame. Every input to
    /// the default stack belongs here: the profile identity, the preview size, the two profile
    /// lists that drive client ordering and therefore which slot each preview lands in, and the
    /// primary screen rectangle the stack is laid out against.
    /// </summary>
    /// <param name="primaryScreen">
    /// Stringified as-is. The caller resolves it so this class stays free of Windows dependencies.
    /// </param>
    public static string ComposeSignature(
        string profileId,
        int previewWidth,
        int previewHeight,
        IEnumerable<string> characterOrder,
        IEnumerable<string> hiddenClients,
        Rectangle primaryScreen)
    {
        var order = string.Join(ItemSeparator, characterOrder.Select(Escape));
        var hidden = string.Join(ItemSeparator, hiddenClients.Select(Escape));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Escape(profileId)}{FieldSeparator}{previewWidth}x{previewHeight}{FieldSeparator}{order}{FieldSeparator}{hidden}{FieldSeparator}{primaryScreen.X},{primaryScreen.Y},{primaryScreen.Width},{primaryScreen.Height}");
    }

    /// <summary>
    /// Makes the separators unforgeable by the text fed into the signature.
    ///
    /// The separators are the Cc control characters U+001E and U+001F, and nothing upstream removes
    /// them: <c>TriffViewProfile.CleanList</c> only trims whitespace and drops empties and
    /// duplicates, <c>SplitLines</c> splits on newlines and commas, and the settings import path
    /// does not touch <c>profile.Id</c>. A character name carrying a raw separator could therefore
    /// make two genuinely different signatures compare equal, suppressing a reset that should have
    /// fired and leaving a stale frame in place until the next real change.
    ///
    /// Escaping rather than stripping, so that distinct inputs stay distinct: stripping would
    /// conflate a name containing a separator with the same name without it, which is the same
    /// missed reset by another route. Handled here rather than in the shared list normalization,
    /// which has nothing to do with this encoding.
    /// </summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var needsEscaping = false;
        foreach (var ch in value)
        {
            if (!char.IsControl(ch) && ch != '\\') continue;
            needsEscaping = true;
            break;
        }

        if (!needsEscaping) return value;

        var builder = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                builder.Append(@"\\");
            }
            else if (char.IsControl(ch))
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)ch:X4}");
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Forgets every remembered frame when <paramref name="signature"/> differs from the one in
    /// force. Returns whether anything was invalidated.
    /// </summary>
    public bool ResetIfChanged(string signature)
    {
        if (string.Equals(signature, _signature, StringComparison.Ordinal)) return false;
        _signature = signature;
        _frames.Clear();
        return true;
    }

    public void Remember(PreviewWindowKey key, Rectangle frame) => _frames[key] = frame;

    public bool TryGet(PreviewWindowKey key, out Rectangle frame) => _frames.TryGetValue(key, out frame);

    /// <summary>
    /// Picks the default-stack slot a window should be placed in, skipping slots whose rectangle is
    /// already held by a different window.
    ///
    /// The slot a client gets is driven by its position in the visible client list, which none of
    /// the reset signature can capture - client membership deliberately stays out of it, since a
    /// signature that changed on every client open or close would forget every held position. So
    /// slots free up underneath the memory: close the middle client of a default stack of three and
    /// the outer two hold slots 0 and 2 while the next client to start resolves to slot 2 and lands
    /// exactly on top of one of them. Probing forward past held slots is what stops that.
    ///
    /// Collision is exact rectangle equality, not overlap. The only collision this needs to fix is
    /// the machine-generated one above, where the newcomer's slot rectangle is byte-for-byte a
    /// rectangle already in use. Overlap testing would also fire on previews the user has
    /// deliberately placed so they overlap, displacing a newcomer for no reason the user asked for,
    /// and with large previews it can cascade until the probe limit runs out.
    ///
    /// <paramref name="slotRect"/> is supplied by the caller because computing a slot rectangle
    /// needs screen geometry. Note what this compares: a candidate preview rectangle against other
    /// preview rectangles. It never compares a preview against a monitor rectangle - see the
    /// coordinate-spaces section of CLAUDE.md for why that distinction matters here.
    /// </summary>
    /// <param name="slotIndex">The slot the caller would have used.</param>
    /// <param name="requester">The window being placed; its own held frame is never an obstacle.</param>
    /// <returns>
    /// The first free slot at or after <paramref name="slotIndex"/>, or <paramref name="slotIndex"/>
    /// itself if none is found within the probe limit.
    /// </returns>
    public int FindFreeSlot(int slotIndex, PreviewWindowKey requester, Func<int, Rectangle> slotRect)
    {
        if (_frames.Count == 0) return slotIndex;

        for (var probe = 0; probe < SlotProbeLimit; probe++)
        {
            var candidate = slotIndex + probe;
            if (!IsHeldByAnotherWindow(slotRect(candidate), requester)) return candidate;
        }

        return slotIndex;
    }

    private bool IsHeldByAnotherWindow(Rectangle rect, PreviewWindowKey requester)
    {
        foreach (var (key, frame) in _frames)
        {
            if (key.Equals(requester)) continue;
            if (frame == rect) return true;
        }

        return false;
    }

    /// <summary>
    /// Drops frames for windows that are no longer present.
    ///
    /// Callers pass the client list rather than the set of live previews on purpose: with
    /// HideActivePreview the focused client's preview state is disposed and recreated on every
    /// sync, and pruning off preview states would throw its position away exactly when the user
    /// logs off the client they are looking at.
    /// </summary>
    public void PruneTo(IEnumerable<PreviewWindowKey> liveKeys)
    {
        if (_frames.Count == 0) return;

        // Called on every poll tick, and the overwhelmingly common case is that nothing is stale.
        // The removal list is only allocated once something actually has to be removed.
        var live = liveKeys as IReadOnlySet<PreviewWindowKey> ?? liveKeys.ToHashSet();
        List<PreviewWindowKey>? stale = null;
        foreach (var key in _frames.Keys)
        {
            if (live.Contains(key)) continue;
            (stale ??= new List<PreviewWindowKey>()).Add(key);
        }

        if (stale == null) return;

        foreach (var key in stale)
        {
            _frames.Remove(key);
        }
    }
}
