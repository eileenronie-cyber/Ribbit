using Ribbit.Geometry;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// The pick loop behind Connect / Connect Multi and Align / Align Multi. The single and
/// Multi buttons run the same engine; this is where they differ: whether the loop
/// stops after a success, and what the first-pick prompt tells the user.
/// </summary>
public class PickLoopTests
{
    private const string Ask = "Pick the FIXED target element";

    private static PickLoop Single() => new(Ask, "connected", multiple: false);

    private static PickLoop Multi() => new(Ask, "connected", multiple: true);

    // ------------------------------------------------------------------ single

    [Fact]
    public void Single_FirstPrompt_IsJustTheAsk()
        => Assert.Equal(Ask, Single().Prompt);

    [Fact]
    public void Single_StopsAfterTheFirstSuccess()
    {
        PickLoop loop = Single();

        Assert.False(loop.Record(null));
        Assert.Equal(1, loop.Done);
    }

    // The old behaviour ended the command on a failed pair without a word.
    [Fact]
    public void Single_AFailedPair_AsksAgainSayingWhy()
    {
        PickLoop loop = Single();

        Assert.True(loop.Record("No connector pair in the same domain"));
        Assert.Equal(0, loop.Done);
        Assert.Equal(Ask + " (last pair: No connector pair in the same domain; Esc to quit)", loop.Prompt);
    }

    [Fact]
    public void Single_ARetryThatWorks_StopsAndCountsOne()
    {
        PickLoop loop = Single();
        loop.Record("too far");

        Assert.False(loop.Record(null));
        Assert.Equal(1, loop.Done);
        Assert.Null(loop.LastNote);
    }

    [Fact]
    public void Single_HasNoCountInItsPrompt()
    {
        PickLoop loop = Single();
        loop.Record("too far");

        Assert.DoesNotContain("connected", loop.Prompt);
    }

    // ------------------------------------------------------------------- Multi

    [Fact]
    public void Multi_FirstPrompt_CountsZeroAndOffersEsc()
        => Assert.Equal(Ask + " (0 connected, Esc to finish)", Multi().Prompt);

    [Fact]
    public void Multi_KeepsGoingAfterASuccess_AndCountsIt()
    {
        PickLoop loop = Multi();

        Assert.True(loop.Record(null));
        Assert.True(loop.Record(null));
        Assert.Equal(2, loop.Done);
        Assert.Equal(Ask + " (2 connected, Esc to finish)", loop.Prompt);
    }

    [Fact]
    public void Multi_KeepsGoingAfterAFailure_AndShowsWhy()
    {
        PickLoop loop = Multi();
        loop.Record(null);

        Assert.True(loop.Record("already aligned"));
        Assert.Equal(1, loop.Done);
        Assert.Equal(Ask + " (1 connected; last: already aligned, Esc to finish)", loop.Prompt);
    }

    // A reason left on screen after a later success reads as if the success failed.
    [Fact]
    public void Multi_ASuccessClearsTheOldReason()
    {
        PickLoop loop = Multi();
        loop.Record("too far");

        loop.Record(null);

        Assert.Null(loop.LastNote);
        Assert.Equal(Ask + " (1 connected, Esc to finish)", loop.Prompt);
    }

    [Fact]
    public void Multi_ANewFailureReplacesTheOldReason()
    {
        PickLoop loop = Multi();
        loop.Record("too far");

        loop.Record("not parallel");

        Assert.Equal(Ask + " (0 connected; last: not parallel, Esc to finish)", loop.Prompt);
    }

    [Fact]
    public void Multi_UsesTheToolsOwnWord()
    {
        PickLoop loop = new("Pick the FIXED reference pipe", "aligned", multiple: true);
        loop.Record(null);

        Assert.Equal("Pick the FIXED reference pipe (1 aligned, Esc to finish)", loop.Prompt);
    }
}
