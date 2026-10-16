using Ribbit.Geometry.Sheets;
using Xunit;

namespace Ribbit.Tests;

public class NaturalSortTests
{
    private static List<string> Sorted(params string[] input)
    {
        List<string> list = [.. input];
        list.Sort(NaturalSort.Comparer);
        return list;
    }

    // The whole reason this exists: a plain string sort puts A10 before A2.
    [Fact]
    public void DigitRuns_CompareNumerically_NotLexically()
        => Assert.Equal(["A2", "A10", "A100"], Sorted("A100", "A10", "A2"));

    [Fact]
    public void PlainStringSort_WouldGetThisWrong()
    {
        List<string> lexical = ["A100", "A10", "A2"];
        lexical.Sort(StringComparer.OrdinalIgnoreCase);

        // Documents what we are NOT doing - lexical order really does invert it.
        Assert.Equal(["A10", "A100", "A2"], lexical);
    }

    [Fact]
    public void PrefixesSortBeforeDigits()
        => Assert.Equal(["A1", "A2", "M1", "M2"], Sorted("M2", "A2", "M1", "A1"));

    [Fact]
    public void MultipleDigitRuns_AreEachCompared()
        => Assert.Equal(["A1-2", "A1-10", "A2-1"], Sorted("A2-1", "A1-10", "A1-2"));

    [Fact]
    public void ComparisonIsCaseInsensitive()
        => Assert.Equal(0, NaturalSort.Comparer.Compare("a10", "A10"));

    [Fact]
    public void PureNumbers_SortNumerically()
        => Assert.Equal(["2", "10", "100"], Sorted("100", "10", "2"));

    // A long job-code run would overflow int; parsing as long keeps it ordered.
    [Fact]
    public void VeryLongDigitRuns_DoNotOverflow()
        => Assert.Equal(
            ["J-2147483648", "J-2147483649"],
            Sorted("J-2147483649", "J-2147483648"));

    [Fact]
    public void NullsAndBlanks_AreHandled()
    {
        Assert.Equal(0, NaturalSort.Comparer.Compare(null, null));
        Assert.Equal(0, NaturalSort.Comparer.Compare(null, ""));
        Assert.True(NaturalSort.Comparer.Compare("", "A1") < 0);
    }

    [Fact]
    public void ASharedPrefix_FallsThroughToTheDigits()
        => Assert.Equal(["SK-1", "SK-2", "SK-11"], Sorted("SK-11", "SK-2", "SK-1"));
}
