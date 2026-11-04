namespace Dashboard.Api.Budget;

public enum BudgetBand
{
    Ok = 0,
    Warn = 1,
    Over = 2
}

public sealed record CategoryCapStatus(
    long Id,
    string Category,
    decimal Cap,
    decimal Spent,
    double Percent,
    BudgetBand Band);

public sealed record BudgetStatus(
    decimal MonthlyLimit,
    decimal Spent,
    decimal Remaining,
    double Percent,
    int HighestThreshold,
    BudgetBand Band,
    bool Enabled,
    decimal WeeklyLimit,
    decimal WeeklySpent,
    double WeeklyPercent,
    BudgetBand WeeklyBand,
    IReadOnlyList<CategoryCapStatus> CategoryCaps,
    decimal MonthlyIncome = 0,
    decimal KeepFromSalary = 0);
