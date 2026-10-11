using Dashboard.Api.Telegram;

namespace Dashboard.Tests;

public class ExpenseMessageParserTests
{
    [Fact]
    public void ParsesAmountWhatAndWhy()
    {
        var result = ExpenseMessageParser.Parse("4.50 coffee late for train");

        Assert.True(result.HighConfidence);
        Assert.Equal(4.50m, result.Amount);
        Assert.Equal("coffee", result.Description);
        Assert.Equal("late for train", result.Why);
    }

    [Fact]
    public void ParsesDashWhyAndCommaAmount()
    {
        var result = ExpenseMessageParser.Parse("12,30 taxi - rain");

        Assert.True(result.HighConfidence);
        Assert.Equal(12.30m, result.Amount);
        Assert.Equal("taxi", result.Description);
        Assert.Equal("rain", result.Why);
    }

    [Fact]
    public void UnclearTextIsLowConfidence()
    {
        var result = ExpenseMessageParser.Parse("bought snacks earlier");

        Assert.False(result.HighConfidence);
        Assert.Null(result.Amount);
    }

    [Fact]
    public void ParsesCategoryThenAmount()
    {
        var result = ExpenseMessageParser.Parse("Food 10");

        Assert.True(result.HighConfidence);
        Assert.Equal(10m, result.Amount);
        Assert.Equal("Food", result.Category);
        Assert.Equal("Food", result.Description);
    }
}
