namespace Dashboard.Api.Budget;

/// <summary>Pure helpers for Keep roadmap — unit-tested without DI.</summary>
public static class RoadmapMath
{
    public static DateOnly MonthStart(DateOnly day) => new(day.Year, day.Month, 1);

    /// <summary>Inclusive month count from fromMonth through toMonth (day-of-month ignored).</summary>
    public static int MonthsInclusive(DateOnly fromMonth, DateOnly toMonth)
    {
        fromMonth = MonthStart(fromMonth);
        toMonth = MonthStart(toMonth);
        if (toMonth < fromMonth)
        {
            return 0;
        }

        return (toMonth.Year - fromMonth.Year) * 12 + (toMonth.Month - fromMonth.Month) + 1;
    }

    /// <summary>
    /// How many forecast squares after the current month until the finish month (inclusive of finish).
    /// Current month is already drawn as "now".
    /// </summary>
    public static int ForecastSquaresUntil(DateOnly today, DateOnly finishBy)
    {
        var thisMonth = MonthStart(today);
        var finishMonth = MonthStart(finishBy);
        var total = MonthsInclusive(thisMonth, finishMonth);
        return Math.Max(0, total - 1);
    }

    /// <summary>€/mo needed on remaining forecast months to hit remaining pile.</summary>
    public static decimal RequiredPace(decimal remaining, int forecastMonths)
    {
        if (forecastMonths <= 0 || remaining <= 0)
        {
            return 0;
        }

        return decimal.Round(remaining / forecastMonths, 2);
    }
}
