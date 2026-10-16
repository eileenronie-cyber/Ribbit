using Ribbit.Geometry.Clash;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// <see cref="NewClashes"/> — the rule behind the "New" badge.
///
/// The empty-baseline case is the one that matters: getting it wrong badges
/// every row on a first run, which trains people to ignore the badge.
/// </summary>
public class NewClashesTests
{
    [Fact]
    public void NoBaselineMeansNothingIsNew()
    {
        Assert.Empty(NewClashes.Compute(null, ["a", "b"]));
        Assert.Empty(NewClashes.Compute([], ["a", "b"]));
    }

    [Fact]
    public void NoBaselineIsReportedAsSuch()
    {
        Assert.False(NewClashes.HasBaseline(null));
        Assert.False(NewClashes.HasBaseline([]));
        Assert.True(NewClashes.HasBaseline(["a"]));
    }

    [Fact]
    public void OnlyUnseenKeysAreNew()
    {
        HashSet<string> result = NewClashes.Compute(["a", "b"], ["a", "b", "c"]);

        Assert.Equal(["c"], result);
    }

    [Fact]
    public void NothingChangedMeansNothingNew()
    {
        Assert.Empty(NewClashes.Compute(["a", "b"], ["a", "b"]));
    }

    [Fact]
    public void DisappearedKeysAreNotReportedHere()
    {
        // Gone-since-last-time is the stale table's job. Reporting it here would
        // conflate "appeared" with "vanished" in one badge.
        HashSet<string> result = NewClashes.Compute(["a", "b", "c"], ["a"]);

        Assert.Empty(result);
    }

    [Fact]
    public void EverythingIsNewWhenTheBaselineShareNothing()
    {
        HashSet<string> result = NewClashes.Compute(["x"], ["a", "b"]);

        Assert.Equal(2, result.Count);
        Assert.Contains("a", result);
        Assert.Contains("b", result);
    }

    [Fact]
    public void KeysAreMatchedExactly()
    {
        // Clash keys are built from element ids and link names, not typed by a
        // human, so a case-insensitive match could only ever merge two DIFFERENT
        // clashes into one.
        HashSet<string> result = NewClashes.Compute(["100@|200@ST"], ["100@|200@st"]);

        Assert.Single(result);
    }

    [Fact]
    public void BlankKeysAreIgnored()
    {
        HashSet<string> result = NewClashes.Compute(["a"], ["", "b"]);

        Assert.Equal(["b"], result);
    }

    [Fact]
    public void DuplicateCurrentKeysCollapse()
    {
        // Both rows of a pair carry the same clash key, so the current list
        // arrives with every key twice.
        HashSet<string> result = NewClashes.Compute(["a"], ["b", "b"]);

        Assert.Single(result);
    }
}
