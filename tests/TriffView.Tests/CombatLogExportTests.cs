using System.IO.Compression;
using System.Text;
using TriffView.Alerts;
using Xunit;

namespace TriffView.Tests;

/// <summary>
/// The export hands EVE Gamelogs to eve-intel's fight-aar skill, which reads the
/// archive with <c>parse_fights.py --zip</c>. Two things it depends on are easy
/// to break silently: the window that decides which logs are collected, and the
/// bytes of the logs themselves, which the parser reads with its own grammar and
/// pilot-identity rules.
/// </summary>
public class CombatLogExportTests
{
    private static readonly DateTime Noon = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    private static TriffAlertEvent Alert(string type, string character, DateTime whenUtc, bool test = false)
    {
        return new TriffAlertEvent
        {
            Type = type,
            CharacterName = character,
            TimestampUtc = whenUtc,
            Test = test,
        };
    }

    // The service hands out history newest-first; detection is specified against
    // that order, so the tests build it the same way.
    private static TriffAlertEvent[] NewestFirst(params TriffAlertEvent[] alerts)
    {
        return alerts.OrderByDescending(alert => alert.TimestampUtc).ToArray();
    }

    [Fact]
    public void NoHistoryMeansNoFight()
    {
        Assert.Null(CombatLogExport.DetectLastFight(Array.Empty<TriffAlertEvent>()));
    }

    [Fact]
    public void AmbientAlertsAreNotAFight()
    {
        // Fleet invites and system changes say nothing about shooting, and a
        // window built from them would collect logs for a quiet evening.
        var history = NewestFirst(
            Alert("fleet_invite", "Alpha", Noon),
            Alert("system_change", "Alpha", Noon.AddMinutes(1)),
            Alert("decloak", "Alpha", Noon.AddMinutes(2)));

        Assert.Null(CombatLogExport.DetectLastFight(history));
    }

    [Fact]
    public void TestAlertsDoNotInventAFight()
    {
        // Firing a test alert from the settings panel must not leave a phantom
        // fight sitting in the export UI.
        var history = NewestFirst(Alert("attack", "Alpha", Noon, test: true));

        Assert.Null(CombatLogExport.DetectLastFight(history));
    }

    [Fact]
    public void TheWindowSpansTheClusterAndNamesItsPilots()
    {
        var history = NewestFirst(
            Alert("attack", "Alpha", Noon),
            Alert("warp_scramble", "Bravo", Noon.AddMinutes(1)),
            Alert("attack", "Alpha", Noon.AddMinutes(2)));

        var fight = CombatLogExport.DetectLastFight(history);

        Assert.NotNull(fight);
        Assert.Equal(Noon, fight!.StartUtc);
        Assert.Equal(Noon.AddMinutes(2), fight.EndUtc);
        Assert.Equal(3, fight.AlertCount);
        Assert.Equal(new[] { "Alpha", "Bravo" }, fight.Characters);
    }

    [Fact]
    public void OnlyTheMostRecentFightIsDetected()
    {
        // An earlier engagement in the same session must not be merged into this
        // one -- the combined window would drag in unrelated logs and, worse,
        // read as a single fight in the report.
        var history = NewestFirst(
            Alert("attack", "Alpha", Noon),
            Alert("attack", "Alpha", Noon.AddMinutes(1)),
            Alert("attack", "Alpha", Noon.AddMinutes(40)),
            Alert("attack", "Alpha", Noon.AddMinutes(41)));

        var fight = CombatLogExport.DetectLastFight(history);

        Assert.NotNull(fight);
        Assert.Equal(Noon.AddMinutes(40), fight!.StartUtc);
        Assert.Equal(Noon.AddMinutes(41), fight.EndUtc);
        Assert.Equal(2, fight.AlertCount);
    }

    [Theory]
    [InlineData(179, 2)] // just inside the gap
    [InlineData(180, 2)] // exactly the gap is still one fight
    [InlineData(181, 1)] // past it, the older alert belongs to a previous fight
    public void ClusteringSplitsOnAQuietGap(int gapSeconds, int expectedAlerts)
    {
        var history = NewestFirst(
            Alert("attack", "Alpha", Noon),
            Alert("attack", "Alpha", Noon.AddSeconds(gapSeconds)));

        var fight = CombatLogExport.DetectLastFight(history);

        Assert.NotNull(fight);
        Assert.Equal(expectedAlerts, fight!.AlertCount);
    }

    [Fact]
    public void APilotAlertingRepeatedlyIsNamedOnce()
    {
        var history = NewestFirst(
            Alert("attack", "Alpha Pilot", Noon),
            Alert("attack", "alpha pilot", Noon.AddSeconds(30)),
            Alert("attack", "", Noon.AddSeconds(45)));

        var fight = CombatLogExport.DetectLastFight(history);

        Assert.NotNull(fight);
        // Which casing survives is not worth pinning down -- both spellings came
        // from EVE. That there is exactly one entry, and no blank one, is.
        Assert.Equal("Alpha Pilot", Assert.Single(fight!.Characters), ignoreCase: true);
    }

    [Fact]
    public void FileNameCarriesTheWindowAndPilotCount()
    {
        var fight = CombatLogExport.DetectLastFight(NewestFirst(
            Alert("attack", "Alpha", Noon),
            Alert("attack", "Bravo", Noon.AddSeconds(10))));

        Assert.Equal("triffview-fight-20260814-1200Z-2pilots.zip", CombatLogExport.SuggestFileName(fight!));
    }

    [Fact]
    public void FileNameStaysSingularForOnePilot()
    {
        var fight = CombatLogExport.DetectLastFight(NewestFirst(Alert("attack", "Alpha", Noon)));

        Assert.Equal("triffview-fight-20260814-1200Z-1pilot.zip", CombatLogExport.SuggestFileName(fight!));
    }

    // ---- Export ----

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("triffview-export-tests").FullName;

        public string WriteLog(string name, string listener, DateTime sessionStartUtc, DateTime lastWriteUtc, string body = "")
        {
            var full = System.IO.Path.Combine(Path, name);
            // A real Gamelog header: the session start is what bounds the log's
            // span, and "Listener:" is the pilot identity the parser keys off.
            var text = new StringBuilder()
                .AppendLine("------------------------------------------------------------")
                .AppendLine("  Gamelog")
                .AppendLine($"  Listener: {listener}")
                .AppendLine($"  Session Started: {sessionStartUtc:yyyy.MM.dd HH:mm:ss}")
                .AppendLine("------------------------------------------------------------")
                .Append(body)
                .ToString();
            File.WriteAllText(full, text);
            File.SetLastWriteTimeUtc(full, lastWriteUtc);
            return full;
        }

        public void Dispose()
        {
            // Cleanup must never fail a test that already passed: Directory.Delete
            // throws UnauthorizedAccessException for a read-only or still-mapped
            // file, which is not an IOException.
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Dictionary<string, string> ReadArchive(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var reader = new StreamReader(entry.Open());
                return reader.ReadToEnd();
            });
    }

    [Fact]
    public void OnlyLogsOverlappingTheWindowAreCollected()
    {
        using var dir = new TempDir();
        dir.WriteLog("during.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(10));
        // Ended well before the window, and outside the padding either side.
        dir.WriteLog("earlier.txt", "Bravo", Noon.AddHours(-5), Noon.AddHours(-4));
        // Starts well after it.
        dir.WriteLog("later.txt", "Charlie", Noon.AddHours(4), Noon.AddHours(5));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        var result = CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        Assert.Equal(1, result.FileCount);
        Assert.Equal(new[] { "Alpha" }, result.Characters);
        Assert.Equal(new[] { "during.txt" }, ReadArchive(zip).Keys.ToArray());
    }

    [Fact]
    public void ASessionStartingJustBeforeTheFightIsStillCollected()
    {
        // The padding exists for this: a client logged in a couple of minutes
        // before the shooting started is a participant, and whole files are
        // exported anyway, so widening costs a few compressed KB.
        using var dir = new TempDir();
        dir.WriteLog("just-before.txt", "Alpha", Noon.AddMinutes(-2), Noon.AddMinutes(-1));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        var result = CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public void EveryLogForAPilotIsCollected()
    {
        // A pilot who relogged mid-fight has two session files. The parser
        // handles several files per pilot; dropping one loses half their fight.
        using var dir = new TempDir();
        dir.WriteLog("session-one.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(1));
        dir.WriteLog("session-two.txt", "Alpha", Noon.AddMinutes(2), Noon.AddMinutes(10));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        var result = CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        Assert.Equal(2, result.FileCount);
        Assert.Equal(new[] { "Alpha" }, result.Characters);
    }

    [Fact]
    public void LogsAreCopiedVerbatimUnderTheirOwnNames()
    {
        // The parser strips EVE's markup itself and keys pilots off the header,
        // so anything that rewrites, trims or renames the contents breaks it.
        using var dir = new TempDir();
        var body = "[ 2026.08.14 12:01:00 ] (combat) <color=0xffcc0000><b>142</b> to " +
                   "<b>HostileOne[HSTL](Loki)</b><font size=10> - Hits</font>\n";
        var source = dir.WriteLog("20260814_115000_98000001.txt", "Alpha Pilot", Noon.AddMinutes(-10), Noon.AddMinutes(10), body);
        var expected = File.ReadAllText(source);

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        var entries = ReadArchive(zip);
        Assert.Equal(new[] { "20260814_115000_98000001.txt" }, entries.Keys.ToArray());
        Assert.Equal(expected, entries["20260814_115000_98000001.txt"]);
    }

    [Fact]
    public void ALogHeldOpenByARunningClientIsStillExported()
    {
        // EVE keeps its Gamelog open for writing for as long as the client runs,
        // which is exactly the case a fight export cares about. Anything that
        // opens the file without FileShare.ReadWrite -- ZipFile.CreateEntryFromFile
        // among them -- fails on every log belonging to a live client.
        using var dir = new TempDir();
        var path = dir.WriteLog("live.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(10));

        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            var zip = System.IO.Path.Combine(dir.Path, "out.zip");
            var result = CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

            Assert.Equal(1, result.FileCount);
        }
    }

    [Fact]
    public void AWindowWithNoLogsIsAnError()
    {
        // Silence must not produce an empty archive that reads as "nothing
        // happened" once it reaches the report.
        using var dir = new TempDir();
        dir.WriteLog("elsewhere.txt", "Alpha", Noon.AddHours(-9), Noon.AddHours(-8));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");

        Assert.Throws<InvalidOperationException>(
            () => CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip));
    }

    [Fact]
    public void AReversedWindowIsReadTheRightWayRound()
    {
        using var dir = new TempDir();
        dir.WriteLog("during.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(10));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        var result = CombatLogExport.Export(dir.Path, Noon.AddMinutes(5), Noon, zip);

        Assert.Equal(1, result.FileCount);
        Assert.Equal(Noon, result.StartUtc);
        Assert.Equal(Noon.AddMinutes(5), result.EndUtc);
    }

    [Fact]
    public void ExportingTwiceToTheSameFileReplacesIt()
    {
        // The save dialog prompts before overwriting but does not delete the old
        // archive, and the suggested name is derived from the fight's start
        // minute -- so re-exporting the same fight lands on the same path. The
        // second run must replace it rather than fail on a file that is already
        // there.
        using var dir = new TempDir();
        dir.WriteLog("during.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(10));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);
        var result = CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        Assert.Equal(1, result.FileCount);
        Assert.Equal(new[] { "during.txt" }, ReadArchive(zip).Keys.ToArray());
    }

    [Fact]
    public void ASuccessfulExportLeavesNoStagingFileBehind()
    {
        // The archive is built beside its destination and moved into place, so a
        // leftover staging file would sit in whichever folder the pilot exported
        // to -- and, when that is the Gamelogs folder, next to the logs.
        using var dir = new TempDir();
        dir.WriteLog("during.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(10));

        var zip = System.IO.Path.Combine(dir.Path, "out.zip");
        CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), zip);

        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void AFailedExportLeavesNoStagingFileBehind()
    {
        // The staging file must not survive a failed run. A directory sitting at
        // the destination path fails the move into place after the archive has
        // been written, which is the one failure mode reachable without a seam
        // in the production code -- and it exercises the same cleanup path a
        // full disk or a vanishing log would.
        using var dir = new TempDir();
        dir.WriteLog("during.txt", "Alpha", Noon.AddMinutes(-10), Noon.AddMinutes(10));

        var occupied = System.IO.Path.Combine(dir.Path, "out.zip");
        Directory.CreateDirectory(occupied);

        Assert.ThrowsAny<IOException>(
            () => CombatLogExport.Export(dir.Path, Noon, Noon.AddMinutes(5), occupied));

        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void AMissingGamelogsFolderIsAnError()
    {
        Assert.Throws<InvalidOperationException>(() => CombatLogExport.Export(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "triffview-no-such-folder-9d3f"),
            Noon,
            Noon.AddMinutes(5),
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "out.zip")));
    }
}
