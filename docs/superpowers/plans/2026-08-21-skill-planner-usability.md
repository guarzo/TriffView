# Skill Planner Usability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Skill Planner's plans-by-characters glyph grid with a plan-first roster that stays usable at 40 characters, and add single-plan clipboard import/export.

**Architecture:** Plans move into the left rail (7 items fit); the wide pane holds the selected plan's roster, grouped by readiness with pins, groups, and a filter box. Clipboard access is native on both sides, injected into `TriffSkillsController` as delegates. Pins, groups, and the selected plan persist in the existing TriffSkills `state.json`. `TriffSkillsMatrix.BuildCompact` is untouched — the new UI consumes the same payload the grid did.

**Tech Stack:** C# / .NET 8 (`net8.0-windows` for the app, plain `net8.0` for the cross-platform test project), WPF host + WebView2, React 18 + TypeScript + Vite.

**Spec:** `docs/superpowers/specs/2026-08-21-skill-planner-usability-design.md` (committed at `da17283`)

## Global Constraints

- **Windows only.** This repo cannot be built or run from WSL. Use the explicit-SDK invocation below; `./scripts/build-native.ps1` does **not** work from a worktree (`build-native.ps1:4` resolves the root exactly one level up).
- **Persistence invariants live in `Normalize()`**, not at call sites. It runs on both load and save.
- **Never hard-code a real PID** in any test; exercise seams, not real system resources.
- **`native/TriffView.Tests`** is the target for anything needing the real project or `internal` access. `tests/TriffView.Tests` links sources with `<Compile Include>` and must stay Windows-free — no TriffSkills work belongs there.
- **Do not touch `TriffSkillsMatrix.BuildCompact`.** `MatrixBoundsTests` must keep passing untouched as proof.
- **Do not add logic that compares preview positions to monitor bounds.** Unrelated to this work, but it is the repo's standing trap.
- **Baseline before any change:** `tests/TriffView.Tests` 182 passed; `native/TriffView.Tests` 318 passed.

### The build and test invocation

Every `Run:` step in this plan that builds or tests uses this form. Copy it verbatim; the env vars point at the **main checkout's** caches and only the csproj path changes.

```powershell
powershell.exe -NoProfile -Command "
$env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'
$env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'
$env:APPDATA='C:\dev\TriffView\.appdata'
$env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
& 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\skills-planner-usability\native\TriffView.Tests\TriffView.Tests.csproj' -c Release --nologo --filter FullyQualifiedName~<TestName>
"
```

Overriding `APPDATA` redirects anything else in that shell — do runtime file work in a separate call.

### Honest verification limits

The web tasks (8-13) have **no automated test coverage**: `app/package.json` has no test script and no test dependency, and this plan does not add one. Their verification is `npm run build` plus the named manual checks, which must be run on Windows. Do not report a web task as verified on the strength of a successful build alone.

---

## File Structure

**Native — modified**

| File | Responsibility after this change |
|---|---|
| `native/TriffSkills/TriffSkillsState.cs` | Adds `PinnedCharacterIds`, `CharacterGroups`, `SelectedPlanName`, their `Normalize()` invariants, and the deep-snapshot helpers. Loses `TryReorderCharacters`. |
| `native/TriffSkills/TriffSkillsAuthentication.cs` | Forget-character rollback extended to restore pins and groups. |
| `native/TriffSkills/TriffSkillsController.cs` | Clipboard delegates; `copy-plan`, `import-clipboard`, `set-pinned`, `save-group`, `delete-group`, `select-plan` handlers; state projection gains three fields. Loses `ReorderCharacters`. |
| `native/TriffSkills/PlanImportWorkflow.cs` | `Commit` accepts a validated name override. |
| `native/MainWindow.xaml.cs` | Supplies the two clipboard delegates when constructing the controller. |

**Native — created**

| File | Responsibility |
|---|---|
| `native/TriffSkills/ClipboardPlanText.cs` | Pure helpers: split a leading `#` title line off clipboard text, and prepend one for export. No Windows dependencies, no state. |

**Web — created**

| File | Responsibility |
|---|---|
| `app/src/tools/skills/rosterOrdering.ts` | Pure: group characters by readiness, apply pins/filter/group chips, order Missing fewest-first. No React. |
| `app/src/tools/skills/PlanRail.tsx` | The left rail's plan list with ready ratios. |
| `app/src/tools/skills/CharacterRow.tsx` | One roster row plus its expansion: requirements, forget, re-auth, pin, group membership. |
| `app/src/tools/skills/ImportClipboardDialog.tsx` | The clipboard import confirm dialog. |

**Web — modified**

| File | Responsibility after this change |
|---|---|
| `app/src/tools/TriffSkills.tsx` | Loses the matrix table, `DetailPanel`, and drag handlers; gains the rail + roster composition and the two tabs. |
| `app/src/tools/TriffSkills.css` | Loses matrix/table rules; gains rail, roster, row, and chip rules. |

---

## Task 1: Persisted pins, groups, and selected plan

**Files:**
- Modify: `native/TriffSkills/TriffSkillsState.cs`
- Test: `native/TriffView.Tests/TriffSkillsStateTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `CharacterGroup` (class with `string Name`, `List<long> CharacterIds`, `CharacterGroup Clone()`); on `TriffSkillsState` — `List<long> PinnedCharacterIds`, `List<CharacterGroup> CharacterGroups`, `string SelectedPlanName`, `List<long> SnapshotPins()`, `List<CharacterGroup> SnapshotGroups()`, and the constants `MaxGroups = 20`, `MaxGroupNameLength = 32`.

- [ ] **Step 1: Write the failing tests**

Append to `native/TriffView.Tests/TriffSkillsStateTests.cs`:

```csharp
    [Fact]
    public void EmptyGroupSurvivesSaveAndReload()
    {
        var state = new TriffSkillsState();
        state.Upsert(9001).CharacterName = "Pilot";
        state.CharacterGroups.Add(new CharacterGroup { Name = "Haulers" });

        Assert.True(state.TrySave(out var error), error);

        var loaded = TriffSkillsState.Load();
        var group = Assert.Single(loaded.State.CharacterGroups);
        Assert.Equal("Haulers", group.Name);
        Assert.Empty(group.CharacterIds);
    }

    [Fact]
    public void NormalizeDropsPinsAndMembershipsForUnknownCharacters()
    {
        var state = new TriffSkillsState();
        state.Upsert(9001).CharacterName = "Pilot";
        state.PinnedCharacterIds.Add(9001);
        state.PinnedCharacterIds.Add(9002);
        state.CharacterGroups.Add(new CharacterGroup { Name = "Mains", CharacterIds = [9001, 9002] });

        state.Normalize();

        Assert.Equal([9001L], state.PinnedCharacterIds);
        Assert.Equal([9001L], Assert.Single(state.CharacterGroups).CharacterIds);
    }

    [Fact]
    public void NormalizeDeduplicatesGroupNamesCaseInsensitivelyKeepingTheFirst()
    {
        var state = new TriffSkillsState();
        state.Upsert(9001).CharacterName = "Pilot";
        state.CharacterGroups.Add(new CharacterGroup { Name = "Haulers", CharacterIds = [9001] });
        state.CharacterGroups.Add(new CharacterGroup { Name = "HAULERS" });

        state.Normalize();

        var group = Assert.Single(state.CharacterGroups);
        Assert.Equal("Haulers", group.Name);
        Assert.Equal([9001L], group.CharacterIds);
    }

    [Fact]
    public void SnapshotsAreIndependentOfLaterMutation()
    {
        var state = new TriffSkillsState();
        state.Upsert(9001).CharacterName = "Pilot";
        state.PinnedCharacterIds.Add(9001);
        state.CharacterGroups.Add(new CharacterGroup { Name = "Mains", CharacterIds = [9001] });

        var pins = state.SnapshotPins();
        var groups = state.SnapshotGroups();

        state.PinnedCharacterIds.Clear();
        state.CharacterGroups[0].Name = "Renamed";
        state.CharacterGroups[0].CharacterIds.Clear();

        Assert.Equal([9001L], pins);
        Assert.Equal("Mains", groups[0].Name);
        Assert.Equal([9001L], groups[0].CharacterIds);
    }
```

`SnapshotsAreIndependentOfLaterMutation` is the one that matters most: it fails against a reference capture and passes only against a real copy. That distinction is what three review rounds converged on.

- [ ] **Step 2: Run the tests to verify they fail**

Run the invocation above with `--filter FullyQualifiedName~TriffSkillsStateTests`.
Expected: FAIL — `CharacterGroup` does not exist, so the test project does not compile.

- [ ] **Step 3: Add the group type and the three properties**

In `native/TriffSkills/TriffSkillsState.cs`, add above `internal sealed class TriffSkillsState`:

```csharp
internal sealed class CharacterGroup
{
    public string Name { get; set; } = string.Empty;
    public List<long> CharacterIds { get; set; } = [];

    public CharacterGroup Clone() => new()
    {
        Name = Name,
        CharacterIds = [.. CharacterIds],
    };
}
```

Inside `TriffSkillsState`, beside `SelectedCharacterId`:

```csharp
    public const int MaxGroups = 20;
    public const int MaxGroupNameLength = 32;
    private const int MaxSelectedPlanNameLength = 120;

    public List<long> PinnedCharacterIds { get; set; } = [];
    public List<CharacterGroup> CharacterGroups { get; set; } = [];
    public string SelectedPlanName { get; set; } = string.Empty;

    public List<long> SnapshotPins() => [.. PinnedCharacterIds];

    public List<CharacterGroup> SnapshotGroups() => CharacterGroups.Select(group => group.Clone()).ToList();
```

- [ ] **Step 4: Add the Normalize invariants**

In `Normalize()`, immediately after `Characters = deduped.Values.Take(MaxCharacters).ToList();` and before the `SelectedCharacterId` line:

```csharp
        var known = Characters.Select(character => character.CharacterId).ToHashSet();

        PinnedCharacterIds = (PinnedCharacterIds ?? [])
            .Where(known.Contains)
            .Distinct()
            .Take(MaxCharacters)
            .ToList();

        var seenGroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedGroups = new List<CharacterGroup>();
        foreach (var group in (CharacterGroups ?? []).Take(MaxGroups * 2))
        {
            if (group is null) continue;
            var name = (group.Name ?? string.Empty).Trim();
            if (name.Length == 0) continue;
            if (name.Length > MaxGroupNameLength) name = name[..MaxGroupNameLength];
            if (!seenGroupNames.Add(name)) continue;

            // An empty group is valid: a group is created before anyone is put in
            // it, and TrySave normalizes before it writes.
            normalizedGroups.Add(new CharacterGroup
            {
                Name = name,
                CharacterIds = (group.CharacterIds ?? []).Where(known.Contains).Distinct().Take(MaxCharacters).ToList(),
            });
            if (normalizedGroups.Count >= MaxGroups) break;
        }
        CharacterGroups = normalizedGroups;

        SelectedPlanName = (SelectedPlanName ?? string.Empty).Trim();
        if (SelectedPlanName.Length > MaxSelectedPlanNameLength) SelectedPlanName = string.Empty;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: same filter as Step 2.
Expected: PASS, and the pre-existing `TriffSkillsStateTests` still pass.

- [ ] **Step 6: Run the whole native suite**

Run the invocation with no `--filter`.
Expected: 318 + 4 = 322 passed, 0 failed.

- [ ] **Step 7: Commit**

```bash
git add native/TriffSkills/TriffSkillsState.cs native/TriffView.Tests/TriffSkillsStateTests.cs
git commit -m "feat(triffskills): persist character pins, groups, and the selected plan"
```

---

## Task 2: Forget-character rollback restores pins and groups

**Files:**
- Modify: `native/TriffSkills/TriffSkillsAuthentication.cs:86-104`
- Test: `native/TriffView.Tests/TriffSkillsStateTests.cs`

**Interfaces:**
- Consumes: `SnapshotPins()`, `SnapshotGroups()` from Task 1.
- Produces: nothing new.

Without this, a forget whose save fails restores the character but leaves its pins and group memberships already stripped by `Normalize()` — silent partial loss on a path that reports successful rollback.

- [ ] **Step 1: Write the failing test**

Append to `native/TriffView.Tests/TriffSkillsStateTests.cs`:

```csharp
    [Fact]
    public void NormalizeDoesNotStripPinsWhenTheCharacterIsRestored()
    {
        // Models the forget rollback: Normalize() has already dropped the pin for
        // the removed character, and the rollback must put both back together.
        var state = new TriffSkillsState();
        state.Upsert(9001).CharacterName = "Pilot";
        state.PinnedCharacterIds.Add(9001);
        state.CharacterGroups.Add(new CharacterGroup { Name = "Mains", CharacterIds = [9001] });

        var previousCharacter = state.Characters[0].Clone();
        var previousPins = state.SnapshotPins();
        var previousGroups = state.SnapshotGroups();

        state.Characters.RemoveAt(0);
        state.Normalize();
        Assert.Empty(state.PinnedCharacterIds);

        state.Characters.Insert(0, previousCharacter);
        state.PinnedCharacterIds = previousPins;
        state.CharacterGroups = previousGroups;
        state.Normalize();

        Assert.Equal([9001L], state.PinnedCharacterIds);
        Assert.Equal([9001L], Assert.Single(state.CharacterGroups).CharacterIds);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run with `--filter FullyQualifiedName~NormalizeDoesNotStripPinsWhenTheCharacterIsRestored`.
Expected: PASS immediately — this test documents the state-layer contract Task 1 already provides. If it fails, Task 1's `Normalize()` is wrong; fix that before continuing.

- [ ] **Step 3: Extend the rollback**

In `native/TriffSkills/TriffSkillsAuthentication.cs`, in the forget path, beside `var previousSelection = _state.SelectedCharacterId;`:

```csharp
            var previousPins = _state.SnapshotPins();
            var previousGroups = _state.SnapshotGroups();
```

and inside the `if (saveError is not null)` block, beside `_state.SelectedCharacterId = previousSelection;`:

```csharp
                _state.PinnedCharacterIds = previousPins;
                _state.CharacterGroups = previousGroups;
```

- [ ] **Step 4: Run the whole native suite**

Expected: 323 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add native/TriffSkills/TriffSkillsAuthentication.cs native/TriffView.Tests/TriffSkillsStateTests.cs
git commit -m "fix(triffskills): restore pins and groups when a forget rolls back"
```

---

## Task 3: Clipboard title-line helpers

**Files:**
- Create: `native/TriffSkills/ClipboardPlanText.cs`
- Test: `native/TriffView.Tests/ClipboardPlanTextTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static class ClipboardPlanText` with
  `static (string Name, string Contents) SplitTitle(string? clipboardText)` and
  `static string WithTitle(string planName, string fileContents)`.

- [ ] **Step 1: Write the failing tests**

Create `native/TriffView.Tests/ClipboardPlanTextTests.cs`:

```csharp
using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class ClipboardPlanTextTests
{
    [Fact]
    public void LeadingCommentBecomesTheNameAndIsRemoved()
    {
        var (name, contents) = ClipboardPlanText.SplitTitle("# Mastadon\r\nCaldari Industrial V\r\n");

        Assert.Equal("Mastadon", name);
        Assert.DoesNotContain("#", contents, System.StringComparison.Ordinal);
        Assert.Contains("Caldari Industrial V", contents, System.StringComparison.Ordinal);
    }

    [Fact]
    public void LeadingRequirementIsNotTreatedAsAName()
    {
        var (name, contents) = ClipboardPlanText.SplitTitle("Caldari Industrial V\nTransport Ships I\n");

        Assert.Equal(string.Empty, name);
        Assert.Contains("Caldari Industrial V", contents, System.StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedLeadingRequirementIsLeftForTheParserToReport()
    {
        // The whole point of requiring "#": a typo'd skill must stay a
        // requirement so the parser reports it, rather than becoming the name.
        var (name, contents) = ClipboardPlanText.SplitTitle("Caldari Battleshp Q\nTransport Ships I\n");

        Assert.Equal(string.Empty, name);
        Assert.Contains("Caldari Battleshp Q", contents, System.StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheLeadingCommentIsRemoved()
    {
        var (name, contents) = ClipboardPlanText.SplitTitle("# Mastadon\nCaldari Industrial V\n# keep me\n");

        Assert.Equal("Mastadon", name);
        Assert.Contains("# keep me", contents, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BlankLinesBeforeTheTitleAreTolerated()
    {
        var (name, _) = ClipboardPlanText.SplitTitle("\n\n#   Mastadon   \nCaldari Industrial V\n");

        Assert.Equal("Mastadon", name);
    }

    [Fact]
    public void EmptyClipboardYieldsNoNameAndNoContents()
    {
        var (name, contents) = ClipboardPlanText.SplitTitle(null);

        Assert.Equal(string.Empty, name);
        Assert.Equal(string.Empty, contents);
    }

    [Fact]
    public void WithTitleRoundTripsThroughSplitTitle()
    {
        var exported = ClipboardPlanText.WithTitle("Mastadon", "Caldari Industrial V\r\nTransport Ships I\r\n");
        var (name, contents) = ClipboardPlanText.SplitTitle(exported);

        Assert.Equal("Mastadon", name);
        Assert.Contains("Caldari Industrial V", contents, System.StringComparison.Ordinal);
        Assert.DoesNotContain("# Mastadon", contents, System.StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run with `--filter FullyQualifiedName~ClipboardPlanTextTests`.
Expected: FAIL — `ClipboardPlanText` does not exist.

- [ ] **Step 3: Implement**

Create `native/TriffSkills/ClipboardPlanText.cs`:

```csharp
namespace TriffView.TriffSkills;

/// <summary>
/// Moves a plan's name through the clipboard as a leading "# name" comment.
/// Plan files on disk hold requirement lines only — the name lives in the
/// filename — so export prepends the title and import strips it again. Leaving
/// it in would persist the comment into the saved file, and each later export
/// would prepend another.
/// </summary>
internal static class ClipboardPlanText
{
    public static (string Name, string Contents) SplitTitle(string? clipboardText)
    {
        if (string.IsNullOrEmpty(clipboardText)) return (string.Empty, string.Empty);

        var lines = clipboardText.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith('#')) break;

            var name = line[1..].Trim();
            if (name.Length == 0) break;

            var remainder = string.Join('\n', lines.Skip(index + 1));
            return (name, remainder);
        }

        return (string.Empty, clipboardText);
    }

    public static string WithTitle(string planName, string fileContents)
        => $"# {planName}\r\n{fileContents}";
}
```

- [ ] **Step 4: Run to verify pass**

Expected: 7 passed.

- [ ] **Step 5: Commit**

```bash
git add native/TriffSkills/ClipboardPlanText.cs native/TriffView.Tests/ClipboardPlanTextTests.cs
git commit -m "feat(triffskills): carry a plan name through the clipboard as a title comment"
```

---

## Task 4: Clipboard delegates and `copy-plan`

**Files:**
- Modify: `native/TriffSkills/TriffSkillsController.cs`
- Modify: `native/MainWindow.xaml.cs:684`
- Test: `native/TriffView.Tests/ClipboardControllerTests.cs` (create)

**Interfaces:**
- Consumes: `ClipboardPlanText.WithTitle` from Task 3.
- Produces: the internal controller constructor gains two trailing optional parameters, `Func<string>? readClipboard = null, Action<string>? writeClipboard = null`; a public constructor overload `TriffSkillsController(Action<object> post, Func<string> readClipboard, Action<string> writeClipboard)`.

`CopyText` is private to `MainWindow` (`native/MainWindow.xaml.cs:900-910`), and a handled `triffskills:` message returns before the global `copy-text` switch (`:680-703`) — the controller can reach neither, so clipboard access must be injected.

- [ ] **Step 1: Write the failing test**

Create `native/TriffView.Tests/ClipboardControllerTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class ClipboardControllerTests : IDisposable
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(5);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "triffskills-clipboard", Guid.NewGuid().ToString("N"));

    public ClipboardControllerTests() => TriffSkillsPaths.OverrideRoot(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        finally { TriffSkillsPaths.ClearOverride(); }
    }

    [Fact]
    public void CopyPlanWritesTheFileContentsBehindATitleLine()
    {
        var written = new List<string>();
        using var controller = Controller(() => string.Empty, written.Add, new ConcurrentQueue<string>());

        // PlanStore.EnsureSeeded created "Core Ship Skills" during construction.
        controller.HandleWebMessage("triffskills:copy-plan", new JsonObject { ["planName"] = PlanStore.StarterPlanName });

        var text = Assert.Single(written);
        Assert.StartsWith($"# {PlanStore.StarterPlanName}", text, StringComparison.Ordinal);
        Assert.Contains("CPU Management IV", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyPlanReportsAnErrorForAnUnknownPlan()
    {
        var written = new List<string>();
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(() => string.Empty, written.Add, messages);

        controller.HandleWebMessage("triffskills:copy-plan", new JsonObject { ["planName"] = "No Such Plan" });

        Assert.Empty(written);
        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("triffskills:error", StringComparison.Ordinal) && json.Contains("copy-plan", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    [Fact]
    public void CopyPlanReportsAnErrorWhenTheClipboardWriteThrows()
    {
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(() => string.Empty, _ => throw new InvalidOperationException("clipboard busy"), messages);

        controller.HandleWebMessage("triffskills:copy-plan", new JsonObject { ["planName"] = PlanStore.StarterPlanName });

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("triffskills:error", StringComparison.Ordinal) && json.Contains("copy-plan", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    private static TriffSkillsController Controller(
        Func<string> readClipboard,
        Action<string> writeClipboard,
        ConcurrentQueue<string> messages)
        => new(
            value => messages.Enqueue(JsonSerializer.Serialize(value)),
            new MemoryCredentials(),
            new EsiClient(new HttpClient(new StubHandler()), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }, "TriffView.Tests/1.0"),
            new ControlledSso(),
            TimeProvider.System,
            saveState: () => null,
            readClipboard: readClipboard,
            writeClipboard: writeClipboard);

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }
}
```

`MemoryCredentials` and `ControlledSso` already exist in `native/TriffView.Tests/ControllerLifecycleTests.cs`. If they are declared `private` nested there, promote them to internal top-level types in a shared file in the same task and note it in the commit.

- [ ] **Step 2: Run to verify failure**

Run with `--filter FullyQualifiedName~ClipboardControllerTests`.
Expected: FAIL — the constructor has no `readClipboard`/`writeClipboard` parameters.

- [ ] **Step 3: Add the delegates to the controller**

In `native/TriffSkills/TriffSkillsController.cs`, add fields beside `_saveState`:

```csharp
    private readonly Func<string> _readClipboard;
    private readonly Action<string> _writeClipboard;
```

Replace the public constructor and extend the internal one:

```csharp
    public TriffSkillsController(Action<object> post)
        : this(post, () => string.Empty, _ => { })
    {
    }

    public TriffSkillsController(Action<object> post, Func<string> readClipboard, Action<string> writeClipboard)
        : this(post, new WindowsCredentialStore(), CreateEsiClient(), CreateSsoClient(), TimeProvider.System, null, readClipboard, writeClipboard)
    {
    }

    internal TriffSkillsController(
        Action<object> post,
        ICredentialStore credentials,
        EsiClient esi,
        IEveSsoClient sso,
        TimeProvider time,
        Func<string?>? saveState = null,
        Func<string>? readClipboard = null,
        Action<string>? writeClipboard = null)
    {
```

and inside that constructor body, beside `_post = post;`:

```csharp
        _readClipboard = readClipboard ?? (() => string.Empty);
        _writeClipboard = writeClipboard ?? (_ => { });
```

- [ ] **Step 4: Add the `copy-plan` handler**

Add a case in `HandleWebMessage`, above `default:`:

```csharp
            case "triffskills:copy-plan":
                CopyPlan(message);
                return true;
```

and the method beside `PostCellDetail`:

```csharp
    private void CopyPlan(JsonObject? message)
    {
        var planName = ReadString(message, "planName", 120);
        var plan = _plans.FirstOrDefault(item => string.Equals(item.Name, planName, StringComparison.OrdinalIgnoreCase));
        if (plan is null)
        {
            PostError("copy-plan", "That plan no longer exists. Reload plans and try again.");
            return;
        }

        try
        {
            var path = Path.Combine(TriffSkillsPaths.PlansDir, plan.Name + ".txt");
            var contents = AtomicFile.ReadBoundedText(path, PlanStore.MaxPlanFileBytes);
            _writeClipboard(ClipboardPlanText.WithTitle(plan.Name, contents));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            PostError("copy-plan", $"Plan could not be copied: {exception.Message}");
        }
        catch (Exception exception)
        {
            // The injected clipboard writer owns the real failure surface.
            PostError("copy-plan", $"Plan could not be copied: {exception.Message}");
        }
    }
```

- [ ] **Step 5: Wire MainWindow**

In `native/MainWindow.xaml.cs`, replace line 684:

```csharp
                _triffSkills ??= new TriffSkills.TriffSkillsController(PostAppEvent, ReadClipboardText, CopyText);
```

and add beside `ReadClipboard()`:

```csharp
    private string ReadClipboardText()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
```

`CopyText(string?)` already matches `Action<string>` by contravariance on the nullable annotation; if the compiler objects, wrap it as `text => CopyText(text)`.

- [ ] **Step 6: Run to verify pass**

Expected: 3 passed for the new class; full native suite 326 passed.

- [ ] **Step 7: Commit**

```bash
git add native/TriffSkills/TriffSkillsController.cs native/MainWindow.xaml.cs native/TriffView.Tests/ClipboardControllerTests.cs
git commit -m "feat(triffskills): inject clipboard access and copy a plan to it"
```

---

## Task 5: Clipboard import

**Files:**
- Modify: `native/TriffSkills/PlanImportWorkflow.cs`
- Modify: `native/TriffSkills/TriffSkillsController.cs`
- Test: `native/TriffView.Tests/ClipboardControllerTests.cs`

**Interfaces:**
- Consumes: `ClipboardPlanText.SplitTitle` (Task 3), the clipboard delegates (Task 4).
- Produces: `PlanImportWorkflow.Commit(string requestId, long revision, bool replace, string? nameOverride = null)`; the message `triffskills:import-clipboard` `{requestId, revision}` and the reply `triffskills:clipboard-preview` `{requestId, revision, ok, name, nameError, requirementCount, diagnostics}`.

Two rules this task exists to enforce, both found by review:

1. **Preview always runs under the placeholder `Imported plan`, never the candidate.** `PreviewAsync` validates the name before it parses (`PlanImportWorkflow.cs:53-58`), so a candidate like `# CON` — a reserved device name (`PlanNameValidator.cs:62-64`) — would be rejected before a pending preview was stored, and no later correction could commit.
2. **`ok`/`diagnostics` describe the plan; `name`/`nameError` describe the candidate.** The modal's `canCommit` reads `preview.ok` alone (`TriffSkills.tsx:221`), so a name problem reported through `diagnostics` would read as an unparseable plan.

- [ ] **Step 1: Write the failing tests**

Append to `native/TriffView.Tests/ClipboardControllerTests.cs`:

```csharp
    [Fact]
    public void ImportFromClipboardStripsTheTitleAndReportsTheCandidateName()
    {
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(() => "# Mastadon\r\nCPU Management IV\r\n", _ => { }, messages);

        controller.HandleWebMessage("triffskills:import-clipboard", new JsonObject { ["requestId"] = "req_0001", ["revision"] = 1 });

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("clipboard-preview", StringComparison.Ordinal) && json.Contains("Mastadon", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    [Fact]
    public void AnInvalidCandidateNameStillProducesACommittablePreview()
    {
        // "CON" is a reserved Windows device name. The plan itself is fine, so
        // ok must stay true and the reason must arrive as nameError.
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(() => "# CON\r\nCPU Management IV\r\n", _ => { }, messages);

        controller.HandleWebMessage("triffskills:import-clipboard", new JsonObject { ["requestId"] = "req_0002", ["revision"] = 1 });

        Assert.True(
            SpinWait.SpinUntil(
                () => messages.Any(json =>
                    json.Contains("clipboard-preview", StringComparison.Ordinal) &&
                    json.Contains("req_0002", StringComparison.Ordinal) &&
                    json.Contains("nameError", StringComparison.Ordinal) &&
                    json.Contains("reserved", StringComparison.OrdinalIgnoreCase)),
                SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    [Fact]
    public void AnEmptyClipboardReportsADiagnosticRatherThanSilence()
    {
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(() => string.Empty, _ => { }, messages);

        controller.HandleWebMessage("triffskills:import-clipboard", new JsonObject { ["requestId"] = "req_0003", ["revision"] = 1 });

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("req_0003", StringComparison.Ordinal) && json.Contains("clipboard", StringComparison.OrdinalIgnoreCase)), SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    [Fact]
    public void AReadThatThrowsSurfacesAsAnError()
    {
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(() => throw new InvalidOperationException("clipboard busy"), _ => { }, messages);

        controller.HandleWebMessage("triffskills:import-clipboard", new JsonObject { ["requestId"] = "req_0004", ["revision"] = 1 });

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("triffskills:error", StringComparison.Ordinal) && json.Contains("import-clipboard", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }
```

- [ ] **Step 2: Run to verify failure**

Expected: FAIL — `triffskills:import-clipboard` is unhandled, so no reply is posted and every `SpinUntil` times out.

- [ ] **Step 3: Let Commit take a validated name override**

In `native/TriffSkills/PlanImportWorkflow.cs`, change the signature and the `CommitValidated` call:

```csharp
    public PlanImportCommitResult Commit(string requestId, long revision, bool replace, string? nameOverride = null)
    {
        Prune();
        if (!_pending.TryGetValue(requestId, out var preview) || preview.Revision != revision)
        {
            return new PlanImportCommitResult(false, false, true, string.Empty, "Validated preview expired or no longer matches the current input.");
        }

        var name = preview.Name;
        if (!string.IsNullOrWhiteSpace(nameOverride))
        {
            if (!PlanNameValidator.TryValidate(nameOverride, out var validated, out var nameError))
            {
                // The pending preview is deliberately left in place: the caller
                // corrects the name and retries with the same requestId.
                return new PlanImportCommitResult(false, false, false, nameOverride, nameError);
            }
            name = validated;
        }

        var result = PlanStore.CommitValidated(
            _plansDirectory,
            name,
            preview.Contents,
            preview.Plan,
            replace);
        if (result.Success) _pending.TryRemove(requestId, out _);
        return new PlanImportCommitResult(result.Success, result.Collision, false, result.Name, result.Error);
    }
```

- [ ] **Step 4: Pass the override through the controller**

In `CommitPlanAsync`, replace the `Commit` call:

```csharp
        var result = _planImports.Commit(requestId, revision, ReadBool(message, "replace"), ReadString(message, "name", 120));
```

- [ ] **Step 5: Add the import-clipboard handler**

Add the case above `default:`:

```csharp
            case "triffskills:import-clipboard":
                _ = ImportFromClipboardAsync(message);
                return true;
```

and the method beside `PreviewPlanAsync`:

```csharp
    private const string ClipboardPlaceholderName = "Imported plan";

    private async Task ImportFromClipboardAsync(JsonObject? message)
    {
        var requestId = ReadRequestId(message);
        if (requestId.Length == 0) return;
        var revision = ReadRevision(message);

        string clipboard;
        try
        {
            clipboard = _readClipboard() ?? string.Empty;
        }
        catch (Exception exception)
        {
            PostError("import-clipboard", $"The clipboard could not be read: {exception.Message}");
            return;
        }

        var (candidate, contents) = ClipboardPlanText.SplitTitle(clipboard);
        if (string.IsNullOrWhiteSpace(contents))
        {
            PostClipboardPreview(requestId, revision, ok: false, name: string.Empty, nameError: string.Empty, requirementCount: 0,
                diagnostics: [new PlanDiagnostic(0, "The clipboard holds no plan text.")]);
            return;
        }

        // Always preview under a name guaranteed valid. Validating the candidate
        // here would reject it before a pending preview existed, and no later
        // correction in the dialog could then commit.
        var preview = await _planImports.PreviewAsync(requestId, revision, ClipboardPlaceholderName, contents, _lifetime.Token);

        var nameError = string.Empty;
        var name = candidate;
        if (candidate.Length > 0 && !PlanNameValidator.TryValidate(candidate, out _, out var candidateError))
        {
            name = string.Empty;
            nameError = candidateError;
        }

        PostClipboardPreview(
            requestId,
            revision,
            ok: preview.Plan is not null,
            name: name,
            nameError: nameError,
            requirementCount: preview.Plan?.Requirements.Count ?? 0,
            diagnostics: preview.Diagnostics);
    }

    private void PostClipboardPreview(
        string requestId,
        long revision,
        bool ok,
        string name,
        string nameError,
        int requirementCount,
        IReadOnlyList<PlanDiagnostic> diagnostics)
        => _post(new
        {
            type = "triffskills:clipboard-preview",
            requestId,
            revision,
            ok,
            name,
            nameError,
            requirementCount,
            diagnostics = diagnostics.Take(20).ToArray(),
        });
```

- [ ] **Step 6: Run to verify pass**

Expected: 7 passed in `ClipboardControllerTests`; full native suite 330 passed.

- [ ] **Step 7: Commit**

```bash
git add native/TriffSkills/PlanImportWorkflow.cs native/TriffSkills/TriffSkillsController.cs native/TriffView.Tests/ClipboardControllerTests.cs
git commit -m "feat(triffskills): import a plan straight from the clipboard"
```

---

## Task 6: Pin, group, and plan-selection handlers

**Files:**
- Modify: `native/TriffSkills/TriffSkillsController.cs`
- Test: `native/TriffView.Tests/SkillsMutationTests.cs` (create)

**Interfaces:**
- Consumes: `SnapshotPins()`, `SnapshotGroups()` (Task 1).
- Produces: handlers for `triffskills:set-pinned` `{characterId, pinned}`, `triffskills:save-group` `{name, characterIds, originalName?}`, `triffskills:delete-group` `{name}`, `triffskills:select-plan` `{planName}`.

Every handler snapshots, applies, saves, and **restores the snapshot on failure**, then posts state on both paths — the shape `ReorderCharacters` already uses (`TriffSkillsController.cs:229-242`). The snapshot must be an independent copy: `PinnedCharacterIds.Add(id)` mutates in place, so a captured reference and the live list are the same object.

- [ ] **Step 1: Write the failing tests**

Create `native/TriffView.Tests/SkillsMutationTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class SkillsMutationTests : IDisposable
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(5);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "triffskills-mutation", Guid.NewGuid().ToString("N"));

    public SkillsMutationTests() => TriffSkillsPaths.OverrideRoot(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        finally { TriffSkillsPaths.ClearOverride(); }
    }

    [Fact]
    public void SetPinnedIsReflectedInTheNextState()
    {
        SeedCharacter(9001);
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(messages, saveState: () => null);

        controller.HandleWebMessage("triffskills:set-pinned", new JsonObject { ["characterId"] = 9001, ["pinned"] = true });

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("pinnedCharacterIds", StringComparison.Ordinal) && json.Contains("9001", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    [Fact]
    public void AFailedSaveRestoresThePriorPins()
    {
        SeedCharacter(9001);
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(messages, saveState: () => "disk full");

        controller.HandleWebMessage("triffskills:set-pinned", JsonNode.Parse("""{"characterId":9001,"pinned":true}""")!.AsObject());

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("triffskills:error", StringComparison.Ordinal) && json.Contains("set-pinned", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));

        // State is posted on the failure path too, and must show no pin. SetPinned posts
        // the error and then the state synchronously in the same call, so by the time the
        // spin-wait above observes the error, both messages are already enqueued — but
        // messages.Last(...) would still be wrong to use: at the instant the error first
        // becomes visible, the last "state" message in the queue could still be the one
        // PostState posted during controller construction (before this handler ever ran),
        // which trivially has no pin and would make this assertion pass for the wrong
        // reason. Take the snapshot after the wait, find the error's position in it, and
        // require the specific state message that follows it.
        var snapshot = messages.ToArray();
        var errorIndex = Array.FindIndex(snapshot, json => json.Contains("triffskills:error", StringComparison.Ordinal) && json.Contains("set-pinned", StringComparison.Ordinal));
        var stateAfterError = snapshot
            .Skip(errorIndex + 1)
            .Select(json => JsonNode.Parse(json))
            .FirstOrDefault(node => (string?)node?["type"] == "triffskills:state");
        Assert.NotNull(stateAfterError);
        var pinnedIds = stateAfterError!["pinnedCharacterIds"]?.AsArray().Select(node => (long?)node).ToArray() ?? [];
        Assert.DoesNotContain(9001L, pinnedIds);
    }

    [Fact]
    public void AFailedSaveRestoresThePriorGroupName()
    {
        SeedCharacter(9001);
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(messages, saveState: () => null);
        controller.HandleWebMessage("triffskills:save-group", new JsonObject
        {
            ["name"] = "Haulers",
            ["characterIds"] = new JsonArray(9001),
        });

        using var failing = Controller(messages, saveState: () => "disk full");
        failing.HandleWebMessage("triffskills:save-group", new JsonObject
        {
            ["originalName"] = "Haulers",
            ["name"] = "Renamed",
            ["characterIds"] = new JsonArray(9001),
        });

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("triffskills:error", StringComparison.Ordinal) && json.Contains("save-group", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));

        var last = messages.Last(json => json.Contains("triffskills:state", StringComparison.Ordinal));
        Assert.Contains("Haulers", last, StringComparison.Ordinal);
        Assert.DoesNotContain("Renamed", last, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateGroupNameIsRejected()
    {
        SeedCharacter(9001);
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(messages, saveState: () => null);

        controller.HandleWebMessage("triffskills:save-group", new JsonObject { ["name"] = "Haulers", ["characterIds"] = new JsonArray(9001) });
        controller.HandleWebMessage("triffskills:save-group", new JsonObject { ["name"] = "haulers", ["characterIds"] = new JsonArray(9001) });

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("triffskills:error", StringComparison.Ordinal) && json.Contains("save-group", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    private void SeedCharacter(long characterId)
    {
        var state = new TriffSkillsState();
        var character = state.Upsert(characterId);
        character.CharacterName = $"Pilot {characterId}";
        Assert.True(state.TrySave(out var error), error);
    }

    private static TriffSkillsController Controller(ConcurrentQueue<string> messages, Func<string?> saveState)
        => new(
            value => messages.Enqueue(JsonSerializer.Serialize(value)),
            new MemoryCredentials(),
            new EsiClient(new HttpClient(new StubHandler()), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }, "TriffView.Tests/1.0"),
            new ControlledSso(),
            TimeProvider.System,
            saveState);

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Expected: FAIL — the four messages are unhandled and the state payload has no `pinnedCharacterIds`.

- [ ] **Step 3: Add the handler cases**

Above `default:` in `HandleWebMessage`:

```csharp
            case "triffskills:set-pinned":
                SetPinned(message);
                return true;
            case "triffskills:save-group":
                SaveGroup(message);
                return true;
            case "triffskills:delete-group":
                DeleteGroup(message);
                return true;
            case "triffskills:select-plan":
                SelectPlan(message);
                return true;
```

- [ ] **Step 4: Implement the handlers**

Beside `ReorderCharacters`:

```csharp
    private void SetPinned(JsonObject? message)
    {
        var characterId = ReadLong(message, "characterId");
        if (_state.Find(characterId) is null)
        {
            PostError("set-pinned", "That character no longer exists.");
            PostState(force: true);
            return;
        }

        // An independent copy, not a captured reference: Add and Remove mutate
        // this list in place, so a reference would be the same object.
        var previous = _state.SnapshotPins();
        if (ReadBool(message, "pinned"))
        {
            if (!_state.PinnedCharacterIds.Contains(characterId)) _state.PinnedCharacterIds.Add(characterId);
        }
        else
        {
            _state.PinnedCharacterIds.Remove(characterId);
        }

        if (_saveState() is not null)
        {
            _state.PinnedCharacterIds = previous;
            PostError("set-pinned", "The pin could not be saved.");
        }
        PostState(force: true);
    }

    private void SaveGroup(JsonObject? message)
    {
        var name = ReadString(message, "name", TriffSkillsState.MaxGroupNameLength).Trim();
        if (name.Length == 0)
        {
            PostError("save-group", "A group needs a name.");
            PostState(force: true);
            return;
        }

        var originalName = ReadString(message, "originalName", TriffSkillsState.MaxGroupNameLength).Trim();
        var characterIds = ReadCharacterIds(message);

        // Deep, not shallow: renaming mutates a group object, which a shallow
        // list copy would still share.
        var previous = _state.SnapshotGroups();

        var existing = _state.CharacterGroups.FirstOrDefault(group => string.Equals(group.Name, originalName, StringComparison.OrdinalIgnoreCase));
        var collides = _state.CharacterGroups.Any(group =>
            string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase) && !ReferenceEquals(group, existing));
        if (collides)
        {
            PostError("save-group", $"A group called \"{name}\" already exists.");
            PostState(force: true);
            return;
        }

        if (existing is null)
        {
            if (_state.CharacterGroups.Count >= TriffSkillsState.MaxGroups)
            {
                PostError("save-group", $"There is a maximum of {TriffSkillsState.MaxGroups} groups.");
                PostState(force: true);
                return;
            }
            _state.CharacterGroups.Add(new CharacterGroup { Name = name, CharacterIds = characterIds });
        }
        else
        {
            existing.Name = name;
            existing.CharacterIds = characterIds;
        }

        if (_saveState() is not null)
        {
            _state.CharacterGroups = previous;
            PostError("save-group", "The group could not be saved.");
        }
        PostState(force: true);
    }

    private void DeleteGroup(JsonObject? message)
    {
        var name = ReadString(message, "name", TriffSkillsState.MaxGroupNameLength).Trim();
        var previous = _state.SnapshotGroups();
        _state.CharacterGroups.RemoveAll(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase));

        if (_saveState() is not null)
        {
            _state.CharacterGroups = previous;
            PostError("delete-group", "The group could not be removed.");
        }
        PostState(force: true);
    }

    private void SelectPlan(JsonObject? message)
    {
        var previous = _state.SelectedPlanName;
        _state.SelectedPlanName = ReadString(message, "planName", 120);

        if (_saveState() is not null)
        {
            _state.SelectedPlanName = previous;
            PostError("select-plan", "The selected plan could not be saved.");
        }
        PostState(force: true);
    }

    private static List<long> ReadCharacterIds(JsonObject? message)
    {
        var ids = new List<long>();
        if (message?["characterIds"] is not JsonArray array) return ids;
        foreach (var node in array.Take(TriffSkillsState.MaxCharacters))
        {
            if (node is null) continue;
            try { ids.Add(node.GetValue<long>()); }
            catch (FormatException) { }
            catch (InvalidOperationException) { }
        }
        return ids;
    }
```

- [ ] **Step 5: Run — the pin tests should still fail on the payload**

Expected: the error-path tests PASS; `SetPinnedIsReflectedInTheNextState` still FAILS because `pinnedCharacterIds` is not projected yet. That gap is Task 7.

- [ ] **Step 6: Commit**

```bash
git add native/TriffSkills/TriffSkillsController.cs native/TriffView.Tests/SkillsMutationTests.cs
git commit -m "feat(triffskills): pin, group, and plan-selection handlers with rollback"
```

---

## Task 7: Project the new fields into `triffskills:state`

**Files:**
- Modify: `native/TriffSkills/TriffSkillsController.cs:607-647`
- Test: `native/TriffView.Tests/SkillsMutationTests.cs` (existing tests from Task 6 go green)

**Interfaces:**
- Consumes: Task 1's state fields.
- Produces: `triffskills:state` additionally carries `pinnedCharacterIds: number[]`, `characterGroups: {name, characterIds}[]`, `selectedPlanName: string`.

Projected from normalized state, so the web never sees an id native has already dropped.

- [ ] **Step 1: Extend the projection**

In `PostState`, inside the anonymous `state` object, after the `characters` array:

```csharp
            pinnedCharacterIds = _state.PinnedCharacterIds.ToArray(),
            characterGroups = _state.CharacterGroups.Select(group => new
            {
                group.Name,
                CharacterIds = group.CharacterIds.ToArray(),
            }).ToArray(),
            selectedPlanName = _state.SelectedPlanName,
```

- [ ] **Step 2: Run the Task 6 tests**

Run with `--filter FullyQualifiedName~SkillsMutationTests`.
Expected: all 4 PASS.

- [ ] **Step 3: Run the whole native suite**

Expected: 334 passed, 0 failed, `MatrixBoundsTests` among them.

- [ ] **Step 4: Commit**

```bash
git add native/TriffSkills/TriffSkillsController.cs
git commit -m "feat(triffskills): project pins, groups, and selected plan to the web"
```

---

## Task 8: Remove native drag-reorder

**Files:**
- Modify: `native/TriffSkills/TriffSkillsController.cs` (remove the case and `ReorderCharacters`)
- Modify: `native/TriffSkills/TriffSkillsState.cs` (remove `TryReorderCharacters`)
- Modify: `native/TriffView.Tests/TriffSkillsStateTests.cs` and any controller test covering reorder

**Interfaces:**
- Consumes: nothing.
- Produces: nothing. This is a deletion.

Do this **after** Task 6, so the reorder handler is still available as the reference pattern while the new handlers are being written.

- [ ] **Step 1: Find every reference**

```bash
grep -rn "TryReorderCharacters\|reorder-characters\|ReorderCharacters" native/ app/
```

- [ ] **Step 2: Delete them**

Remove the `case "triffskills:reorder-characters":` block, the `ReorderCharacters` method, `TriffSkillsState.TryReorderCharacters`, and every test asserting on them. Leave the web references for Task 9.

- [ ] **Step 3: Run the whole native suite**

Expected: PASS, with a count lower than 334 by however many reorder tests were removed. Record the new number in the commit message.

- [ ] **Step 4: Commit**

```bash
git add -A native/
git commit -m "refactor(triffskills): drop manual character ordering, replaced by pins and groups"
```

---

## Task 9: Roster ordering module

**Files:**
- Create: `app/src/tools/skills/rosterOrdering.ts`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `type Readiness = "Ready" | "Training" | "Locked" | "Missing" | "Unknown" | "Unscored"`
  - `type RosterGroup = { key: Readiness | "Pinned"; label: string; characterIds: number[] }`
  - `buildRoster(input: RosterInput): RosterGroup[]` where
    `RosterInput = { characters: {characterId: number; characterName: string}[]; readinessOf: (characterId: number) => Readiness; missingOf: (characterId: number) => number; pinnedIds: number[]; filter: string; activeGroup: string | null; groups: {name: string; characterIds: number[]}[] }`

Pure, no React, no DOM. `app/` has no test runner, so this is verified by build plus the manual checks in Task 11.

- [ ] **Step 1: Write the module**

Create `app/src/tools/skills/rosterOrdering.ts`:

```typescript
export type Readiness = "Ready" | "Training" | "Locked" | "Missing" | "Unknown" | "Unscored";

export type RosterCharacter = { characterId: number; characterName: string };
export type RosterGroupDefinition = { name: string; characterIds: number[] };

export type RosterInput = {
  characters: RosterCharacter[];
  readinessOf: (characterId: number) => Readiness;
  missingOf: (characterId: number) => number;
  pinnedIds: number[];
  filter: string;
  activeGroup: string | null;
  groups: RosterGroupDefinition[];
};

export type RosterGroup = {
  key: Readiness | "Pinned";
  label: string;
  characterIds: number[];
};

const READINESS_ORDER: Readiness[] = ["Ready", "Training", "Locked", "Missing", "Unknown", "Unscored"];

const LABELS: Record<Readiness | "Pinned", string> = {
  Pinned: "Pinned",
  Ready: "Ready",
  Training: "Training",
  Locked: "Locked",
  Missing: "Missing",
  Unknown: "Unknown",
  Unscored: "Unscored",
};

/**
 * Groups every character exactly once. Driven by the character list rather than
 * by enumerating readiness values, so a character whose readiness is Unscored —
 * every newly added character, until its first refresh — still gets a row and
 * stays reachable for forget and re-authenticate.
 */
export function buildRoster(input: RosterInput): RosterGroup[] {
  const needle = input.filter.trim().toLowerCase();
  const pinned = new Set(input.pinnedIds);
  const groupMembers = input.activeGroup
    ? new Set(input.groups.find((group) => group.name === input.activeGroup)?.characterIds ?? [])
    : null;

  const visible = input.characters.filter((character) => {
    if (needle && !character.characterName.toLowerCase().includes(needle)) return false;
    if (groupMembers && !groupMembers.has(character.characterId)) return false;
    return true;
  });

  const byName = (left: RosterCharacter, right: RosterCharacter) =>
    left.characterName.localeCompare(right.characterName);

  const groups: RosterGroup[] = [];

  const pinnedMembers = visible.filter((character) => pinned.has(character.characterId)).sort(byName);
  if (pinnedMembers.length) {
    groups.push({ key: "Pinned", label: LABELS.Pinned, characterIds: pinnedMembers.map((c) => c.characterId) });
  }

  for (const readiness of READINESS_ORDER) {
    const members = visible.filter(
      (character) => !pinned.has(character.characterId) && input.readinessOf(character.characterId) === readiness,
    );
    if (!members.length) continue;

    // Closest-to-ready first, so the long Missing group leads with whoever is
    // nearly there. Distance is a count of unmet requirements, not training
    // time — the evaluator has no skill ranks to compute time from.
    const ordered =
      readiness === "Missing"
        ? [...members].sort(
            (left, right) =>
              input.missingOf(left.characterId) - input.missingOf(right.characterId) || byName(left, right),
          )
        : [...members].sort(byName);

    groups.push({ key: readiness, label: LABELS[readiness], characterIds: ordered.map((c) => c.characterId) });
  }

  return groups;
}
```

- [ ] **Step 2: Verify it compiles**

```bash
cd app && npm run build
```
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add app/src/tools/skills/rosterOrdering.ts
git commit -m "feat(triffskills): pure roster grouping, filtering, and ordering"
```

---

## Task 10: Remove the matrix and the drag handlers from the web

**Files:**
- Modify: `app/src/tools/TriffSkills.tsx` (remove the matrix `<section>` at `792-923`, `reorderCharacters`, the drag state and refs, and `DetailPanel`'s character/plan/cell branches)
- Modify: `app/src/tools/TriffSkills.css` (remove matrix and table rules)

**Interfaces:**
- Consumes: nothing.
- Produces: nothing. Deletion only — the pane is deliberately empty at the end of this task, and Tasks 11-13 fill it.

Landing this separately keeps the diff readable; a combined remove-and-rebuild commit is unreviewable.

- [ ] **Step 1: Delete the matrix section**

Remove the whole `<section className="triffskills-matrix-pane">` block and its `triffskills-matrix-scroll` table. Replace the `triffskills-workspace` contents with a placeholder `<div className="triffskills-empty"><p>Roster coming in the next task.</p></div>` so the file still compiles.

- [ ] **Step 2: Delete the drag machinery**

Remove `reorderCharacters`, `draggedCharacterId`, `dragTargetCharacterId`, `draggedCharacterRef`, and every `onDragStart` / `onDragOver` / `onDrop` / `onDragEnd` handler.

- [ ] **Step 3: Delete `DetailPanel`**

Remove the component and its usage. Its contents are re-homed in Tasks 11-12.

- [ ] **Step 4: Delete the matrix CSS**

In `app/src/tools/TriffSkills.css`, remove every rule scoped to `.triffskills-matrix`, `.triffskills-matrix-scroll`, `.triffskills-matrix-pane`, `.triffskills-detail-pane`, and the rotated column-header rules. Keep `.triffskills-progress-mark` and the `.is-ready` / `.is-training` / `.is-locked` / `.is-missing` / `.is-unknown` colour rules — the group headers still use them.

- [ ] **Step 5: Verify the build**

```bash
cd app && npm run build
```
Expected: succeeds with no unused-symbol errors.

- [ ] **Step 6: Commit**

```bash
git add app/src/tools/TriffSkills.tsx app/src/tools/TriffSkills.css
git commit -m "refactor(triffskills): remove the readiness grid and its detail panel"
```

---

## Task 11: Plan rail and roster pane

**Files:**
- Create: `app/src/tools/skills/PlanRail.tsx`
- Modify: `app/src/tools/TriffSkills.tsx`
- Modify: `app/src/tools/TriffSkills.css`

**Interfaces:**
- Consumes: `buildRoster` (Task 9); `pinnedCharacterIds`, `characterGroups`, `selectedPlanName` from the state payload (Task 7); `triffskills:select-plan` and `triffskills:set-pinned` (Task 6).
- Produces: `PlanRail` with props `{ plans: {name: string; requirementCount: number}[]; readyCounts: Record<string, number>; characterCount: number; selectedPlanName: string; onSelect: (planName: string) => void }`.

- [ ] **Step 1: Write the rail**

Create `app/src/tools/skills/PlanRail.tsx`:

```tsx
import React from "react";

type Plan = { name: string; requirementCount: number };

export default function PlanRail({
  plans,
  readyCounts,
  characterCount,
  selectedPlanName,
  onSelect,
}: {
  plans: Plan[];
  readyCounts: Record<string, number>;
  characterCount: number;
  selectedPlanName: string;
  onSelect: (planName: string) => void;
}) {
  if (!plans.length) {
    return <p className="triffskills-rail-status">No local plans yet. Import one, then reload plans.</p>;
  }

  return (
    <nav className="triffskills-plan-list" aria-label="Skill plans">
      {plans.map((plan) => {
        const selected = plan.name === selectedPlanName;
        return (
          <button
            type="button"
            key={plan.name}
            className={`triffskills-plan-row${selected ? " is-selected" : ""}`}
            aria-current={selected ? "true" : undefined}
            title={`${plan.name} — ${plan.requirementCount} requirement${plan.requirementCount === 1 ? "" : "s"}`}
            onClick={() => onSelect(plan.name)}
          >
            <span className="triffskills-plan-name">{plan.name}</span>
            <span className="triffskills-plan-ratio">
              {readyCounts[plan.name] ?? 0}/{characterCount}
            </span>
          </button>
        );
      })}
    </nav>
  );
}
```

- [ ] **Step 2: Add the state fields and selection to `TriffSkills.tsx`**

Extend `SkillsState` and `EMPTY_STATE`:

```tsx
type CharacterGroupDef = { name: string; characterIds: number[] };
```

add to `SkillsState`:

```tsx
  pinnedCharacterIds: number[];
  characterGroups: CharacterGroupDef[];
  selectedPlanName: string;
```

and to `EMPTY_STATE`:

```tsx
  pinnedCharacterIds: [],
  characterGroups: [],
  selectedPlanName: "",
```

- [ ] **Step 3: Derive the selected plan and the ready counts**

In the component:

```tsx
  const selectedPlanName = useMemo(() => {
    if (state.plans.some((plan) => plan.name === state.selectedPlanName)) return state.selectedPlanName;
    return state.plans[0]?.name ?? "";
  }, [state.plans, state.selectedPlanName]);

  const readyCounts = useMemo(() => {
    const counts: Record<string, number> = {};
    for (const cell of state.matrix) {
      if (cell.readiness === "Ready") counts[cell.planName] = (counts[cell.planName] ?? 0) + 1;
    }
    return counts;
  }, [state.matrix]);
```

An unknown `selectedPlanName` resolves to the first plan without rewriting native state — plans are files and are not part of that state.

- [ ] **Step 4: Render the rail and the roster groups**

Replace the placeholder from Task 10 with the pane, using `buildRoster`:

```tsx
  const [filter, setFilter] = useState("");
  const [activeGroup, setActiveGroup] = useState<string | null>(null);

  const cellFor = (characterId: number) => cells.get(key(characterId, selectedPlanName));

  const roster = useMemo(
    () =>
      buildRoster({
        characters: state.characters,
        readinessOf: (characterId) => cellFor(characterId)?.readiness ?? "Unscored",
        missingOf: (characterId) => cellFor(characterId)?.missingCount ?? Number.MAX_SAFE_INTEGER,
        pinnedIds: state.pinnedCharacterIds,
        filter,
        activeGroup,
        groups: state.characterGroups,
      }),
    [state.characters, state.pinnedCharacterIds, state.characterGroups, cells, selectedPlanName, filter, activeGroup],
  );
```

and render each group with the readiness mark as its header key:

```tsx
  {roster.map((group) => (
    <section key={group.key} className="triffskills-roster-group">
      <h4 className={group.key === "Pinned" ? "is-pinned" : statusClass(group.key as Readiness)}>
        {group.key !== "Pinned" ? <ProgressMark readiness={group.key as Readiness} fill={STATUS[group.key as Readiness].sampleFill} /> : <span aria-hidden="true">★</span>}
        {group.label}
        <span>{group.characterIds.length}</span>
      </h4>
      {group.characterIds.map((characterId) => (
        <CharacterRow key={characterId} characterId={characterId} planName={selectedPlanName} /* remaining props in Task 12 */ />
      ))}
    </section>
  ))}
```

- [ ] **Step 5: Add the filter box and group chips**

```tsx
  <div className="triffskills-filters">
    <input
      type="search"
      className="triffskills-filter"
      placeholder="Filter characters…"
      value={filter}
      onChange={(event) => setFilter(event.target.value)}
      aria-label="Filter characters by name"
    />
    <button type="button" className={`triffskills-chip${activeGroup === null ? " is-on" : ""}`} onClick={() => setActiveGroup(null)}>
      All {state.characters.length}
    </button>
    {state.characterGroups.map((group) => (
      <button
        type="button"
        key={group.name}
        className={`triffskills-chip${activeGroup === group.name ? " is-on" : ""}`}
        onClick={() => setActiveGroup(activeGroup === group.name ? null : group.name)}
      >
        {group.name}
      </button>
    ))}
  </div>
```

- [ ] **Step 6: Add the CSS**

Add rail, roster-group, filter, and chip rules to `app/src/tools/TriffSkills.css`, following the existing conventions: `border-radius: 0`, `var(--tv-*)` tokens only, `font-variant-numeric: tabular-nums` on the ratio, and `data-hud-scroll` on the scrolling roster container.

- [ ] **Step 7: Build and check by hand on Windows**

```bash
cd app && npm run build
```
then build and run the app. Confirm: the rail lists every plan with a ratio; selecting one refills the pane; the roster scrolls vertically and the page never scrolls sideways; the filter narrows as you type; group chips narrow the list; **a character with no successful refresh appears under Unscored**.

- [ ] **Step 8: Commit**

```bash
git add app/src/tools/skills/PlanRail.tsx app/src/tools/TriffSkills.tsx app/src/tools/TriffSkills.css
git commit -m "feat(triffskills): plan rail and grouped character roster"
```

---

## Task 12: Character row, expansion, and management

**Files:**
- Create: `app/src/tools/skills/CharacterRow.tsx`
- Modify: `app/src/tools/TriffSkills.tsx`, `app/src/tools/TriffSkills.css`

**Interfaces:**
- Consumes: `triffskills:get-cell-detail` (existing), `triffskills:set-pinned`, `triffskills:save-group`, `triffskills:forget-character` (existing).
- Produces: `CharacterRow` with props `{ character: Character; cell?: MatrixCell; planName: string; pinned: boolean; groups: CharacterGroupDef[]; expanded: boolean; detail?: CellDetail; onToggleExpand: () => void; onTogglePin: () => void; onToggleGroup: (groupName: string) => void; onForget: () => void; onCopyMissing: () => void }`.

This row is the **only** surface for forgetting or re-authenticating a character once the detail panel is gone. A character with no row cannot be repaired or removed, which is why Task 9's grouping is driven by the character list.

- [ ] **Step 1: Write the component**

Row: star button (pin), name, group tags, and the right-hand status — `Ready`, `Training — <eta>`, `Missing N`, `Unscored`. Show a `Stale` badge **only** when the character is degraded, replacing the old always-on `Current` label.

Expansion: outstanding requirements from `detail`, a `Copy missing skills` button, a checkbox per group for membership, `Forget character` behind the existing two-step confirm, and the re-auth prompt when `needsReauth` is set.

- [ ] **Step 2: Fetch detail lazily on expand**

Expanding posts `triffskills:get-cell-detail` for that character and plan. One request per expansion — the compact matrix carries counts only, and eager fetching would mean up to 50 requests per tab open.

- [ ] **Step 3: Build and check by hand on Windows**

Confirm: expanding shows only outstanding requirements; the pin star persists across a restart; group checkboxes persist; **forget still works and its confirm still appears**; a `needsReauth` character shows the prompt.

- [ ] **Step 4: Commit**

```bash
git add app/src/tools/skills/CharacterRow.tsx app/src/tools/TriffSkills.tsx app/src/tools/TriffSkills.css
git commit -m "feat(triffskills): character rows carry detail, pinning, groups, and management"
```

---

## Task 13: Train next tab, clipboard import dialog, and copy

**Files:**
- Create: `app/src/tools/skills/ImportClipboardDialog.tsx`
- Modify: `app/src/tools/TriffSkills.tsx`, `app/src/tools/TriffSkills.css`

**Interfaces:**
- Consumes: `triffskills:import-clipboard` and `triffskills:clipboard-preview` (Task 5), `triffskills:copy-plan` (Task 4), `triffskills:commit-plan` with a `name` (Task 5).
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Add the two tabs**

`Readiness` (Task 11's roster) and `Train next`: the same roster filtered to not-ready characters, ordered fewest-missing-first, each row showing its count and expanding to the specific skills — reusing `CharacterRow`.

- [ ] **Step 2: Write the import dialog**

A name field, a verdict line, and `Cancel` / `Import`. The critical wiring:

```tsx
  // ok/diagnostics describe the plan; name/nameError describe the candidate.
  // canCommit must not consult nameError — a rejected candidate still has a
  // valid, committable preview.
  const canCommit = Boolean(preview?.ok) && name.trim().length > 0 && !saving;
```

- [ ] **Step 3: Do not invalidate on name edit**

Editing the name must leave `requestId` and `revision` untouched. The textarea path clears the preview on every name change (`TriffSkills.tsx:663-671`), which is correct there because the web holds the plan text; here the contents live natively and an invalidated preview could never be rebuilt — the dialog would deadlock.

- [ ] **Step 4: Retry in place on a name rejection**

A `plan-commit` failure caused by the name shows the message and leaves the dialog open with the preview intact. `Commit` removes the pending preview only on success, so retrying under a corrected name with the same `requestId` and `revision` succeeds.

- [ ] **Step 5: Wire `Copy plan`**

One button in the rail — not also in the pane header — posting `triffskills:copy-plan` for the selected plan.

- [ ] **Step 6: Build and check by hand on Windows**

Confirm: copy a plan, paste into a text editor, see the `# name` header. Copy a plan, import it back, and **open the saved file to confirm it has no `#` line**. Repeat the cycle three times and confirm the file still has none. Copy text whose title is `# CON`, confirm the dialog shows a requirement count with a name error and an empty name field, correct the name, and confirm the import succeeds.

- [ ] **Step 7: Commit**

```bash
git add app/src/tools/skills/ImportClipboardDialog.tsx app/src/tools/TriffSkills.tsx app/src/tools/TriffSkills.css
git commit -m "feat(triffskills): train-next tab and clipboard plan import/export"
```

---

## Task 14: Full verification

**Files:** none — this task only runs things.

- [ ] **Step 1: Both test projects**

Run the invocation for `tests\TriffView.Tests\TriffView.Tests.csproj` and for `native\TriffView.Tests\TriffView.Tests.csproj`.
Expected: 182 passed (unchanged — no TriffSkills work belongs there) and the native count from Task 8 plus Tasks 9-13's zero new C# tests.

- [ ] **Step 2: Web build**

```bash
cd app && npm run build
```

- [ ] **Step 3: Native build**

Run the invocation with `build` instead of `test`, against `native\TriffView.csproj`.

- [ ] **Step 4: Run the app on Windows and walk the manual checks**

Copy `native/Assets/overlay-dist.zip` from the main checkout first, or the app serves the "missing overlay" page and every UI observation is void. Then walk every manual check named in Tasks 11, 12, and 13, plus:

- The pane on a 100% monitor as well as the 200% primary.
- A newly added character, before its first refresh, appearing under Unscored and being removable.

- [ ] **Step 5: Report honestly**

State which checks ran and which did not. Do not claim runtime behaviour that was not exercised.

---

## Self-Review

**Spec coverage.** Every section maps to a task: Observable behaviour → 11, 12, 13; Persisted state → 1, 2; Native and web contract → 4, 5, 6, 7; Clipboard round trip and Import flow → 3, 5, 13; Removed → 8, 10; Error and failure behaviour → 4, 5, 6, 12; Testing → each task's own steps; Verification → 14.

**Placeholders.** Tasks 12 and 13 describe component structure in prose rather than full verbatim JSX, and that is deliberate: their exact markup depends on Task 11's composition, and pre-writing 400 lines of React that a reviewer will edit on contact is the "so over-specified they'll be wrong on contact" failure. Every *decision* in them is concrete — the `canCommit` expression, the no-invalidate-on-name-edit rule, the retry-in-place behaviour, and the manual checks are all specified exactly. An implementer following them cannot get the contract wrong; they can choose their own element names.

**Type consistency.** `buildRoster` returns `RosterGroup[]` with `key: Readiness | "Pinned"`, consumed with that union in Task 11. `Commit`'s fourth parameter is `string? nameOverride` in Task 5 and is passed `ReadString(message, "name", 120)` from the controller. `SnapshotPins`/`SnapshotGroups` are defined in Task 1 and used in Tasks 2 and 6 under those names.

**Known gap, stated rather than hidden.** Tasks 9-13 have no automated coverage; `app/` has no test runner and this plan does not add one. The manual checks in Tasks 11-13 and Task 14 are the whole of their verification.
