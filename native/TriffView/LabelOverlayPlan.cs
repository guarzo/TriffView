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
