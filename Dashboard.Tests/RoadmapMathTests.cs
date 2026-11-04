using Dashboard.Api.Budget;

namespace Dashboard.Tests;

public class RoadmapMathTests
{
    [Theory]
    [InlineData(2026, 10, 2026, 10, 1)]
    [InlineData(2026, 10, 2026, 11, 2)]
    [InlineData(2026, 10, 2027, 10, 13)]
    [InlineData(2026, 10, 2028, 12, 27)] // Oct 26 … Dec 28 inclusive
    [InlineData(2026, 10, 2025, 1, 0)] // finish before start
    public void MonthsInclusive_counts_calendar_months(
        int fy, int fm, int ty, int tm, int expected)
    {
        var from = new DateOnly(fy, fm, 15);
        var to = new DateOnly(ty, tm, 28);
        Assert.Equal(expected, RoadmapMath.MonthsInclusive(from, to));
    }

    [Fact]
    public void ForecastSquaresUntil_dec2028_from_oct2026_is_26()
    {
        // Current Oct drawn as "now"; Nov 26 … Dec 28 = 26 dashed squares.
        var today = new DateOnly(2026, 10, 7);
        var finish = new DateOnly(2028, 12, 30);
        Assert.Equal(26, RoadmapMath.ForecastSquaresUntil(today, finish));
        Assert.Equal(27, RoadmapMath.MonthsInclusive(today, finish));
    }

    [Fact]
    public void ForecastSquaresUntil_same_month_is_zero()
    {
        var day = new DateOnly(2026, 10, 20);
        Assert.Equal(0, RoadmapMath.ForecastSquaresUntil(day, day));
    }

    [Theory]
    [InlineData(24120, 26, 927.69)] // 24120/26
    [InlineData(0, 26, 0)]
    [InlineData(1000, 0, 0)]
    [InlineData(1000, 4, 250)]
    public void RequiredPace_splits_remaining(decimal remaining, int months, decimal expected)
    {
        Assert.Equal(expected, RoadmapMath.RequiredPace(remaining, months));
    }

    [Fact]
    public void Finish_date_timeline_not_keep_based_50()
    {
        // User bug: UI said "50 squares at keep 500" while finish was Dec 2028 (~27 mo).
        var today = new DateOnly(2026, 10, 7);
        var finish = new DateOnly(2028, 12, 30);
        var squares = RoadmapMath.MonthsInclusive(today, finish);
        Assert.Equal(27, squares);
        Assert.NotEqual(50, squares);
        Assert.Equal(26, RoadmapMath.ForecastSquaresUntil(today, finish));
    }
}
