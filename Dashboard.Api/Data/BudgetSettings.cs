namespace Dashboard.Api.Data;

public class BudgetSettings
{
    public int Id { get; set; }

    public long AccountId { get; set; }

    /// <summary>Full salary / income for the month (EUR).</summary>
    public decimal MonthlyIncome { get; set; }

    /// <summary>Spend envelope used by alerts/forecast (= Income − Keep).</summary>
    public decimal MonthlyLimit { get; set; }

    /// <summary>How much to keep / leave from this month's salary (EUR).</summary>
    public decimal SavingsTarget { get; set; }

    /// <summary>Year pile to hit — Roadmap bracket (EUR). Separate from monthly keep.</summary>
    public decimal YearGoal { get; set; }

    /// <summary>Label for the year pile (e.g. Trip, Emergency).</summary>
    public string? YearGoalName { get; set; }

    public DateOnly? SavingsDeadline { get; set; }

    /// <summary>yyyyMM of the month the last monthly alert applied to.</summary>
    public int AlertYearMonth { get; set; }

    /// <summary>Highest monthly percent checkpoint already sent (20/50/80/90/100).</summary>
    public int AlertLevel { get; set; }

    /// <summary>ISO-like year*100+week for weekly alerts.</summary>
    public int WeeklyAlertYearWeek { get; set; }

    /// <summary>Highest weekly percent checkpoint already sent (80/100).</summary>
    public int WeeklyAlertLevel { get; set; }

    /// <summary>Money you may spend = salary − keep.</summary>
    public decimal SpendBudget =>
        MonthlyIncome > 0
            ? Math.Max(0, decimal.Round(MonthlyIncome - SavingsTarget, 2))
            : MonthlyLimit;

    public void SyncSpendFromPaycheck()
    {
        if (MonthlyIncome > 0)
        {
            if (SavingsTarget > MonthlyIncome)
            {
                SavingsTarget = MonthlyIncome;
            }

            MonthlyLimit = SpendBudget;
        }
    }
}
