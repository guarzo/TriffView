using System.IO;
using System.Text;
using System.Text.Json;

namespace TriffView.TriffSkills;

// One downloadable plan file, as named by the GitHub contents listing.
internal sealed record RemotePlanFile(string Name, string DownloadUrl);

internal static class PlanCatalog
{
    // Plan bodies are only ever fetched from GitHub's raw host. download_url is the one
    // field in this listing that chooses a server, so it is validated here, at the point
    // the untrusted document is parsed, rather than being trusted all the way down to
    // HttpClient.
    public const string RawContentHost = "raw.githubusercontent.com";

    // Filters a GET /repos/{owner}/{repo}/contents/{path} response down to the plan
    // files worth downloading. Directory entries carry type "dir" and a null
    // download_url; non-.txt files (the repo currently ships a README.md) are not plans.
    // Throws on malformed JSON rather than returning an empty list, because an empty
    // list is indistinguishable from "upstream deleted every plan" and Commit would
    // happily apply that.
    public static IReadOnlyList<RemotePlanFile> ParseContentsListing(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The GitHub contents listing was not a JSON array.");
        }

        var files = new List<RemotePlanFile>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            var type = element.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
            if (!string.Equals(type, "file", StringComparison.Ordinal)) continue;

            var name = element.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!name!.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;

            var downloadUrl = element.TryGetProperty("download_url", out var urlNode)
                ? urlNode.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(downloadUrl)) continue;

            // Throws rather than skipping. A .txt entry whose download_url points somewhere
            // other than the raw host is not a plan this listing forgot to fill in - it is a
            // listing that is not the one we pinned, and abandoning the whole refresh leaves
            // the previously cached plans in place. Same doctrine as the malformed-JSON throw
            // above: a silent skip would look like "upstream removed that plan".
            if (!IsRawContentUrl(downloadUrl!))
            {
                throw new InvalidDataException(
                    $"The GitHub contents listing pointed '{name}' at '{downloadUrl}', which is not on {RawContentHost}.");
            }

            files.Add(new RemotePlanFile(name, downloadUrl!));
        }

        return files;
    }

    public static bool IsRawContentUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, RawContentHost, StringComparison.OrdinalIgnoreCase);
    }
}

// Owns the on-disk plans directory. Path-injectable and I/O-only so the swap can be
// exercised in tests with no network: the controller downloads, this decides what
// lands on disk and when.
internal static class PlanCache
{
    // Siblings of plansDir, never children: Directory.Move(plansDir, ...) would
    // otherwise drag the staging directory along with it.
    public static string StagingDir(string plansDir) => plansDir + ".new";

    public static string RetiredDir(string plansDir) => plansDir + ".old";

    // Closes the one unsafe window in Commit: a crash after plans\ was moved aside but
    // before plans.new took its place leaves the cache only under plans.old. Put it
    // back rather than silently starting from nothing.
    public static void Recover(string plansDir)
    {
        var retired = RetiredDir(plansDir);
        if (!Directory.Exists(plansDir) && Directory.Exists(retired))
        {
            Directory.Move(retired, plansDir);
            return;
        }

        DeleteIfExists(retired);
    }

    public static string BeginStaging(string plansDir)
    {
        Recover(plansDir);
        var staging = StagingDir(plansDir);
        DeleteIfExists(staging);
        Directory.CreateDirectory(staging);
        return staging;
    }

    public static void WritePlan(string stagingDir, string fileName, string contents)
    {
        // The name comes off the network. Path.GetFileName strips any directory part,
        // and the comparison against the original rejects traversal outright rather
        // than silently writing a differently-named file.
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName)
            || !string.Equals(safeName, fileName, StringComparison.Ordinal)
            || !safeName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Refusing to cache a plan file named '{fileName}'.");
        }

        File.WriteAllText(Path.Combine(stagingDir, safeName), contents, new UTF8Encoding(false));
    }

    // Swaps the staged download in for the live cache. Directory.Move is atomic within
    // a volume, and plansDir, plans.new and plans.old are always siblings, so the swap
    // is two atomic renames rather than a copy that can be interrupted half-done.
    public static void Commit(string plansDir, string stagingDir)
    {
        if (!Directory.EnumerateFiles(stagingDir, "*.txt").Any())
        {
            throw new InvalidDataException("Refusing to replace the plan cache with an empty download.");
        }

        var retired = RetiredDir(plansDir);
        DeleteIfExists(retired);

        if (Directory.Exists(plansDir))
        {
            Directory.Move(plansDir, retired);
        }

        Directory.Move(stagingDir, plansDir);
        DeleteIfExists(retired);
    }

    public static void Abandon(string stagingDir) => DeleteIfExists(stagingDir);

    // Parses every cached .txt into a plan named after its file stem. A file that
    // cannot be read or parsed is skipped rather than failing the whole load - one bad
    // file must not cost the user every other plan.
    //
    // Deliberately broad: File.ReadAllText can throw UnauthorizedAccessException (a
    // permission-denied ACL, a read-only/system attribute, or the "directory where a
    // file was expected" case) as readily as IOException (a lock), and neither derives
    // from the other. SkillPlanParser.Parse tolerates malformed lines internally but is
    // not guaranteed against every pathological input. This is the same per-file
    // isolation contract RefreshCharactersAsync's per-character catch enforces
    // (TriffSkillsController.cs) - one bad item must degrade by exactly one item, so the
    // catch here has to cover whatever that one item can throw, not just the case that
    // was easiest to name.
    public static IReadOnlyList<SkillPlan> LoadAll(string plansDir)
    {
        Recover(plansDir);
        if (!Directory.Exists(plansDir)) return Array.Empty<SkillPlan>();

        var plans = new List<SkillPlan>();
        foreach (var path in Directory.GetFiles(plansDir, "*.txt").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                plans.Add(SkillPlanParser.Parse(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path)));
            }
            catch (Exception)
            {
                // Unreadable (locked, permission-denied, missing) or unparseable: skip
                // this one file, keep the rest.
            }
        }

        return plans;
    }

    private static void DeleteIfExists(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
