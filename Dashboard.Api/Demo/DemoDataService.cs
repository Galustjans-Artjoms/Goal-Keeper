using Dashboard.Api.Auth;
using Dashboard.Api.Budget;
using Dashboard.Api.Data;
using Dashboard.Api.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Api.Demo;

public sealed class DemoDataService
{
    private readonly DashboardDbContext _db;
    private readonly TransactionService _transactions;
    private readonly BudgetService _budget;
    private readonly AccountContext _account;

    public DemoDataService(
        DashboardDbContext db,
        TransactionService transactions,
        BudgetService budget,
        AccountContext account)
    {
        _db = db;
        _transactions = transactions;
        _budget = budget;
        _account = account;
    }

    public async Task ResetAndSeedAsync(CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        await _transactions.DeleteAllForAccountAsync(cancellationToken);
        await _budget.ResetAccountBudgetAsync(cancellationToken);

        var inbox = await _db.InboxEntries.Where(e => e.AccountId == accountId).ToListAsync(cancellationToken);
        _db.InboxEntries.RemoveRange(inbox);
        var batches = await _db.ImportBatches.Where(b => b.AccountId == accountId).ToListAsync(cancellationToken);
        _db.ImportBatches.RemoveRange(batches);
        var scenarios = await _db.OpScenarios.Where(s => s.AccountId == accountId).ToListAsync(cancellationToken);
        _db.OpScenarios.RemoveRange(scenarios);
        await _db.SaveChangesAsync(cancellationToken);

        // Salary 1000, keep 200 → spend 800; year goal separate (Analysis)
        await _budget.SetPaycheckAsync(1000m, 200m, cancellationToken);
        await _budget.SetYearGoalAsync("Demo year pile", 2400m, null, cancellationToken);
        await _budget.UpsertCategoryCapAsync("Food", 250m, cancellationToken);
        await _budget.UpsertCategoryCapAsync("Travel", 150m, cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var demo = new (int DayOffset, decimal Amount, string Category, string What, string Why)[]
        {
            (0, 4.50m, "Food", "Coffee", "Morning focus"),
            (1, 12.30m, "Travel", "Taxi", "Late for lecture"),
            (2, 28.00m, "Food", "Groceries", "Week stock"),
            (3, 9.90m, "General", "Notebook", "Course notes"),
            (4, 45.00m, "Travel", "Bus pass", "Month commute"),
            (5, 15.00m, "Food", "Lunch", "Campus"),
            (6, 6.20m, "Food", "Snack", "Study break")
        };

        foreach (var row in demo)
        {
            var date = monthStart.AddDays(Math.Min(row.DayOffset, Math.Max(0, today.Day - 1)));
            await _transactions.CreateAsync(
                new TransactionRequest
                {
                    Date = date,
                    Amount = row.Amount,
                    Category = row.Category,
                    Description = row.What,
                    Why = row.Why
                },
                cancellationToken);
        }

        _db.InboxEntries.Add(new InboxEntry
        {
            AccountId = accountId,
            RawText = "something unclear from telegram",
            Date = today,
            Amount = null,
            Category = "General",
            Description = null,
            Why = null,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
    }
}
