using Xunit;

namespace TriffView.Tests;

/// <summary>
/// Update checks used to hardcode the upstream repo. Now the repo slug is a build input
/// (an AssemblyMetadataAttribute baked in at publish time), normalized and turned into the
/// releases page / latest-release API URLs by these pure helpers.
/// </summary>
public class TriffViewUpdateCheckerTests
{
    [Theory]
    [InlineData("guarzo/TriffView", "guarzo/TriffView")]
    [InlineData("  guarzo/TriffView  ", "guarzo/TriffView")]
    [InlineData("/guarzo/TriffView/", "guarzo/TriffView")]
    [InlineData(null, TriffViewUpdateChecker.DefaultUpdateRepository)]
    [InlineData("", TriffViewUpdateChecker.DefaultUpdateRepository)]
    [InlineData("   ", TriffViewUpdateChecker.DefaultUpdateRepository)]
    [InlineData("justonesegment", TriffViewUpdateChecker.DefaultUpdateRepository)]
    [InlineData("too/many/segments", TriffViewUpdateChecker.DefaultUpdateRepository)]
    [InlineData("owner/", TriffViewUpdateChecker.DefaultUpdateRepository)]
    [InlineData("/repo", TriffViewUpdateChecker.DefaultUpdateRepository)]
    [InlineData("owner//repo", TriffViewUpdateChecker.DefaultUpdateRepository)]
    public void NormalizeRepositorySlugFallsBackToUpstreamForBadInput(string? slug, string expected)
    {
        Assert.Equal(expected, TriffViewUpdateChecker.NormalizeRepositorySlug(slug));
    }

    [Fact]
    public void BuildReleasesPageUrlUsesTheGivenSlug()
    {
        Assert.Equal(
            "https://github.com/guarzo/TriffView/releases",
            TriffViewUpdateChecker.BuildReleasesPageUrl("guarzo/TriffView"));
    }

    [Fact]
    public void BuildReleasesPageUrlFallsBackToUpstreamForBadSlug()
    {
        Assert.Equal(
            "https://github.com/NarcisussX/TriffView/releases",
            TriffViewUpdateChecker.BuildReleasesPageUrl(null));
    }

    [Fact]
    public void BuildLatestReleaseApiUrlUsesTheGivenSlug()
    {
        Assert.Equal(
            "https://api.github.com/repos/guarzo/TriffView/releases/latest",
            TriffViewUpdateChecker.BuildLatestReleaseApiUrl("guarzo/TriffView"));
    }

    [Fact]
    public void BuildLatestReleaseApiUrlFallsBackToUpstreamForBadSlug()
    {
        Assert.Equal(
            "https://api.github.com/repos/NarcisussX/TriffView/releases/latest",
            TriffViewUpdateChecker.BuildLatestReleaseApiUrl(""));
    }

    // The test assembly carries no UpdateRepository metadata attribute, so the type's static
    // fields resolve to the same default the shipped app uses when built without -p:UpdateRepository.
    [Fact]
    public void DefaultResolvedUrlsMatchUpstreamWhenNoMetadataIsPresent()
    {
        Assert.Equal(TriffViewUpdateChecker.DefaultUpdateRepository, TriffViewUpdateChecker.UpdateRepository);
        Assert.Equal("https://github.com/NarcisussX/TriffView/releases", TriffViewUpdateChecker.ReleasesPageUrl);
    }
}
