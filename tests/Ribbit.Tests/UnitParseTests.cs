using Ribbit.Geometry.Clash;
using Xunit;

namespace Ribbit.Tests;

public class UnitParseTests
{
    private const double Tol = 1e-9;

    [Theory]
    [InlineData("25mm", 25.0 / 304.8)]
    [InlineData("2.5cm", 2.5 / 30.48)]
    [InlineData("1m", 1.0 / 0.3048)]
    [InlineData("6in", 0.5)]
    [InlineData("6\"", 0.5)]
    [InlineData("2ft", 2.0)]
    [InlineData("2'", 2.0)]
    public void SuffixedValues_ConvertToFeet(string input, double expected)
        => Assert.Equal(expected, UnitParse.ToFeet(input)!.Value, Tol);

    [Fact]
    public void ABareNumberIsAlreadyFeet()
        => Assert.Equal(3.5, UnitParse.ToFeet("3.5")!.Value, Tol);

    // "mm" and "cm" have to be tested before the bare "m", or 25mm reads as 25
    // metres - a 3000x error straight into the clash pad.
    [Fact]
    public void MillimetresAreNotMistakenForMetres()
    {
        double mm = UnitParse.ToFeet("25mm")!.Value;
        double m = UnitParse.ToFeet("25m")!.Value;

        Assert.Equal(25.0 / 304.8, mm, Tol);
        Assert.True(m > mm * 100);
    }

    [Fact]
    public void WhitespaceAndCaseAreIgnored()
        => Assert.Equal(25.0 / 304.8, UnitParse.ToFeet("  25 MM ")!.Value, Tol);

    [Fact]
    public void NegativeAndZeroAreAccepted()
    {
        Assert.Equal(0.0, UnitParse.ToFeet("0")!.Value, Tol);
        Assert.Equal(-1.0, UnitParse.ToFeet("-1ft")!.Value, Tol);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("mm")]
    [InlineData("1.2.3")]
    public void UnparseableValuesReturnNull(string? input)
        => Assert.Null(UnitParse.ToFeet(input));

    // ALL spaces are stripped, not just the outer ones - which is what lets
    // "25 mm" work, but also means an interior space is silently closed up
    // rather than rejected. Faithful to the Python; documented here so the
    // behaviour is a decision rather than a surprise.
    [Fact]
    public void InteriorSpacesAreStripped_NotTreatedAsASeparator()
        => Assert.Equal(1234.0 / 304.8, UnitParse.ToFeet("12 34 mm")!.Value, Tol);

    // A settings file written on a comma-decimal machine must not read "2,5" as
    // 25 - that is a tenfold clash pad, silently.
    [Fact]
    public void CommaDecimalsAreRejectedRatherThanMisread()
        => Assert.Null(UnitParse.ToFeet("2,5"));
}
