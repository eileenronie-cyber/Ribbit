using Ribbit.Geometry;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// The throttle is what keeps a large-model scan from spending more time
/// repainting than working, so its guarantees are pinned here rather than left
/// to inspection.
/// </summary>
public class ProgressThrottleTests
{
    /// <summary>Repaints a full run would actually perform.</summary>
    private static int RepaintCount(int total)
    {
        int last = -1;
        int drawn = 0;

        for (int i = 0; i < total; i++)
        {
            if (!ProgressThrottle.ShouldReport(i, total, last))
                continue;

            last = i;
            drawn++;
        }

        return drawn;
    }

    // The whole point: a huge run costs a bounded number of UI round-trips.
    [Theory]
    [InlineData(10_000)]
    [InlineData(200_000)]
    [InlineData(1_000_000)]
    public void ALargeRunStaysNearTheRepaintCap(int total)
    {
        int drawn = RepaintCount(total);
        Assert.True(drawn <= ProgressThrottle.MaxRepaints + 2,
            $"{total} items drew {drawn} times");
    }

    // Without the throttle this is what every clash call site was doing.
    [Fact]
    public void AHugeRunDrawsFarFewerTimesThanItHasItems()
        => Assert.True(RepaintCount(200_000) < 200_000 / 1000);

    // A short run must not lose updates - the step falls to 1.
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(100)]
    public void AShortRunDrawsEveryItem(int total)
        => Assert.Equal(total, RepaintCount(total));

    // The bar must start from something real rather than sitting empty until
    // the first step boundary.
    [Fact]
    public void TheFirstItemAlwaysDraws()
        => Assert.True(ProgressThrottle.ShouldReport(0, 500_000, -1));

    // ...and must finish full rather than stopping just short.
    [Fact]
    public void TheLastItemAlwaysDraws()
        => Assert.True(ProgressThrottle.ShouldReport(499_999, 500_000, 400_000));

    [Fact]
    public void AnItemInsideTheStepIsSkipped()
        => Assert.False(ProgressThrottle.ShouldReport(1_001, 500_000, 1_000));

    [Fact]
    public void AnItemPastTheStepDraws()
        => Assert.True(ProgressThrottle.ShouldReport(6_000, 500_000, 1_000));

    // An empty run must not divide by zero or loop.
    [Fact]
    public void AnEmptyRunIsHarmless()
    {
        Assert.True(ProgressThrottle.ShouldReport(0, 0, -1));
        Assert.Equal(0, RepaintCount(0));
    }
}
