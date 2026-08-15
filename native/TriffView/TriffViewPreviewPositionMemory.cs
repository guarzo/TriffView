using System.Drawing;

namespace TriffView.Preview;

internal readonly record struct PreviewClientIdentity(nint Handle, uint ProcessId)
{
    public static PreviewClientIdentity From(EveClientWindow client) => new(client.Handle, client.ProcessId);
}

internal sealed class TriffViewPreviewPositionMemory
{
    private readonly Dictionary<PreviewClientIdentity, Rectangle> _positions = new();
    private string _profileId = "";
    private int _previewWidth;
    private int _previewHeight;
    private ScreenPixelInfo[] _monitorTopology = Array.Empty<ScreenPixelInfo>();
    private bool _hasContext;

    public bool MatchesContext(
        string profileId,
        int previewWidth,
        int previewHeight,
        IReadOnlyList<ScreenPixelInfo> monitorTopology)
    {
        return _hasContext
            && string.Equals(_profileId, profileId, StringComparison.OrdinalIgnoreCase)
            && _previewWidth == previewWidth
            && _previewHeight == previewHeight
            && _monitorTopology.SequenceEqual(monitorTopology);
    }

    public bool BeginContext(
        string profileId,
        int previewWidth,
        int previewHeight,
        IReadOnlyList<ScreenPixelInfo> monitorTopology)
    {
        var nextTopology = monitorTopology.ToArray();
        var changed = !MatchesContext(profileId, previewWidth, previewHeight, nextTopology);

        if (!changed) return false;

        _positions.Clear();
        _profileId = profileId;
        _previewWidth = previewWidth;
        _previewHeight = previewHeight;
        _monitorTopology = nextTopology;
        _hasContext = true;
        return true;
    }

    public void Remember(PreviewClientIdentity identity, Rectangle position)
    {
        _positions[identity] = position;
    }

    public bool TryGet(PreviewClientIdentity identity, out Rectangle position)
    {
        return _positions.TryGetValue(identity, out position);
    }

    public void PurgeExcept(IReadOnlySet<PreviewClientIdentity> liveIdentities)
    {
        foreach (var identity in _positions.Keys.Where(identity => !liveIdentities.Contains(identity)).ToArray())
        {
            _positions.Remove(identity);
        }
    }

    /// <summary>
    /// How many default-stack slots <see cref="FindFreeSlot"/> will try before giving up. The
    /// stack wraps into columns and its column/row arithmetic is not injective, so there is no
    /// guarantee a free slot exists at all; the bound stops a pathological state from spinning.
    /// </summary>
    private const int SlotProbeLimit = 32;

    /// <summary>
    /// The first default-stack slot at or after <paramref name="slotIndex"/> whose rectangle no
    /// other window is already remembered at.
    ///
    /// The slot index follows the visible client list, which the context signature cannot see, so
    /// a slot can be both "next in line" and still held by a client that outlived a closure
    /// earlier in the stack. Taking it regardless stacks two previews on the same rectangle.
    /// </summary>
    public int FindFreeSlot(int slotIndex, PreviewClientIdentity requester, Func<int, Rectangle> slotRect)
    {
        if (_positions.Count == 0) return slotIndex;

        for (var probe = 0; probe < SlotProbeLimit; probe++)
        {
            var candidate = slotIndex + probe;
            if (!IsHeldByAnother(slotRect(candidate), requester)) return candidate;
        }

        return slotIndex;
    }

    private bool IsHeldByAnother(Rectangle rect, PreviewClientIdentity requester)
    {
        foreach (var (identity, position) in _positions)
        {
            if (identity.Equals(requester)) continue;
            if (position == rect) return true;
        }

        return false;
    }

    public static Rectangle Resolve(
        Rectangle? savedForCurrentKey,
        Rectangle? rememberedForClient,
        Rectangle? titleFallback,
        Rectangle defaultStack)
    {
        return savedForCurrentKey
            ?? rememberedForClient
            ?? titleFallback
            ?? defaultStack;
    }
}
