namespace Dashboard.Api.Budget;

public sealed record CategorySpend(string Category, decimal Amount);

public sealed record MonthForecast(
    int Day,
    int DaysInMonth,
    int DaysLeft,
    decimal Spent,
    decimal AvgPerDay,
    decimal ProjectedMonth,
    decimal MonthlyLimit,
    bool HasLimit,
    decimal? ProjectedLeftover,
    decimal ProjectedYearSpend,
    decimal? ProjectedYearLeftover,
    BudgetBand LeftoverBand,
    IReadOnlyList<CategorySpend> Categories,
    decimal SavingsTarget = 0,
    DateOnly? SavingsDeadline = null,
    decimal? SavingsGoalAmount = null,
    decimal? DailyToSavingsGoal = null,
    decimal MonthlyIncome = 0);
