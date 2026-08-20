using TriffView.Preview;
using Xunit;

namespace TriffView.Tests;

/// <summary>
/// Pins the property the window sweep's filter ordering depends on.
///
/// EveWindowTracker.GetClients used to fetch every visible window's title before checking which
/// process owned it. That made the sweep cost O(all visible desktop windows) in string-allocating
/// P/Invokes to find a handful of EVE clients, so the process check was hoisted ahead of the
/// title fetch.
///
/// That reordering is only behaviour-preserving because rejecting a window on its process name is
/// unconditional - no title can rescue a window whose process is not an EVE client. If someone
/// later adds a title-based exception to LooksLikeEve, the hoisted check in GetClients would
/// silently start dropping windows the predicate would have accepted, and nothing else in the
/// codebase would catch it. These tests fail if that property is broken.
/// </summary>
public class EveWindowTrackerFilterTests
{
    [Theory]
    [InlineData("EVE - Some Pilot")]
    [InlineData("Some Pilot - EVE")]
    [InlineData("EVE Online")]
    [InlineData("anything at all")]
    public void ANonEveProcessIsRejectedWhateverTheTitleSays(string title)
    {
        Assert.False(EveWindowTracker.IsEveClientProcess("chrome"));
        Assert.False(EveWindowTracker.LooksLikeEve(title, "chrome"));
    }

    [Fact]
    public void ProcessRejectionImpliesPredicateRejection()
    {
        // The exact implication GetClients relies on, stated directly: for every combination of
        // title and process name, if the hoisted process check rejects, the full predicate must
        // reject too - otherwise the hoisted check drops a window that should have been kept.
        string[] titles = ["EVE - Pilot", "Pilot - EVE", "EVE Online", "", "  ", "x", "EVE Launcher"];
        string[] processes = ["chrome", "explorer", "exefile", "evelauncher", "", "EXEFILE"];

        foreach (var title in titles)
        {
            foreach (var process in processes)
            {
                if (EveWindowTracker.IsEveClientProcess(process)) continue;
                Assert.False(
                    EveWindowTracker.LooksLikeEve(title, process),
                    $"process '{process}' is rejected by the hoisted check but LooksLikeEve " +
                    $"accepted title '{title}' - the sweep's filter order would drop this window");
            }
        }
    }

    [Fact]
    public void TheProcessCheckIsCaseInsensitive()
    {
        // The hoisted check must match the predicate's own comparison, or clients whose process
        // name differs in case would be filtered out before the title is ever read.
        Assert.True(EveWindowTracker.IsEveClientProcess("exefile"));
        Assert.True(EveWindowTracker.IsEveClientProcess("EXEFILE"));
        Assert.True(EveWindowTracker.IsEveClientProcess("ExeFile"));
    }

    [Fact]
    public void AnEveProcessStillHasItsTitleConditionsApplied()
    {
        // Hoisting the process check must not skip the title conditions - they still run after
        // the title is fetched.
        Assert.True(EveWindowTracker.LooksLikeEve("EVE - Pilot", "exefile"));
        Assert.False(EveWindowTracker.LooksLikeEve("EVE Launcher", "exefile"));
        Assert.False(EveWindowTracker.LooksLikeEve("ab", "exefile"));
    }
}
