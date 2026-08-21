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
