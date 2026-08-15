# Per-Preview Label Overlay Forms Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single desktop-spanning label overlay form with one small topmost layered form per preview, so label repaints cost ~479 us instead of ~40,000 us and dragging costs no repaints at all.

**Architecture:** A controller owns one small layered form per preview, keyed by window handle. On each `SetItems` a pure, unit-tested planner decides per handle whether to create, move, repaint, or retire. Moving is `SetBounds` only — no invalidation — which is where the win comes from. Each form always repaints in full, because partial repaints do not reach a layered window's composited surface.

**Tech Stack:** C# / .NET 8, WinForms (`net8.0-windows`), xunit for the pure logic.

**Spec:** `docs/superpowers/specs/2026-08-13-per-preview-label-forms-design.md`

## Global Constraints

- **Windows-only.** `native/TriffView.csproj` targets `net8.0-windows` with `UseWPF`/`UseWindowsForms`. It cannot be built or run from WSL. Build via `powershell.exe` using the exact commands below.
- **This worktree is a sibling of the main checkout**, not nested inside it, so `scripts/build-native.ps1` and `tests/TriffView.Tests/run-tests.ps1` will NOT find `.dotnet` by walking upward. Use the explicit commands in this plan, which point at the main checkout's SDK.
- **Never reintroduce partial invalidation** on any label form. `Invalidate(rect)` does not reach a layered (`WS_EX_LAYERED` + `TransparencyKey`) surface; five strategies were measured and all left ghost labels. Every label repaint must be a full-surface `Invalidate()` of one small form.
- **Do not compare preview positions against monitor bounds** anywhere. See `CLAUDE.md`; an earlier attempt to do so moved correctly-placed previews off screen.
- Test project links pure-logic source files via `<Compile Include>`; it is NOT a `ProjectReference`. Anything to be tested must live in its own file and depend only on `System.Drawing.Primitives` and primitives.
- Base branch: `feat/per-preview-label-forms`, based on `fork/main` at v1.6.3.

### Build command

```powershell
powershell.exe -NoProfile -Command "
\$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'
\$env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'
\$env:APPDATA='C:\dev\TriffView\.appdata'
\$env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'
\$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
& 'C:\dev\TriffView\.dotnet\dotnet.exe' build 'C:\dev\tv-fix\native\TriffView.csproj' -c Release
"
```

### Test command (verified working from this worktree — 15 tests currently pass)

```powershell
powershell.exe -NoProfile -Command "
\$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'
\$env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'
\$env:APPDATA='C:\dev\TriffView\.appdata'
\$env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'
\$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
& 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\tv-fix\tests\TriffView.Tests\TriffView.Tests.csproj'
"
```

---

### Task 1: Move the item record into a pure file and give it a handle

The controller is keyed by window handle, but `TriffViewLabelOverlayItem` carries no handle today, so that identity is not implementable. The handle is reachable: `PreviewState.Client` is an `EveClientWindow` and `_previews` is keyed by `client.Handle`.

Moving the record into its own file is also what makes the planner in Task 2 testable — the test project links source files and cannot reference the WinForms project.

**Files:**
- Create: `native/TriffView/LabelOverlayPlan.cs`
- Modify: `native/TriffView/TriffViewSubsystem.cs` (delete record at 3009-3015; update `RefreshLabelOverlay` at ~2638-2650)

**Interfaces:**
- Consumes: nothing
- Produces: `TriffViewLabelOverlayItem(nint Handle, Rectangle Frame, string Text, Color TextColor, int FontSize, string Position, int BorderThickness)` in namespace `TriffView.Preview`

- [ ] **Step 1: Create the new file with the record moved and a handle added**

Create `native/TriffView/LabelOverlayPlan.cs`:

```csharp
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
```

- [ ] **Step 2: Delete the old record declaration**

In `native/TriffView/TriffViewSubsystem.cs`, delete lines 3009-3015 — the whole `internal sealed record TriffViewLabelOverlayItem(...)` declaration. It now lives in `LabelOverlayPlan.cs`. No `using` is needed: both files are in namespace `TriffView.Preview`.

- [ ] **Step 3: Populate the handle at the one construction site**

In `RefreshLabelOverlay`, add the handle as the first constructor argument:

```csharp
        var items = _previews.Values
            .Where(state => state.Visible)
            .Select(state => new TriffViewLabelOverlayItem(
                state.Client.Handle,
                ToClientRect(state.FrameRect),
                _profile.PreviewLabelFor(state.Client),
                ColorFromString(_profile.LabelTextColor, Color.FromArgb(217, 226, 238)),
                Math.Max(8, Math.Min(32, _profile.LabelFontSize)),
                LabelPosition(),
                Math.Max(1, _profile.BorderThickness)
            ))
            .ToArray();
```

- [ ] **Step 4: Build and confirm it compiles**

Run the build command from Global Constraints.
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

If it fails with "no argument given that corresponds to the required parameter 'Handle'", another construction site exists — find it with `grep -n "new TriffViewLabelOverlayItem" native/TriffView/TriffViewSubsystem.cs` and add the handle there too.

- [ ] **Step 5: Commit**

```bash
git add native/TriffView/LabelOverlayPlan.cs native/TriffView/TriffViewSubsystem.cs
git commit -m "Move label overlay item to its own file and add the client handle

The per-preview overlay controller keys its forms by window handle, which the
item record did not carry. The handle comes from state.Client.Handle, the same
identity _previews is keyed by. The record moves to its own file so the planning
logic added next can be unit-tested - the test project links pure source files
rather than referencing the net8.0-windows project."
```

---

### Task 2: Pure planning and geometry logic, test-first

This is the only part that can be unit-tested, and it holds the two rules most likely to be got wrong: that a resize must repaint rather than move, and that a form must be large enough to contain a label that overflows its frame.

**Files:**
- Modify: `native/TriffView/LabelOverlayPlan.cs`
- Modify: `tests/TriffView.Tests/TriffView.Tests.csproj`
- Test: `tests/TriffView.Tests/LabelOverlayPlanTests.cs`

**Interfaces:**
- Consumes: `TriffViewLabelOverlayItem` from Task 1
- Produces:
  - `enum LabelOverlayActionKind { Create, Move, Repaint, Retire }`
  - `record LabelOverlayAction(LabelOverlayActionKind Kind, nint Handle, TriffViewLabelOverlayItem? Item)`
  - `static IReadOnlyList<LabelOverlayAction> LabelOverlayPlan.Compute(IReadOnlyDictionary<nint, TriffViewLabelOverlayItem> previous, IReadOnlyList<TriffViewLabelOverlayItem> current)`
  - `static Rectangle LabelOverlayPlan.FormBounds(TriffViewLabelOverlayItem item, int textHeight)`

- [ ] **Step 1: Add the test file linkage**

In `tests/TriffView.Tests/TriffView.Tests.csproj`, add a second `Compile Include` inside the existing `ItemGroup`:

```xml
  <ItemGroup>
    <Compile Include="..\..\native\TriffView\PreviewPointerGesture.cs" Link="PreviewPointerGesture.cs" />
    <Compile Include="..\..\native\TriffView\LabelOverlayPlan.cs" Link="LabelOverlayPlan.cs" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

Create `tests/TriffView.Tests/LabelOverlayPlanTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run the tests and verify they fail**

Run the test command from Global Constraints.
Expected: FAIL — `LabelOverlayPlan` does not exist. Compilation errors are the expected failure here.

- [ ] **Step 4: Implement the planner**

Append to `native/TriffView/LabelOverlayPlan.cs`:

```csharp
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
```

- [ ] **Step 5: Run the tests and verify they pass**

Run the test command from Global Constraints.
Expected: `Passed! - Failed: 0, Passed: 31` (15 existing + 16 new — the
`AnyContentChangeIsARepaint` theory contributes 4 cases).

If `FormBoundsEqualTheFrameWhenTheLabelFitsInside` fails, the shadow inflation is pushing bounds past the frame on a case where the label fits — check that `LabelRect` is being clamped by `Rectangle.Union` with the frame rather than replacing it.

- [ ] **Step 6: Commit**

```bash
git add native/TriffView/LabelOverlayPlan.cs tests/TriffView.Tests/LabelOverlayPlanTests.cs tests/TriffView.Tests/TriffView.Tests.csproj
git commit -m "Add tested planning logic for per-preview label forms

Decides per preview whether its label form needs creating, moving, repainting,
or retiring. A location-only change is a move, which costs a SetBounds and no
repaint; a size change is a repaint, because the label's width and vertical
placement are derived from the frame's dimensions.

FormBounds exists because a label can overflow its frame: previews may be as
small as 16px while a label is at least 18px tall plus inset. The old
desktop-spanning form absorbed that overflow; a frame-sized form would clip it."
```

---

### Task 3: The per-preview form and its controller

Adds the new classes without wiring them in, so this task can be built and reviewed on its own while the old form still runs the app.

**Files:**
- Modify: `native/TriffView/TriffViewSubsystem.cs` (add two classes near the existing `TriffViewLabelOverlayForm` at ~3017)

**Interfaces:**
- Consumes: `LabelOverlayPlan.Compute`, `LabelOverlayPlan.FormBounds`, `TriffViewLabelOverlayItem`
- Produces: `TriffViewLabelOverlayController` with `Owner`, `SetVirtualDesktop(Rectangle)`, `AllowTopmost` (get/set), `ApplyTopmostPolicy(bool force = false)`, `SetItems(IReadOnlyList<TriffViewLabelOverlayItem>)`, `Dispose()` — deliberately the same surface the existing form exposes, so Task 4 is a swap rather than a rewrite

- [ ] **Step 1: Add the per-item form**

Insert before `internal sealed class TriffViewLabelOverlayForm` in `native/TriffView/TriffViewSubsystem.cs`:

```csharp
/// <summary>
/// One label, on its own small layered window sitting above its preview's DWM thumbnail.
///
/// <para>Window styles are copied from the original desktop-spanning overlay: only the size and
/// number of windows changed, so anything that behaved correctly before still does.</para>
///
/// <para>This form always repaints its whole surface. A partial <c>Invalidate(rect)</c> does not
/// reach a layered window's composited surface - it leaves stale "ghost" text behind - which is
/// exactly why the surface is now small enough that a full repaint is cheap. Do not add bounded
/// invalidation here.</para>
/// </summary>
internal sealed class TriffViewLabelOverlayItemForm : Forms.Form
{
    private static readonly Color TransparentBackColor = Color.FromArgb(1, 2, 3);

    private TriffViewLabelOverlayItem? _item;
    private int _textHeight;
    private bool _allowTopmost = true;

    public TriffViewLabelOverlayItemForm()
    {
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        BackColor = TransparentBackColor;
        TransparencyKey = TransparentBackColor;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9, FontStyle.Regular);
    }

    public bool AllowTopmost
    {
        get => _allowTopmost;
        set => _allowTopmost = value;
    }

    protected override bool ShowWithoutActivation => true;

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= TriffViewNativeMethods.WsExToolWindow
                | TriffViewNativeMethods.WsExNoActivate
                | TriffViewNativeMethods.WsExTransparent
                | TriffViewNativeMethods.WsExLayered;
            if (_allowTopmost) cp.ExStyle |= TriffViewNativeMethods.WsExTopmost;
            return cp;
        }
    }

    protected override void WndProc(ref Forms.Message m)
    {
        if (m.Msg == TriffViewNativeMethods.WmNcHitTest)
        {
            m.Result = TriffViewNativeMethods.HtTransparent;
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// Measures the label the same way the painter does, so the form's bounds and its drawing
    /// cannot disagree about how tall the text is.
    /// </summary>
    public int MeasureTextHeight(TriffViewLabelOverlayItem item)
    {
        var fontSize = Math.Max(8, Math.Min(32, item.FontSize));
        using var labelFont = new Font(Font.FontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Point);
        using var graphics = CreateGraphics();
        return Math.Max(18, (int)Math.Ceiling(labelFont.GetHeight(graphics)) + 8);
    }

    /// <summary>Repositions without repainting. Used when only the preview's location changed.</summary>
    public void MoveTo(TriffViewLabelOverlayItem item, Rectangle screenBounds)
    {
        _item = item;
        if (Bounds != screenBounds) Bounds = screenBounds;
    }

    /// <summary>Repositions and repaints. Used when the size or any drawn property changed.</summary>
    public void SetContent(TriffViewLabelOverlayItem item, Rectangle screenBounds, int textHeight)
    {
        _item = item;
        _textHeight = textHeight;
        if (Bounds != screenBounds) Bounds = screenBounds;
        Invalidate();
    }

    public void ApplyTopmostPolicy(bool force = false)
    {
        if (!force && TopMost == _allowTopmost) return;
        TopMost = _allowTopmost;
        if (!IsHandleCreated) return;

        TriffViewNativeMethods.SetWindowPos(
            Handle,
            _allowTopmost ? TriffViewNativeMethods.HwndTopmost : TriffViewNativeMethods.HwndNotTopmost,
            0,
            0,
            0,
            0,
            TriffViewNativeMethods.SwpNoMove | TriffViewNativeMethods.SwpNoSize | TriffViewNativeMethods.SwpNoActivate
        );
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        e.Graphics.Clear(TransparentBackColor);
        if (_item == null) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // The item's rectangles are in virtual-desktop client space; this form's client area
        // starts at its own bounds, so shift the item so it draws at the right place locally.
        var localFrame = _item.Frame;
        localFrame.Offset(-Bounds.X + _formOriginOffset.X, -Bounds.Y + _formOriginOffset.Y);
        DrawItem(e.Graphics, _item with { Frame = localFrame }, _textHeight);
    }

    /// <summary>
    /// Offset from virtual-desktop client space to screen space, i.e. the virtual desktop origin.
    /// Set by the controller, which owns that value.
    /// </summary>
    private Point _formOriginOffset;

    public void SetVirtualDesktopOrigin(Point origin) => _formOriginOffset = origin;

    private void DrawItem(Graphics graphics, TriffViewLabelOverlayItem item, int textHeight)
    {
        if (string.IsNullOrWhiteSpace(item.Text)) return;

        var fontSize = Math.Max(8, Math.Min(32, item.FontSize));
        using var labelFont = new Font(Font.FontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Point);
        using var textBrush = new SolidBrush(item.TextColor);
        using var shadowBrush = new SolidBrush(Color.FromArgb(205, 0, 0, 0));
        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Center,
        };

        var labelRect = (RectangleF)LabelOverlayPlan.LabelRect(item, textHeight);
        var shadowRect = labelRect;
        shadowRect.Offset(1, 1);
        graphics.DrawString(item.Text, labelFont, shadowBrush, shadowRect, format);
        graphics.DrawString(item.Text, labelFont, textBrush, labelRect, format);
    }
}
```

- [ ] **Step 2: Add the controller**

Insert immediately after the per-item form:

```csharp
/// <summary>
/// Owns one <see cref="TriffViewLabelOverlayItemForm"/> per preview, keyed by the client's
/// window handle - the same identity <c>_previews</c> uses.
///
/// <para>Replaces a single form spanning the whole virtual desktop, whose every repaint covered
/// ~62.6M pixels and cost ~40ms on the UI thread. Dragging a preview changes only the frame's
/// location, which is handled here by moving a window and not repainting at all.</para>
///
/// <para>Forms are pooled rather than destroyed when a client goes away: client open/close
/// cycles are frequent, and creating and destroying real windows each time invites flicker and
/// handle churn.</para>
/// </summary>
internal sealed class TriffViewLabelOverlayController : IDisposable
{
    private readonly Dictionary<nint, TriffViewLabelOverlayItemForm> _forms = new();
    private readonly Dictionary<nint, TriffViewLabelOverlayItem> _rendered = new();
    private readonly Stack<TriffViewLabelOverlayItemForm> _pool = new();

    private Forms.Form? _owner;
    private Rectangle _virtualDesktop;
    private bool _allowTopmost = true;

    public Forms.Form? Owner
    {
        get => _owner;
        set
        {
            _owner = value;
            foreach (var form in _forms.Values) form.Owner = value;
        }
    }

    public bool AllowTopmost
    {
        get => _allowTopmost;
        set
        {
            _allowTopmost = value;
            foreach (var form in _forms.Values) form.AllowTopmost = value;
        }
    }

    public void SetVirtualDesktop(Rectangle virtualDesktop)
    {
        if (_virtualDesktop == virtualDesktop) return;
        _virtualDesktop = virtualDesktop;

        // Every form's screen position is derived from this origin, so they all have to move.
        foreach (var (handle, form) in _forms)
        {
            if (!_rendered.TryGetValue(handle, out var item)) continue;
            form.SetVirtualDesktopOrigin(_virtualDesktop.Location);
            form.MoveTo(item, ToScreen(LabelOverlayPlan.FormBounds(item, form.MeasureTextHeight(item))));
        }
    }

    public void ApplyTopmostPolicy(bool force = false)
    {
        foreach (var form in _forms.Values) form.ApplyTopmostPolicy(force);
    }

    public void SetItems(IReadOnlyList<TriffViewLabelOverlayItem> items)
    {
        foreach (var action in LabelOverlayPlan.Compute(_rendered, items))
        {
            switch (action.Kind)
            {
                case LabelOverlayActionKind.Create:
                    Create(action.Item!);
                    break;
                case LabelOverlayActionKind.Move:
                    Move(action.Item!);
                    break;
                case LabelOverlayActionKind.Repaint:
                    Repaint(action.Item!);
                    break;
                case LabelOverlayActionKind.Retire:
                    Retire(action.Handle);
                    break;
            }
        }
    }

    private void Create(TriffViewLabelOverlayItem item)
    {
        var form = _pool.Count > 0 ? _pool.Pop() : new TriffViewLabelOverlayItemForm();
        form.Owner = _owner;
        form.AllowTopmost = _allowTopmost;
        form.SetVirtualDesktopOrigin(_virtualDesktop.Location);
        _forms[item.Handle] = form;

        var textHeight = form.MeasureTextHeight(item);
        form.SetContent(item, ToScreen(LabelOverlayPlan.FormBounds(item, textHeight)), textHeight);
        _rendered[item.Handle] = item;

        if (!form.Visible) form.Show();
        form.ApplyTopmostPolicy(force: true);
    }

    private void Move(TriffViewLabelOverlayItem item)
    {
        if (!_forms.TryGetValue(item.Handle, out var form)) return;
        form.MoveTo(item, ToScreen(LabelOverlayPlan.FormBounds(item, form.MeasureTextHeight(item))));
        _rendered[item.Handle] = item;
    }

    private void Repaint(TriffViewLabelOverlayItem item)
    {
        if (!_forms.TryGetValue(item.Handle, out var form)) return;
        var textHeight = form.MeasureTextHeight(item);
        form.SetContent(item, ToScreen(LabelOverlayPlan.FormBounds(item, textHeight)), textHeight);
        _rendered[item.Handle] = item;
    }

    private void Retire(nint handle)
    {
        if (!_forms.Remove(handle, out var form)) return;
        _rendered.Remove(handle);
        form.Hide();
        _pool.Push(form);
    }

    private Rectangle ToScreen(Rectangle clientRect)
    {
        var screen = clientRect;
        screen.Offset(_virtualDesktop.Left, _virtualDesktop.Top);
        return screen;
    }

    public void Dispose()
    {
        foreach (var form in _forms.Values) form.Dispose();
        _forms.Clear();
        _rendered.Clear();
        while (_pool.Count > 0) _pool.Pop().Dispose();
    }
}
```

- [ ] **Step 3: Build and confirm it compiles**

Run the build command from Global Constraints.
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

Every native constant used above was verified present in `TriffViewNativeMethods`
before this plan was written: `WsExToolWindow`, `WsExNoActivate`,
`WsExTransparent`, `WsExLayered`, `WsExTopmost`, `SwpNoMove`, `SwpNoSize`,
`SwpNoActivate`, `WmNcHitTest` are `public const int`, while `HtTransparent`
(`:4103`), `HwndTopmost` (`:4124`) and `HwndNotTopmost` (`:4125`) are
`public static readonly nint`. The distinction only matters if you try to use the
latter three in a `const` context; used as above, exactly as the existing code
uses them at `:2157`, `:3056` and `:3080`, they are fine.

- [ ] **Step 4: Commit**

```bash
git add native/TriffView/TriffViewSubsystem.cs
git commit -m "Add per-preview label overlay forms and their controller

One small layered form per preview, keyed by client window handle, replacing a
single form spanning the whole virtual desktop. Each form always repaints its
whole surface - partial repaints do not reach a layered window's composited
surface - but the surface is now small enough that this is cheap.

Forms are pooled rather than destroyed so client open/close cycles do not churn
real windows. Not yet wired in; the existing overlay still runs the app."
```

---

### Task 4: Swap the controller in and delete the old form

**Files:**
- Modify: `native/TriffView/TriffViewSubsystem.cs` (field at 2073; call sites at 2105, 2143, 2164-2165, 2410, 2422, 2438, 2634, 2650; delete class at 3017-3184)

**Interfaces:**
- Consumes: `TriffViewLabelOverlayController` from Task 3
- Produces: nothing new

- [ ] **Step 1: Point the field at the controller**

Change line 2073 from:

```csharp
    private readonly TriffViewLabelOverlayForm _labelOverlay = new();
```

to:

```csharp
    private readonly TriffViewLabelOverlayController _labelOverlay = new();
```

Every call site (`Owner`, `SetVirtualDesktop`, `AllowTopmost`, `ApplyTopmostPolicy`, `SetItems`, `Dispose`) keeps the same shape, because the controller was given the same surface deliberately.

- [ ] **Step 2: Delete the old form class**

Delete `internal sealed class TriffViewLabelOverlayForm` in its entirety — from `internal sealed class TriffViewLabelOverlayForm : Forms.Form` at ~3017 through its closing brace immediately before `internal sealed class EveWindowTracker`.

Deleting it rather than leaving it behind a flag is deliberate: keeping it would mean shipping a known-slow implementation plus a switch nobody sets, and it is exactly the code a future contributor might resurrect.

- [ ] **Step 3: Build and confirm it compiles**

Run the build command from Global Constraints.
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

If it fails with "does not contain a definition for 'Owner'", the controller's `Owner` property type does not match the call site at 2105 (`_labelOverlay.Owner = this;` where `this` is `TriffViewOverlayForm`). The property is typed `Forms.Form?`, which accepts it.

- [ ] **Step 4: Confirm no references to the deleted class remain**

Run: `grep -n "TriffViewLabelOverlayForm" native/TriffView/TriffViewSubsystem.cs`
Expected: no output. `TriffViewLabelOverlayItemForm` is a different name and will not match a whole-word search for the old one — if the grep does return that, confirm by eye that only the new class remains.

- [ ] **Step 5: Run the tests**

Run the test command from Global Constraints.
Expected: `Passed! - Failed: 0, Passed: 31`

- [ ] **Step 6: Commit**

```bash
git add native/TriffView/TriffViewSubsystem.cs
git commit -m "Replace the desktop-spanning label overlay with per-preview forms

The single form covering the whole virtual desktop is gone. Its every repaint
covered ~62.6M pixels at ~40ms on the UI thread, and it could not be optimised
in place: partial repaints do not reach a layered window's composited surface,
so bounded invalidation left ghost labels behind.

Measured on the prototype: dragging a preview produced 960 window moves and 0
repaints, against 4 full-desktop repaints before."
```

---

### Task 5: Verification on Windows with real clients

Nothing above proves the app behaves correctly — the plan's unit tests cover only the planning logic, and window behaviour cannot be unit-tested. This task cannot be performed from WSL and requires the user.

**Files:** none

- [ ] **Step 1: Produce a build the user can run**

Run the build command from Global Constraints, then tell the user to launch:

`C:\dev\tv-fix\native\bin\Release\net8.0-windows\TriffView.exe`

They must close any other running TriffView first — a single-instance mutex will otherwise block it. Their profile must have `labelBackgroundTransparent` enabled, or none of this code runs at all.

- [ ] **Step 2: Resize test — the one with no prior evidence**

Ask the user to resize a preview, including down to its minimum size, and report whether the label re-lays-out correctly and is never clipped.

This is the highest-value check in the plan. The prototype that validated this approach produced 960 move-only operations and 0 repaints, but every one came from *dragging* — it never resized a preview once. The resize branch is reasoned from the code, not measured.

- [ ] **Step 3: Drag test**

Ask the user to drag a preview and report whether any stale "ghost" label remains at the old position, and whether the label keeps up with the mouse.
Expected: no ghost, and no lag — dragging should now produce zero repaints.

- [ ] **Step 4: Client lifecycle test**

Ask the user to close and reopen EVE clients several times, and confirm labels disappear and reappear correctly.

Then check handle counts are bounded, not growing:

```bash
powershell.exe -NoProfile -Command "\$p = Get-Process TriffView; \$p.HandleCount"
```

Expected: stable across cycles. The prototype's standing cost was +12 GDI / +5 USER for five forms; growth per cycle instead means pooling is not working.

- [ ] **Step 5: Z-order test**

Ask the user to switch rapidly between clients for ~45 seconds and report any label flickering, dropping behind its thumbnail, or appearing over the wrong preview.

Expected: none. No problems appeared across 50s of prototype testing, including with OBS running, but that is one session's evidence and the failure would be intermittent.

- [ ] **Step 6: Report results honestly**

Write up what was actually observed, including anything not run. If any check fails, stop and report rather than iterating toward a green result — the failure is the finding.

---

## Self-Review

**Spec coverage:**

| Spec requirement | Task |
|---|---|
| One small form per preview | 3 |
| Keyed by window handle; handle added to item contract | 1 |
| Copy window styles verbatim | 3, Step 1 |
| Form sized to union of frame and label rect | 2 (`FormBounds`), 3 |
| Move on location-only change, repaint on size change | 2 (planner), 3 (controller) |
| Always full-surface repaint, never partial | 3 (enforced by comment + no bounded API) |
| Forms pooled, not destroyed | 3 (`_pool`) |
| `_suppressLabelOverlay` hides all | 4 — existing call site passes an empty list, which retires every form |
| Virtual desktop change repositions all | 3 (`SetVirtualDesktop`) |
| Topmost policy per form | 3 (`ApplyTopmostPolicy`) |
| Empty item list hides all | 3 (retire path) |
| Disposal tears down all, pool included | 3 (`Dispose`) |
| Non-transparent path untouched | not modified anywhere — `RefreshLabelOverlay` still returns early |
| Delete old form class | 4 |
| Extract diff decision for unit testing | 2 |
| Resize test before shipping | 5, Step 2 |

**Placeholder scan:** No TBD/TODO. Every code step carries real code. Every command is one verified to run from this worktree.

**Type consistency:** `TriffViewLabelOverlayItem` gains `Handle` in Task 1 and is used with that shape in Tasks 2 and 3. `LabelOverlayPlan.Compute` / `FormBounds` / `LabelRect` are defined in Task 2 and called with matching signatures in Task 3. The controller's public surface in Task 3 matches every call site listed in Task 4.

**One known wrinkle:** `TriffViewLabelOverlayItemForm.OnPaint` shifts the item into form-local space using `Bounds` and the virtual desktop origin. That arithmetic is the single most likely thing to be subtly wrong, and it will show up as labels drawn at an offset rather than as a crash. Task 5 Step 2 and Step 3 will catch it.
