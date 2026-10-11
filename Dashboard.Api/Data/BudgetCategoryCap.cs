namespace Dashboard.Api.Data;

public class BudgetCategoryCap
{
    public long Id { get; set; }

    public long AccountId { get; set; }

    public string Category { get; set; } = "";

    public decimal MonthlyCap { get; set; }

    public int AlertYearMonth { get; set; }

    public bool OverAlertSent { get; set; }
}
