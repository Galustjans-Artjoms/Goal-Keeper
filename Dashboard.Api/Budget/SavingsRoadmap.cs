namespace Dashboard.Api.Budget;

public sealed record RoadmapMonth(
    string Label,
    int Year,
    int Month,
    decimal Saved,
    bool IsForecast,
    bool CountsTowardGoal,
    bool IsCurrent = false);

public sealed record SavingsRoadmap(
    decimal Goal,
    decimal Accumulated,
    double Percent,
    decimal PacePerMonth,
    int MonthsDone,
    int MonthsLeft,
    int MonthsTotal,
    IReadOnlyList<RoadmapMonth> Squares,
    string Hint,
    decimal ThisMonthSaved = 0,
    decimal Remaining = 0);
