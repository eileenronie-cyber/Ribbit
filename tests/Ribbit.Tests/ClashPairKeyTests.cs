using Ribbit.Geometry.Clash;
using Xunit;

namespace Ribbit.Tests;

/// <summary>
/// The host-vs-host search draws both sets from one pool, so the engine reports
/// each real clash twice with the sides swapped. Collapsing those depends
/// entirely on the key ignoring order.
/// </summary>
public class ClashPairKeyTests
{
    [Fact]
    public void APairAndItsMirrorShareOneKey()
        => Assert.Equal(ClashPairKey.Symmetric(400, 100), ClashPairKey.Symmetric(100, 400));

    [Fact]
    public void TheKeyIsTheTwoIdsAscending()
        => Assert.Equal((100L, 400L), ClashPairKey.Symmetric(400, 100));

    [Fact]
    public void DifferentPairsKeepDifferentKeys()
        => Assert.NotEqual(ClashPairKey.Symmetric(100, 400), ClashPairKey.Symmetric(100, 401));

    // An element paired with itself is noise, but the key must still be stable
    // rather than throwing or collapsing into a neighbouring pair.
    [Fact]
    public void TheSameIdOnBothSidesIsItsOwnKey()
        => Assert.Equal((100L, 100L), ClashPairKey.Symmetric(100, 100));

    // Ids are 64-bit from Revit 2024 on; ordering must not fall back to int.
    [Fact]
    public void LargeIdsOrderCorrectly()
        => Assert.Equal((4_000_000_000L, 8_000_000_000L),
            ClashPairKey.Symmetric(8_000_000_000L, 4_000_000_000L));
}
