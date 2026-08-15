# Combat Log Export Manifest Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Write a `triffview-manifest.json` into the combat log export archive that records export provenance and states which characters belong to the one human who exported them.

**Architecture:** A new manifest entry is written inside the existing staging block of `CombatLogExport.Export`, after the logs are copied and before the archive is disposed, so a failed manifest write cleans up through the same path as any other failure. Character identity comes from the `Listener:` header the export already reads, and numeric character ids are parsed from Gamelog filenames. The window's provenance (auto-detected fight versus hand-typed range) rides along on `CombatLogFightWindow`, which is already in scope at the single call site.

**Tech Stack:** C# / .NET 8, `System.IO.Compression`, `System.Text.Json`, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-15-combat-log-manifest-design.md`

## Global Constraints

- The manifest entry is named **`triffview-manifest.json`**, at the archive root. It **must never** end in `.txt` — eve-intel's parser globs `*.txt` and would turn it into a phantom pilot.
- `FileCount` and `RawBytes` on `CombatLogExportResult` stay **logs-only**. The settings panel renders "Exported N logs"; counting the manifest is a UI regression.
- The manifest is **always written**. No opt-in, no confirmation dialog.
- JSON is UTF-8 **without a BOM**, indented, camelCase — matching `JsonNamingPolicy.CamelCase` + `WriteIndented` used at `native/TriffView/TriffViewSubsystem.cs:3541-3542`.
- Both consuming projects target .NET 8 (`native/TriffView.csproj:4` is `net8.0-windows`, `tests/TriffView.Tests/TriffView.Tests.csproj:3` is `net8.0`), so `char.IsAsciiDigit` and `File.Move(overwrite:)` are available.
- `native/TriffAlerts/CombatLogExport.cs` is compiled **directly into the test assembly** (`tests/TriffView.Tests/TriffView.Tests.csproj:25`), not referenced. `internal` members are therefore visible to tests — no `InternalsVisibleTo` needed. `native/TriffView/TriffViewSubsystem.cs` is **not** linked in, so nothing in it is unit-testable.

**Running the build on Linux** (CI runs `windows-latest` and needs neither flag):

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet test tests/TriffView.Tests/TriffView.Tests.csproj -p:EnableSourceControlManagerQueries=false
dotnet build native/TriffView.csproj -c Release -p:EnableSourceControlManagerQueries=false -p:EnableWindowsTargeting=true
```

`EnableSourceControlManagerQueries=false` works around the git tasks rejecting this repo's `relativeworktrees` extension; `EnableWindowsTargeting=true` lets the WPF project compile off Windows. Neither belongs in a committed file.

---

### Task 1: Parse the character id from a Gamelog filename

**Files:**
- Modify: `native/TriffAlerts/CombatLogExport.cs` (add a private-region helper near `ReadHeader`)
- Test: `tests/TriffView.Tests/CombatLogExportTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `internal static bool TryParseCharacterId(string fileName, out long characterId)` on `CombatLogExport`. Task 2 calls it.

- [ ] **Step 1: Write the failing tests**

Add to `tests/TriffView.Tests/CombatLogExportTests.cs`, immediately before the `// ---- Export ----` marker:

```csharp
    // ---- Character ids ----

    [Fact]
    public void ACharacterIdIsReadFromTheGamelogFilename()
    {
        Assert.True(CombatLogExport.TryParseCharacterId("20260814_115000_98000001.txt", out var id));
        Assert.Equal(98000001, id);
    }

    [Fact]
    public void AnIdLessFilenameDoesNotYieldItsTimeAsAnId()
    {
        // The trap: older clients wrote YYYYMMDD_HHMMSS.txt with no id. A rule
        // that took "the trailing all-digit segment" would report 90000 here --
        // a confident, wrong id that collides across dates.
        Assert.False(CombatLogExport.TryParseCharacterId("20260814_090000.txt", out _));
    }

    [Fact]
    public void AnUnrecognisedFilenameHasNoCharacterId()
    {
        Assert.False(CombatLogExport.TryParseCharacterId("during.txt", out _));
        Assert.False(CombatLogExport.TryParseCharacterId("20260814_115000_98000001_extra.txt", out _));
        Assert.False(CombatLogExport.TryParseCharacterId("2026081_115000_98000001.txt", out _));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/TriffView.Tests/TriffView.Tests.csproj -p:EnableSourceControlManagerQueries=false`

Expected: FAIL to **compile**, with `CS0117: 'CombatLogExport' does not contain a definition for 'TryParseCharacterId'`. A compile failure is the correct red here — the method does not exist yet.

- [ ] **Step 3: Write the implementation**

In `native/TriffAlerts/CombatLogExport.cs`, insert directly above `private static void ReadHeader(`:

```csharp
    /// <summary>
    /// Reads the character id out of a Gamelog filename. EVE names these
    /// <c>YYYYMMDD_HHMMSS_&lt;characterID&gt;.txt</c>, but older clients wrote
    /// <c>YYYYMMDD_HHMMSS.txt</c> with no id -- and a looser rule taking the
    /// trailing all-digit segment would read that file's six-digit *time* as a
    /// character id. A wrong id is worse than none: it looks authoritative and
    /// collides across dates.
    /// </summary>
    internal static bool TryParseCharacterId(string fileName, out long characterId)
    {
        characterId = 0;
        var parts = Path.GetFileNameWithoutExtension(fileName).Split('_');
        if (parts.Length != 3) return false;
        if (parts[0].Length != 8 || !parts[0].All(char.IsAsciiDigit)) return false;
        if (parts[1].Length != 6 || !parts[1].All(char.IsAsciiDigit)) return false;
        if (parts[2].Length == 0 || !parts[2].All(char.IsAsciiDigit)) return false;
        return long.TryParse(
            parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out characterId);
    }
```

`NumberStyles` and `CultureInfo` come from `System.Globalization`, already imported at line 1. No new `using` is needed.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/TriffView.Tests/TriffView.Tests.csproj -p:EnableSourceControlManagerQueries=false`

Expected: PASS, 76 total (73 existing + 3 new), 0 failed.

- [ ] **Step 5: Commit**

```bash
git add native/TriffAlerts/CombatLogExport.cs tests/TriffView.Tests/CombatLogExportTests.cs
git commit -m "Parse character ids from Gamelog filenames"
```

---

### Task 2: Write the manifest into the archive

**Files:**
- Modify: `native/TriffAlerts/CombatLogExport.cs` (new enum, `CombatLogFightWindow.Source`, `DetectLastFight`, `Export` signature, plus a new `WriteManifest`)
- Modify: `tests/TriffView.Tests/CombatLogExportTests.cs` (three existing assertions break — see Step 1)

**Interfaces:**
- Consumes: `TryParseCharacterId` from Task 1; the existing `SelectedLog(string Path, string EntryName, string CharacterName, DateTime LastWriteUtc)` record at `CombatLogExport.cs:360`.
- Produces: `public const string ManifestEntryName = "triffview-manifest.json"`; `public enum CombatLogWindowSource { Unspecified, LastFight, ManualRange }`; `CombatLogFightWindow.Source`; `Export(..., CombatLogWindowSource source = CombatLogWindowSource.Unspecified)`. Task 3 supplies a real value for that parameter.

> **Why the window source lands here and not in Task 3.** This task commits a
> manifest stamped `schema: "triffview.combat-log-export/1"`. That string is a
> contract, so `/1` has to mean one thing forever — emitting it without
> `window.source` and adding the field later would silently redefine a published
> version. The whole schema ships in this commit, defaulting to `"unspecified"`;
> Task 3 only replaces that default with the truth.

- [ ] **Step 1: Fix the three existing tests that assert exact archive contents**

Do this **first**, before adding anything. Three tests assert the archive's complete key set and will fail the moment a manifest entry exists. They are not wrong — they need to say "the logs are exactly these" rather than "the archive contains exactly these."

Add this helper to `tests/TriffView.Tests/CombatLogExportTests.cs`, directly below the existing `ReadArchive` method:

```csharp
    /// <summary>The archive's game logs, excluding the manifest.</summary>
    private static Dictionary<string, string> ReadLogs(string zipPath)
    {
        return ReadArchive(zipPath)
            .Where(entry => entry.Key != CombatLogExport.ManifestEntryName)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
    }
```

Then change these three lines:

- Line 215, in `OnlyLogsOverlappingTheWindowAreCollected`:
  ```csharp
  // from
  Assert.Equal(new[] { "during.txt" }, ReadArchive(zip).Keys.ToArray());
  // to
  Assert.Equal(new[] { "during.txt" }, ReadLogs(zip).Keys.ToArray());
  ```
- Line 263, in `LogsAreCopiedVerbatimUnderTheirOwnNames` — change the `entries` assignment (the assertions on line 264-265 need no edit):
  ```csharp
  // from
  var entries = ReadArchive(zip);
  // to
  var entries = ReadLogs(zip);
  ```
- Line 331, in `ExportingTwiceToTheSameFileReplacesIt`:
  ```csharp
  // from
  Assert.Equal(new[] { "during.txt" }, ReadArchive(zip).Keys.ToArray());
  // to
  Assert.Equal(new[] { "during.txt" }, ReadLogs(zip).Keys.ToArray());
  ```

- [ ] **Step 2: Write the failing tests**

Add `using System.Globalization;` and `using System.Text.Json;` to the top of the test file. Neither is covered by the project's implicit usings. Add this helper below `ReadLogs`:

```csharp
    private static JsonElement ReadManifest(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry(CombatLogExport.ManifestEntryName);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open());
        // Clone: the JsonDocument is disposed on the way out and the caller
        // would otherwise be handed elements backed by freed memory.
        return JsonDocument.Parse(reader.ReadToEnd()).RootElement.Clone();
    }

    private static string[] StringsAt(JsonElement element, string property)
    {
        return element.GetProperty(property).EnumerateArray()
            .Select(item => item.GetString()!).ToArray();
    }
```

Then add these tests at the end of the class, before the closing brace:

```csharp
    [Fact]
    public void TheArchiveCarriesAManifest()
    {
        using var dir = new TempDir();
        dir.WriteLog("during.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(10));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        var manifest = ReadManifest(zip);
        Assert.Equal("triffview.combat-log-export/1", manifest.GetProperty("schema").GetString());
        Assert.Equal("TriffView", manifest.GetProperty("tool").GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            manifest.GetProperty("tool").GetProperty("version").GetString()));
        Assert.Equal(0, manifest.GetProperty("droppedFileCount").GetInt32());

        // The window is the whole basis for what got collected, so it has to
        // survive into the archive exactly, and in UTC.
        var window = manifest.GetProperty("window");
        Assert.Equal(Noon, DateTime.Parse(
            window.GetProperty("startUtc").GetString()!,
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        Assert.Equal(Noon.AddMinutes(5), DateTime.Parse(
            window.GetProperty("endUtc").GetString()!,
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

        // Value is a clock read and cannot be asserted, but the shape can:
        // "non-blank" would happily accept "banana".
        var exported = DateTime.Parse(
            manifest.GetProperty("exportedUtc").GetString()!,
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, exported.Kind);
    }

    [Fact]
    public void TheManifestIsWrittenWithoutAByteOrderMark()
    {
        // Read as bytes on purpose: StreamReader silently swallows a BOM, so a
        // test that decodes through it cannot see the thing being asserted.
        // A leading BOM breaks strict JSON parsers, Python's json.loads included.
        using var dir = new TempDir();
        dir.WriteLog("during.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(10));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        using var archive = ZipFile.OpenRead(zip);
        using var stream = archive.GetEntry(CombatLogExport.ManifestEntryName)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();

        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Equal((byte)'{', bytes[0]);
    }

    [Fact]
    public void TheManifestGroupsEverySessionFileUnderItsCharacter()
    {
        // A pilot who relogged mid-fight has two session files; both belong to
        // the one character, and the whole point of the manifest is that the
        // characters in it are one human's.
        using var dir = new TempDir();
        dir.WriteLog("20260814_115000_98000001.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(1));
        dir.WriteLog("20260814_120200_98000001.txt", "Alpha", Noon.AddMinutes(2), Noon.AddMinutes(10));
        dir.WriteLog("20260814_115500_98000002.txt", "Bravo", Noon.AddMinutes(-5), Noon.AddMinutes(5));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        var characters = ReadManifest(zip).GetProperty("operator")
            .GetProperty("characters").EnumerateArray().ToArray();

        Assert.Equal(2, characters.Length);
        Assert.Equal("Alpha", characters[0].GetProperty("name").GetString());
        Assert.Equal(98000001, characters[0].GetProperty("id").GetInt64());
        Assert.Equal(
            new[] { "20260814_115000_98000001.txt", "20260814_120200_98000001.txt" },
            StringsAt(characters[0], "files"));
        Assert.Equal("Bravo", characters[1].GetProperty("name").GetString());
        Assert.Equal(98000002, characters[1].GetProperty("id").GetInt64());
    }

    [Fact]
    public void ACharacterWithNoIdInItsFilenameIsListedWithANullId()
    {
        using var dir = new TempDir();
        dir.WriteLog("20260814_090000.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(10));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        var character = ReadManifest(zip).GetProperty("operator")
            .GetProperty("characters").EnumerateArray().Single();

        Assert.Equal("Alpha", character.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, character.GetProperty("id").ValueKind);
    }

    [Fact]
    public void ALogWithNoListenerHeaderIsNotAttributedToAnyone()
    {
        // Exported today with an empty character name and no way to notice it.
        // It cannot be claimed as anyone's, so it is listed separately.
        //
        // The Session Started header is required even though Listener is not:
        // without it TryDescribeLog falls back to the file's creation time,
        // which is now, putting the log outside the window entirely.
        using var dir = new TempDir();
        var orphan = System.IO.Path.Combine(dir.Path, "headerless.txt");
        File.WriteAllText(orphan,
            "------------------------------------------------------------\n"
            + "  Gamelog\n"
            + $"  Session Started: {Noon.AddMinutes(-10):yyyy.MM.dd HH:mm:ss}\n"
            + "------------------------------------------------------------\n"
            + "[ 2026.08.14 12:01:00 ] (combat) 142 to HostileOne\n");
        File.SetLastWriteTimeUtc(orphan, Noon.AddMinutes(1));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        var manifest = ReadManifest(zip);
        Assert.Empty(manifest.GetProperty("operator").GetProperty("characters").EnumerateArray());
        Assert.Equal(new[] { "headerless.txt" }, StringsAt(manifest, "unattributedFiles"));
    }

    [Fact]
    public void TheManifestReportsLogsDroppedByTheFileCap()
    {
        // The cap is 64. An export that quietly covered part of a window would
        // read as complete coverage in the resulting report, so the count of
        // what was left out has to survive into the archive.
        using var dir = new TempDir();
        for (var i = 0; i < 65; i++)
        {
            dir.WriteLog(
                $"20260814_1150{i:D2}_980000{i:D2}.txt", $"Pilot{i:D2}",
                Noon.AddMinutes(-10), Noon.AddSeconds(i));
        }

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        var result = CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        Assert.Equal(64, result.FileCount);
        Assert.Equal(1, ReadManifest(zip).GetProperty("droppedFileCount").GetInt32());
    }

    [Fact]
    public void ADetectedFightIsRecordedAsSuchInTheManifest()
    {
        using var dir = new TempDir();
        dir.WriteLog("during.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(10));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        CombatLogExport.Export(
            dir.Path, Noon, Noon.AddMinutes(5), zip, CombatLogWindowSource.LastFight);

        Assert.Equal("last-fight",
            ReadManifest(zip).GetProperty("window").GetProperty("source").GetString());
    }

    [Fact]
    public void AHandTypedRangeIsRecordedAsSuchInTheManifest()
    {
        using var dir = new TempDir();
        dir.WriteLog("during.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(10));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        CombatLogExport.Export(
            dir.Path, Noon, Noon.AddMinutes(5), zip, CombatLogWindowSource.ManualRange);

        Assert.Equal("manual-range",
            ReadManifest(zip).GetProperty("window").GetProperty("source").GetString());
    }

    [Fact]
    public void AnUnstatedWindowSourceSaysSoRatherThanGuessing()
    {
        using var dir = new TempDir();
        dir.WriteLog("during.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(10));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        Assert.Equal("unspecified",
            ReadManifest(zip).GetProperty("window").GetProperty("source").GetString());
    }

    [Fact]
    public void ADetectedFightWindowKnowsItWasDetected()
    {
        var fight = CombatLogExport.DetectLastFight(NewestFirst(Alert("attack", "Alpha", Noon)));

        Assert.Equal(CombatLogWindowSource.LastFight, fight!.Source);
    }

    [Fact]
    public void TheManifestDoesNotInflateTheReportedLogCount()
    {
        // The settings panel renders "Exported N logs". Counting the manifest
        // there would be a quiet UI regression.
        using var dir = new TempDir();
        dir.WriteLog("during.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(10));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        var result = CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        Assert.Equal(1, result.FileCount);
        Assert.Equal(2, ReadArchive(zip).Count);
    }

    [Fact]
    public void TheManifestIsNotMistakableForAGameLog()
    {
        // eve-intel globs *.txt and keys a header-less file as a phantom pilot.
        // The .json extension is the contract, not a cosmetic choice.
        using var dir = new TempDir();
        dir.WriteLog("during.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(10));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        Assert.Equal(new[] { "during.txt" },
            ReadArchive(zip).Keys.Where(name => name.EndsWith(".txt")).ToArray());
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/TriffView.Tests/TriffView.Tests.csproj -p:EnableSourceControlManagerQueries=false`

Expected: FAIL to compile with `CS0117: 'CombatLogExport' does not contain a definition for 'ManifestEntryName'` and `CS0246: The type or namespace name 'CombatLogWindowSource' could not be found`. After Step 4 adds those but before the manifest is written, the new tests fail on `Assert.NotNull(entry)` instead. Either is a correct red.

- [ ] **Step 4: Write the implementation**

Add `using System.Reflection;`, `using System.Text.Json;` to the top of `native/TriffAlerts/CombatLogExport.cs`. `System.Text` is already imported at line 4.

Add the enum above `CombatLogFightWindow`:

```csharp
/// <summary>Where an export's time window came from.</summary>
public enum CombatLogWindowSource
{
    /// <summary>Not stated by the caller -- distinct from "not known".</summary>
    Unspecified,

    /// <summary>Derived from the run of combat alerts in the history.</summary>
    LastFight,

    /// <summary>Typed by hand as a UTC range.</summary>
    ManualRange,
}
```

Add the property to `CombatLogFightWindow`, below `AlertCount`:

```csharp
    public CombatLogWindowSource Source { get; init; } = CombatLogWindowSource.Unspecified;
```

In `DetectLastFight`, add to the returned object initializer, after `AlertCount = cluster.Count,`:

```csharp
            Source = CombatLogWindowSource.LastFight,
```

Add the constant and serializer options beside `DiscordAttachmentLimitBytes` (around line 83):

```csharp
    /// <summary>
    /// Name of the manifest entry. Deliberately not <c>*.txt</c>: eve-intel's
    /// parser globs <c>*.txt</c> and keys any file lacking a "Listener:" header
    /// as a phantom pilot, so the extension is load-bearing.
    /// </summary>
    public const string ManifestEntryName = "triffview-manifest.json";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
    };
```

In `Export`, change the signature to accept the window's provenance:

```csharp
    public static CombatLogExportResult Export(
        string gamelogsPath, DateTime startUtc, DateTime endUtc, string destinationZipPath,
        CombatLogWindowSource source = CombatLogWindowSource.Unspecified)
```

The parameter is optional so the existing call site and every current test
compile untouched; Task 3 supplies the real value.

Then change only the archive block (currently lines 186-200) so the manifest is written before disposal, and keep `rawBytes` as logs-only:

```csharp
        long rawBytes;
        try
        {
            using (var archive = ZipFile.Open(stagingPath, ZipArchiveMode.Create))
            {
                // rawBytes stays logs-only: it means "bytes of game log"
                // everywhere it surfaces, and the manifest is not one.
                rawBytes = selected.Sum(log => CopyIntoArchive(archive, log));
                WriteManifest(archive, selected, startUtc, endUtc, source, droppedFiles);
            }

            File.Move(stagingPath, destinationZipPath, overwrite: true);
        }
        catch
        {
            TryDelete(stagingPath);
            throw;
        }
```

Add `WriteManifest` directly below `CopyIntoArchive`:

```csharp
    /// <summary>
    /// Records what this archive is and whose characters are in it. Every log
    /// came from one machine's Gamelogs folder, and one machine is one player,
    /// so the characters listed under "operator" are one human's -- a fact
    /// eve-intel cannot recover from the logs, which say who was listening and
    /// never who was at the keyboard.
    /// </summary>
    private static void WriteManifest(
        ZipArchive archive, List<SelectedLog> selected,
        DateTime startUtc, DateTime endUtc, CombatLogWindowSource source, int droppedFiles)
    {
        var characters = selected
            .Where(log => !string.IsNullOrWhiteSpace(log.CharacterName))
            .GroupBy(log => log.CharacterName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                name = group.Key,
                id = group
                    .Select(log => TryParseCharacterId(log.EntryName, out var id) ? id : (long?)null)
                    .FirstOrDefault(value => value != null),
                files = group
                    .Select(log => log.EntryName)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray(),
            })
            .ToArray();

        var manifest = new
        {
            schema = "triffview.combat-log-export/1",
            tool = new { name = "TriffView", version = ToolVersion },
            exportedUtc = DateTime.UtcNow.ToString("O"),
            window = new
            {
                startUtc = startUtc.ToString("O"),
                endUtc = endUtc.ToString("O"),
                source = SourceToken(source),
            },
            // "operator" is a C# keyword; the @ is stripped when serialized.
            @operator = new { characters },
            unattributedFiles = selected
                .Where(log => string.IsNullOrWhiteSpace(log.CharacterName))
                .Select(log => log.EntryName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray(),
            droppedFileCount = droppedFiles,
        };

        var entry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        // No BOM: a leading byte-order mark breaks strict JSON parsers.
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(manifest, ManifestJsonOptions));
    }

    /// <summary>
    /// Reported so a reader can tell which build produced an archive. Read from
    /// this type's own assembly, which is the test assembly under test -- hence
    /// asserted for shape rather than value.
    /// </summary>
    private static string ToolVersion =>
        typeof(CombatLogExport).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <summary>
    /// Spelled out rather than using JsonStringEnumConverter, which would emit
    /// "LastFight" and put the wire format at the mercy of a rename.
    /// </summary>
    private static string SourceToken(CombatLogWindowSource source) => source switch
    {
        CombatLogWindowSource.LastFight => "last-fight",
        CombatLogWindowSource.ManualRange => "manual-range",
        _ => "unspecified",
    };
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/TriffView.Tests/TriffView.Tests.csproj -p:EnableSourceControlManagerQueries=false`

Expected: PASS, 88 total (76 + 12 new), 0 failed. If `TheManifestGroupsEverySessionFileUnderItsCharacter` fails on ordering, check that `characters` is sorted by name and `files` by ordinal — both orderings are asserted.

- [ ] **Step 6: Commit**

```bash
git add native/TriffAlerts/CombatLogExport.cs tests/TriffView.Tests/CombatLogExportTests.cs
git commit -m "Write an export manifest into the combat log archive"
```

---

### Task 3: Wire the real window source at the call site

**Files:**
- Modify: `native/TriffView/TriffViewSubsystem.cs:1156` (the `Export` call) and `:1195` (the manual-range return)

**Interfaces:**
- Consumes: `CombatLogWindowSource`, `CombatLogFightWindow.Source`, and the optional `source` parameter on `Export` — all from Task 2.
- Produces: nothing new. This task only replaces the `Unspecified` default with the truth.

> **No unit test in this task, deliberately.** `TriffViewSubsystem.cs` is a
> `net8.0-windows` WPF file and is **not** among the sources linked into the test
> project (`tests/TriffView.Tests/TriffView.Tests.csproj:14-27`), so no xUnit test
> can reach this code. The compiler and a manual check are the available
> verification, and pretending otherwise with a test that exercises something
> else would be worse than saying so. Task 2 already covers every value this
> task can produce.

- [ ] **Step 1: Pass the window's source into the export**

In `native/TriffView/TriffViewSubsystem.cs`, at line 1156:

```csharp
            var result = await Task.Run(() => CombatLogExport.Export(
                gamelogsPath, window.StartUtc, window.EndUtc, destination, window.Source));
```

- [ ] **Step 2: Mark the hand-typed branch**

In `BuildCombatLogWindow`, the manual-range return at line 1195 — **not** line 1185, which is the `DetectLastFight` branch and already carries `LastFight` from Task 2:

```csharp
        return new CombatLogFightWindow
        {
            StartUtc = start,
            EndUtc = end,
            Source = CombatLogWindowSource.ManualRange,
        };
```

- [ ] **Step 3: Verify the WPF project compiles**

The test project does not compile `TriffViewSubsystem.cs`, so both edits are unverified until this runs.

Run: `dotnet build native/TriffView.csproj -c Release -p:EnableSourceControlManagerQueries=false -p:EnableWindowsTargeting=true`

Expected: `Build succeeded.` with 0 warnings, 0 errors.

- [ ] **Step 4: Confirm the test suite is still green**

Run: `dotnet test tests/TriffView.Tests/TriffView.Tests.csproj -p:EnableSourceControlManagerQueries=false`

Expected: PASS, 88 total, 0 failed — unchanged from Task 2. This task adds no tests; the run is here to catch an accidental edit to shared code.

- [ ] **Step 5: Commit**

```bash
git add native/TriffView/TriffViewSubsystem.cs
git commit -m "Tell the export where its window came from"
```

---

### Task 4: Correct the copy that says the archive holds only game logs

**Files:**
- Modify: `app/src/tools/TriffViewSettings.jsx:1826-1828`
- Modify: `README.md:59`

**Interfaces:**
- Consumes: `ManifestEntryName` from Task 2 (by name only — this task writes prose, not code).
- Produces: nothing.

Both statements become false once a manifest ships. The substance survives — logs *are* still byte-for-byte and chat logs *are* still never included — so this is one added clause each, not a rewrite.

- [ ] **Step 1: Update the settings panel copy**

In `app/src/tools/TriffViewSettings.jsx`, replace the paragraph at lines 1826-1828:

```jsx
            <p className="triffview-muted">
              Packages the EVE game logs covering a fight into a zip you can upload to Discord for
              eve-intel. Game logs only, copied as-is, plus a small manifest listing your characters.
              Chat logs are never included.
            </p>
```

- [ ] **Step 2: Update the README**

In `README.md`, replace line 59:

```markdown
Logs are copied exactly as EVE wrote them, one file per character session, and only from the Gamelogs folder. The zip also carries a small manifest naming the characters it covers, so the report can tell they are all yours. Chat logs are never included.
```

- [ ] **Step 3: Verify no stale "logs only" claim remains**

Run: `grep -rn "logs only\|Logs are copied exactly\|copied as-is" README.md app/src/tools/TriffViewSettings.jsx`

Expected: both hits now mention the manifest. Nothing else in either file claims the archive contains only logs.

- [ ] **Step 4: Verify the frontend still builds**

The JSX edit is inside a JSX expression block and is not covered by any test or by the `dotnet` builds. Release CI runs this at `.github/workflows/release.yml:31`, so a broken edit would not surface until release.

Run: `cd app && npm ci && npm run build`

Expected: `vite build` completes with no errors. Return to the repository root afterwards.

- [ ] **Step 5: Commit**

```bash
git add README.md app/src/tools/TriffViewSettings.jsx
git commit -m "Say that the export archive carries a manifest"
```

---

## Final verification

- [ ] Full suite green: `dotnet test tests/TriffView.Tests/TriffView.Tests.csproj -c Release -p:EnableSourceControlManagerQueries=false` — expect 88 passed, 0 failed.
- [ ] WPF build clean: `dotnet build native/TriffView.csproj -c Release -p:EnableSourceControlManagerQueries=false -p:EnableWindowsTargeting=true` — expect 0 warnings, 0 errors.
- [ ] Frontend build clean: `cd app && npm ci && npm run build` — expect no errors.
- [ ] Inspect a real archive by hand — the one thing the tests cannot judge is whether the JSON reads well to a human opening it in Discord.
- [ ] Confirm no local-only build flag leaked into a committed file: `git grep -n "EnableWindowsTargeting\|EnableSourceControlManagerQueries"`. Expect **no matches outside this plan**. (`git diff --stat` cannot answer this — it reports changed-line counts, not contents.)

## Out of scope

The eve-intel change that consumes this manifest — widening `own` to every character the manifest lists, falling back to current behavior when it is absent, and surfacing provenance in the report. Separate repository, separate PR. Until it lands, the manifest is inert by design: `parse_fights.py` globs `*.txt` and never opens it.
