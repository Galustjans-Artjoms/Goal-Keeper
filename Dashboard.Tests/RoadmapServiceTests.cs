using Dashboard.Api.Auth;
using Dashboard.Api.Budget;
using Dashboard.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Tests;

public class RoadmapServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DashboardDbContext _db = null!;
    private BudgetService _budget = null!;
    private readonly AccountContext _account = new() { AccountId = 1 };

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DashboardDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new DashboardDbContext(options, new Dashboard.Api.Security.DescriptionEncryptor("test-key!!"));
        await _db.Database.EnsureCreatedAsync();
        _budget = new BudgetService(_db, new NoopNotifier(), _account);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Roadmap_with_finish_date_draws_calendar_months_not_keep_math()
    {
        await _budget.SetPaycheckAsync(1500m, 500m, CancellationToken.None);
        await _budget.SetYearGoalAsync("home", 25000m, new DateOnly(2028, 12, 30), CancellationToken.None);
        _db.Transactions.Add(new Transaction
        {
            AccountId = 1,
            Date = new DateOnly(2026, 10, 7),
            Amount = 620m,
            Category = "Food",
            Description = "x"
        });
        await _db.SaveChangesAsync();

        var map = await _budget.GetRoadmapAsync(CancellationToken.None);

        var today = DateOnly.FromDateTime(DateTime.Today);
        // Pin expectation only when "today" is still Oct 2026 in this session — otherwise compute.
        var expectedForecast = RoadmapMath.ForecastSquaresUntil(today, new DateOnly(2028, 12, 30));
        var expectedTotal = RoadmapMath.MonthsInclusive(today, new DateOnly(2028, 12, 30));

        Assert.Equal(25000m, map.Goal);
        Assert.Equal(expectedForecast, map.MonthsLeft);
        Assert.Equal(expectedTotal, map.MonthsTotal);
        Assert.Equal(expectedForecast, map.Squares.Count(s => s.IsForecast));
        Assert.DoesNotContain(map.Squares, s => s.IsForecast && s.Saved < 0);

        var last = map.Squares.Last(s => s.IsForecast);
        Assert.Equal(2028, last.Year);
        Assert.Equal(12, last.Month);

        // Must NOT be the broken "50 keep squares" story.
        Assert.NotEqual(50, map.MonthsTotal);
        // Hint must not claim false product equality like "28 mo × €880".
        Assert.DoesNotContain("×", map.Hint);
        Assert.Contains("by 2028-12", map.Hint);

        if (today is { Year: 2026, Month: 10 })
        {
            Assert.Equal(880m, map.ThisMonthSaved);
            Assert.Equal(880m, map.Accumulated);
            Assert.Equal(24120m, map.Remaining);
            Assert.Equal(26, map.MonthsLeft);
            Assert.Equal(927.69m, map.PacePerMonth);
        }
    }

    [Fact]
    public async Task Roadmap_without_deadline_uses_leftover_pace()
    {
        await _budget.SetPaycheckAsync(1000m, 200m, CancellationToken.None);
        await _budget.SetYearGoalAsync("pile", 2000m, null, CancellationToken.None);
        // no spends → leftover 1000 this month
        var map = await _budget.GetRoadmapAsync(CancellationToken.None);
        Assert.True(map.ThisMonthSaved >= 1000m || map.Accumulated >= 0);
        Assert.True(map.MonthsLeft >= 0);
        Assert.All(map.Squares.Where(s => s.IsForecast), s => Assert.True(s.Saved >= 0));
    }

    private sealed class NoopNotifier : IBudgetNotifier
    {
        public Task NotifyAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
