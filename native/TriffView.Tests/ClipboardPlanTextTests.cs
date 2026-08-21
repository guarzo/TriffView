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
