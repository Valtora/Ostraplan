using Ostraplan.Core;
using Xunit;

namespace Ostraplan.Tests;

/// <summary>
/// The ship-rating cutoffs are hardcoded ports of <c>Ship.CalculateRating</c> (the memory warns they must be
/// re-verified after a game patch). These lock every boundary so a shifted cutoff — from a patch or a refactor —
/// is caught immediately. Pure functions; no install.
/// </summary>
public class RatingGradeTests
{
    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "Small")]
    [InlineData(249, "Small")]
    [InlineData(250, "Medium")]
    [InlineData(899, "Medium")]
    [InlineData(900, "Lunamax")]
    [InlineData(1599, "Lunamax")]
    [InlineData(1600, "Ceresmax")]
    [InlineData(2299, "Ceresmax")]
    [InlineData(2300, "Titanmax")]
    [InlineData(2999, "Titanmax")]
    [InlineData(3000, "Very Large")]
    [InlineData(3699, "Very Large")]
    [InlineData(3700, "Ultra Large")]
    public void SizeClass_boundaries(int area, string expected) => Assert.Equal(expected, Rating.SizeClass(area));

    [Theory]
    [InlineData(0, "O")]
    [InlineData(-5, "O")]
    [InlineData(1, "A")]
    [InlineData(299.9, "A")]
    [InlineData(300, "B")]
    [InlineData(499.9, "B")]
    [InlineData(500, "C")]
    [InlineData(749.9, "C")]
    [InlineData(750, "D")]
    [InlineData(1499.9, "D")]
    [InlineData(1500, "E")]
    [InlineData(9000, "E")]
    public void ManeuverGrade_boundaries(double n, string expected) => Assert.Equal(expected, Rating.ManeuverGrade(n));

    [Theory]
    [InlineData(0.0, "E")]
    [InlineData(0.5, "E")]
    [InlineData(0.51, "D")]
    [InlineData(0.8, "D")]
    [InlineData(0.81, "C")]
    [InlineData(0.95, "C")]
    [InlineData(0.96, "B")]
    [InlineData(0.99, "B")]
    [InlineData(1.0, "A")]
    public void ConditionGrade_boundaries(double n, string expected) => Assert.Equal(expected, Rating.ConditionGrade(n));
}
