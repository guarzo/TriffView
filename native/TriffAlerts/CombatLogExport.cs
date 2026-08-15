using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TriffView.Alerts;

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

/// <summary>
/// A detected stretch of fighting, derived from the alert history.
/// Timestamps are UTC, which is also EVE time and the timestamp base used
/// inside the Gamelog files themselves, so no conversion happens anywhere
/// between here and the exported archive.
/// </summary>
public sealed class CombatLogFightWindow
{
    public DateTime StartUtc { get; init; }
    public DateTime EndUtc { get; init; }
    public IReadOnlyList<string> Characters { get; init; } = Array.Empty<string>();
    public int AlertCount { get; init; }
    public CombatLogWindowSource Source { get; init; } = CombatLogWindowSource.Unspecified;

    public object ToState()
    {
        return new
        {
            startUtc = StartUtc.ToString("O"),
            endUtc = EndUtc.ToString("O"),
            characters = Characters,
            alertCount = AlertCount,
        };
    }
}

public sealed class CombatLogExportResult
{
    public string Path { get; init; } = "";
    public int FileCount { get; init; }
    public IReadOnlyList<string> Characters { get; init; } = Array.Empty<string>();
    public long RawBytes { get; init; }
    public long ZipBytes { get; init; }
    public DateTime StartUtc { get; init; }
    public DateTime EndUtc { get; init; }

    /// <summary>
    /// Matching logs left out because the file cap was hit. Reported rather
    /// than swallowed: an export that quietly covered only part of a window
    /// would read as complete coverage in the resulting AAR.
    /// </summary>
    public int DroppedFileCount { get; init; }

    public bool ExceedsDiscordLimit => ZipBytes > CombatLogExport.DiscordAttachmentLimitBytes;

    public object ToState()
    {
        return new
        {
            path = Path,
            fileCount = FileCount,
            characters = Characters,
            rawBytes = RawBytes,
            zipBytes = ZipBytes,
            startUtc = StartUtc.ToString("O"),
            endUtc = EndUtc.ToString("O"),
            droppedFileCount = DroppedFileCount,
            exceedsDiscordLimit = ExceedsDiscordLimit,
        };
    }
}

/// <summary>
/// Packages EVE Gamelogs into a zip that eve-intel's fight-aar skill can
/// consume directly (<c>parse_fights.py --zip</c>), for manual upload to Discord.
///
/// Log files are copied verbatim. The parser keys every pilot off the
/// "Listener:" header and does its own markup stripping, so rewriting,
/// trimming or merging the contents would only break it -- merging in
/// particular, since the first Listener in a file is applied to every line
/// after it. One archive entry per source file, always.
/// </summary>
public static class CombatLogExport
{
    /// <summary>Discord's default per-file attachment limit for users without Nitro.</summary>
    public const long DiscordAttachmentLimitBytes = 10L * 1024 * 1024;

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

    /// <summary>Alert types that mean "a fight is happening", as opposed to ambient awareness.</summary>
    private static readonly string[] FightAlertTypes = { "attack", "warp_scramble" };

    /// <summary>Quiet stretch that separates one fight from the next.</summary>
    private static readonly TimeSpan FightGap = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Slack added to the detected window before matching it against log files.
    /// Files are exported whole, so this only ever widens *which files* qualify --
    /// it never affects their contents, and generosity here costs a few
    /// compressed KB against the risk of missing a session that started just
    /// before the shooting did.
    /// </summary>
    private static readonly TimeSpan WindowPadding = TimeSpan.FromMinutes(5);

    /// <summary>Guard against an absurd export if a window somehow spans the whole log directory.</summary>
    private const int MaxFiles = 64;

    private static readonly Regex SessionStartedRegex = new(
        @"^\s*Session Started:\s*(\d{4})\.(\d{2})\.(\d{2}) (\d{2}):(\d{2}):(\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ListenerRegex = new(
        @"^\s*Listener:\s*(.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Finds the most recent run of combat alerts. <paramref name="history"/> is
    /// expected newest-first, the order <see cref="TriffAlertsService.History"/>
    /// returns. Returns null when nothing combat-related is in the buffer.
    ///
    /// Note the history is in-memory and capped, so this only ever sees the
    /// current app session -- callers need a manual-window fallback for anything
    /// older, which is why <see cref="Export"/> takes a window rather than
    /// detecting one itself.
    /// </summary>
    public static CombatLogFightWindow? DetectLastFight(IReadOnlyList<TriffAlertEvent> history)
    {
        if (history == null || history.Count == 0) return null;

        var candidates = history
            .Where(alert => !alert.Test && FightAlertTypes.Contains(alert.Type, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(alert => alert.TimestampUtc)
            .ToArray();
        if (candidates.Length == 0) return null;

        var cluster = new List<TriffAlertEvent> { candidates[0] };
        for (var i = 1; i < candidates.Length; i++)
        {
            if (cluster[^1].TimestampUtc - candidates[i].TimestampUtc > FightGap) break;
            cluster.Add(candidates[i]);
        }

        return new CombatLogFightWindow
        {
            StartUtc = cluster[^1].TimestampUtc,
            EndUtc = cluster[0].TimestampUtc,
            Characters = cluster
                .Select(alert => alert.CharacterName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            AlertCount = cluster.Count,
            Source = CombatLogWindowSource.LastFight,
        };
    }

    /// <summary>
    /// Writes every Gamelog overlapping [<paramref name="startUtc"/>, <paramref name="endUtc"/>]
    /// into a zip at <paramref name="destinationZipPath"/>. Throws
    /// <see cref="InvalidOperationException"/> when no log overlaps the window.
    /// </summary>
    public static CombatLogExportResult Export(
        string gamelogsPath, DateTime startUtc, DateTime endUtc, string destinationZipPath,
        CombatLogWindowSource source = CombatLogWindowSource.Unspecified)
    {
        if (endUtc < startUtc) (startUtc, endUtc) = (endUtc, startUtc);
        if (!Directory.Exists(gamelogsPath))
        {
            throw new InvalidOperationException($"Gamelogs folder not found: {gamelogsPath}");
        }

        var selected = SelectLogs(
            gamelogsPath, startUtc - WindowPadding, endUtc + WindowPadding, out var droppedFiles);
        if (selected.Count == 0)
        {
            throw new InvalidOperationException(
                "No EVE logs overlap that time window. Check that log files exist in " +
                $"{gamelogsPath} and that the window is in UTC (EVE time).");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrEmpty(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);

        // Built beside the destination and moved into place, for two reasons.
        // ZipFile.Open(Create) opens with FileMode.CreateNew and throws when the
        // file already exists, and the save dialog's overwrite prompt does not
        // delete the old archive -- so re-exporting a fight, whose suggested
        // name is fixed by its start minute, would always fail. Staging also
        // keeps a run that dies partway through from leaving a truncated zip
        // that reads as a complete export.
        var stagingPath = destinationZipPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

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

        return new CombatLogExportResult
        {
            Path = destinationZipPath,
            FileCount = selected.Count,
            Characters = selected
                .Select(log => log.CharacterName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            RawBytes = rawBytes,
            ZipBytes = new FileInfo(destinationZipPath).Length,
            StartUtc = startUtc,
            EndUtc = endUtc,
            DroppedFileCount = droppedFiles,
        };
    }

    /// <summary>Self-describing name, since these land in a Discord channel among many.</summary>
    public static string SuggestFileName(CombatLogFightWindow window)
    {
        var pilots = window.Characters.Count;
        var suffix = pilots > 0 ? $"-{pilots}pilot{(pilots == 1 ? "" : "s")}" : "";
        return $"triffview-fight-{window.StartUtc:yyyyMMdd-HHmm}Z{suffix}.zip";
    }

    /// <summary>
    /// Every log whose session overlaps the window. Deliberately a fresh
    /// directory scan rather than a read of the live tracking set: that set
    /// retires older files and keeps only the newest log per character, so a
    /// pilot who relogged mid-fight would silently lose their first session.
    /// The parser handles several files per pilot, so over-collecting is safe
    /// and under-collecting is not.
    /// </summary>
    private static List<SelectedLog> SelectLogs(string gamelogsPath, DateTime windowStartUtc, DateTime windowEndUtc, out int dropped)
    {
        var matched = new List<SelectedLog>();
        foreach (var path in Directory.EnumerateFiles(gamelogsPath, "*.txt"))
        {
            var log = TryDescribeLog(path, windowStartUtc, windowEndUtc);
            if (log != null) matched.Add(log.Value);
        }

        // Newest last-write first, so the guard drops the least relevant logs
        // rather than whichever the filesystem happened to enumerate last.
        dropped = Math.Max(0, matched.Count - MaxFiles);
        return matched
            .OrderByDescending(log => log.LastWriteUtc)
            .Take(MaxFiles)
            .ToList();
    }

    private static SelectedLog? TryDescribeLog(string path, DateTime windowStartUtc, DateTime windowEndUtc)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return null;

            ReadHeader(path, out var listener, out var sessionStartUtc);
            // A log spans from its session start to its last write. Creation
            // time is the fallback when the header is unreadable -- a file
            // still being written by a live client can race the header read.
            var spanStart = sessionStartUtc ?? info.CreationTimeUtc;
            var spanEnd = info.LastWriteTimeUtc;
            if (spanEnd < spanStart) spanEnd = spanStart;
            if (spanStart > windowEndUtc || spanEnd < windowStartUtc) return null;

            return new SelectedLog(path, info.Name, listener, spanEnd);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies one log into the archive, returning its uncompressed size.
    /// Opened with the same sharing the alert tail uses: EVE holds its logs
    /// open for writing, so anything stricter (including
    /// <c>CreateEntryFromFile</c>, which asks for FileShare.Read) fails on
    /// every log belonging to a running client -- exactly the ones a fight
    /// export needs.
    /// </summary>
    private static long CopyIntoArchive(ZipArchive archive, SelectedLog log)
    {
        using var source = new FileStream(
            log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var entry = archive.CreateEntry(log.EntryName, CompressionLevel.Optimal);
        // The zip format cannot represent anything earlier than 1980, and the
        // setter throws rather than clamping. A bogus filesystem timestamp must
        // not take down the whole export.
        if (log.LastWriteUtc.Year >= 1980) entry.LastWriteTime = log.LastWriteUtc;
        using var destination = entry.Open();
        source.CopyTo(destination);
        // Bytes actually read, not FileInfo.Length: a live client may extend the
        // log while it is being copied.
        return source.Position;
    }

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

    /// <summary>
    /// Best-effort staging cleanup. A failure here must not replace the original
    /// export error with a less useful one about a temporary file.
    /// </summary>
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

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

    private static void ReadHeader(string path, out string listener, out DateTime? sessionStartUtc)
    {
        listener = "";
        sessionStartUtc = null;
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);

        for (var i = 0; i < 30 && !reader.EndOfStream; i++)
        {
            var line = reader.ReadLine() ?? "";
            if (listener.Length == 0)
            {
                var listenerMatch = ListenerRegex.Match(line);
                // "EVE" is the placeholder listener on non-character logs.
                if (listenerMatch.Success && !listenerMatch.Groups[1].Value.Equals("EVE", StringComparison.OrdinalIgnoreCase))
                {
                    listener = listenerMatch.Groups[1].Value;
                }
            }

            if (sessionStartUtc == null)
            {
                var sessionMatch = SessionStartedRegex.Match(line);
                if (sessionMatch.Success)
                {
                    // EVE writes this header in EVE time, which is UTC.
                    sessionStartUtc = new DateTime(
                        int.Parse(sessionMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                        int.Parse(sessionMatch.Groups[2].Value, CultureInfo.InvariantCulture),
                        int.Parse(sessionMatch.Groups[3].Value, CultureInfo.InvariantCulture),
                        int.Parse(sessionMatch.Groups[4].Value, CultureInfo.InvariantCulture),
                        int.Parse(sessionMatch.Groups[5].Value, CultureInfo.InvariantCulture),
                        int.Parse(sessionMatch.Groups[6].Value, CultureInfo.InvariantCulture),
                        DateTimeKind.Utc);
                }
            }

            if (listener.Length > 0 && sessionStartUtc != null) return;
        }
    }

    private readonly record struct SelectedLog(
        string Path, string EntryName, string CharacterName, DateTime LastWriteUtc);
}
