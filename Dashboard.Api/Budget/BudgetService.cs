using System.Globalization;
using Dashboard.Api.Auth;
using Dashboard.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Api.Budget;

public class BudgetService
{
    public static readonly int[] MonthlyThresholds = [20, 50, 80, 90, 100];
    public static readonly int[] WeeklyThresholds = [80, 100];

    private readonly DashboardDbContext _db;
    private readonly IBudgetNotifier _notifier;
    private readonly AccountContext _account;

    public BudgetService(DashboardDbContext db, IBudgetNotifier notifier, AccountContext account)
    {
        _db = db;
        _notifier = notifier;
        _account = account;
    }

    public async Task<BudgetSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var settings = await _db.BudgetSettings
            .FirstOrDefaultAsync(item => item.AccountId == accountId, cancellationToken);
        if (settings is not null)
        {
            if (settings.AlertLevel is > 0 and < 20)
            {
                settings.AlertLevel = settings.AlertLevel >= 2 ? 100 : 80;
                await _db.SaveChangesAsync(cancellationToken);
            }

            return settings;
        }

        settings = new BudgetSettings { AccountId = accountId, MonthlyLimit = 0 };
        _db.BudgetSettings.Add(settings);
        await _db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task SetMonthlyLimitAsync(decimal limit, CancellationToken cancellationToken)
    {
        // Legacy: treat as full salary with keep unchanged, then sync spend.
        var settings = await GetSettingsAsync(cancellationToken);
        settings.MonthlyIncome = Math.Max(0, decimal.Round(limit, 2));
        settings.SyncSpendFromPaycheck();
        settings.AlertLevel = 0;
        settings.AlertYearMonth = 0;
        settings.WeeklyAlertLevel = 0;
        settings.WeeklyAlertYearWeek = 0;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetPaycheckAsync(
        decimal income,
        decimal keep,
        CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.MonthlyIncome = Math.Max(0, decimal.Round(income, 2));
        settings.SavingsTarget = Math.Max(0, decimal.Round(keep, 2));
        settings.SyncSpendFromPaycheck();
        settings.AlertLevel = 0;
        settings.AlertYearMonth = 0;
        settings.WeeklyAlertLevel = 0;
        settings.WeeklyAlertYearWeek = 0;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetYearGoalAsync(
        string? name,
        decimal amount,
        DateOnly? byDate,
        CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.YearGoalName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (settings.YearGoalName is { Length: > 120 })
        {
            settings.YearGoalName = settings.YearGoalName[..120];
        }

        settings.YearGoal = Math.Max(0, decimal.Round(amount, 2));
        settings.SavingsDeadline = byDate;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetSavingsGoalAsync(decimal target, DateOnly? deadline, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        settings.SavingsTarget = Math.Max(0, decimal.Round(target, 2));
        settings.SavingsDeadline = deadline;
        if (settings.MonthlyIncome <= 0 && settings.MonthlyLimit > 0)
        {
            // Old rows: MonthlyLimit was spend envelope — reconstruct income.
            settings.MonthlyIncome = settings.MonthlyLimit + settings.SavingsTarget;
        }

        settings.SyncSpendFromPaycheck();
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertCategoryCapAsync(string category, decimal monthlyCap, CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        category = category.Trim();
        if (category.Length == 0 || monthlyCap <= 0)
        {
            return;
        }

        var existing = await _db.BudgetCategoryCaps
            .FirstOrDefaultAsync(
                cap => cap.AccountId == accountId && cap.Category.ToLower() == category.ToLower(),
                cancellationToken);
        if (existing is null)
        {
            _db.BudgetCategoryCaps.Add(new BudgetCategoryCap
            {
                AccountId = accountId,
                Category = category,
                MonthlyCap = decimal.Round(monthlyCap, 2)
            });
        }
        else
        {
            existing.Category = category;
            existing.MonthlyCap = decimal.Round(monthlyCap, 2);
            existing.AlertYearMonth = 0;
            existing.OverAlertSent = false;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteCategoryCapAsync(long id, CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var cap = await _db.BudgetCategoryCaps
            .FirstOrDefaultAsync(item => item.Id == id && item.AccountId == accountId, cancellationToken);
        if (cap is null)
        {
            return;
        }

        _db.BudgetCategoryCaps.Remove(cap);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<BudgetStatus> GetStatusAsync(DateOnly? forDate, CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var settings = await GetSettingsAsync(cancellationToken);
        var day = forDate ?? DateOnly.FromDateTime(DateTime.Today);
        var monthStart = new DateOnly(day.Year, day.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var (weekStart, weekEnd) = WeekRange(day);

        var monthSpent = await _db.Transactions
            .Where(t => t.AccountId == accountId && t.Date >= monthStart && t.Date < monthEnd)
            .SumAsync(t => (decimal?)t.Amount, cancellationToken) ?? 0m;
        var weekSpent = await _db.Transactions
            .Where(t => t.AccountId == accountId && t.Date >= weekStart && t.Date < weekEnd)
            .SumAsync(t => (decimal?)t.Amount, cancellationToken) ?? 0m;

        var spendBudget = ResolveSpendBudget(settings);
        var weeklyLimit = WeeklyShare(spendBudget, day);
        var caps = await _db.BudgetCategoryCaps
            .Where(cap => cap.AccountId == accountId)
            .OrderBy(cap => cap.Category)
            .ToListAsync(cancellationToken);
        var categoryStatuses = new List<CategoryCapStatus>();
        foreach (var cap in caps)
        {
            var key = cap.Category.ToLower();
            var spent = await _db.Transactions
                .Where(t => t.AccountId == accountId
                            && t.Date >= monthStart
                            && t.Date < monthEnd
                            && t.Category.ToLower() == key)
                .SumAsync(t => (decimal?)t.Amount, cancellationToken) ?? 0m;
            categoryStatuses.Add(BuildCategoryStatus(cap, spent));
        }

        return BuildStatus(
            spendBudget,
            monthSpent,
            weeklyLimit,
            weekSpent,
            categoryStatuses,
            settings.MonthlyIncome > 0 ? settings.MonthlyIncome : spendBudget + settings.SavingsTarget,
            settings.SavingsTarget);
    }

    public async Task<MonthForecast> GetForecastAsync(CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var settings = await GetSettingsAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var day = today.Day;
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var daysLeft = Math.Max(0, daysInMonth - day);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);

        var spent = await _db.Transactions
            .Where(t => t.AccountId == accountId && t.Date >= monthStart && t.Date < monthEnd)
            .SumAsync(t => (decimal?)t.Amount, cancellationToken) ?? 0m;

        var avg = day > 0 ? decimal.Round(spent / day, 2) : 0m;
        var projectedMonth = decimal.Round(avg * daysInMonth, 2);
        var projectedYear = decimal.Round(avg * 365m, 2);
        var spendBudget = ResolveSpendBudget(settings);
        var income = settings.MonthlyIncome > 0 ? settings.MonthlyIncome : spendBudget + settings.SavingsTarget;
        var hasLimit = spendBudget > 0;
        decimal? leftover = hasLimit ? spendBudget - projectedMonth : null;
        decimal? yearLeftover = hasLimit ? spendBudget * 12m - projectedYear : null;
        var band = leftover is null
            ? BudgetBand.Ok
            : leftover < 0
                ? BudgetBand.Over
                : leftover < spendBudget * 0.2m
                    ? BudgetBand.Warn
                    : BudgetBand.Ok;

        var categoryRows = await _db.Transactions
            .Where(t => t.AccountId == accountId && t.Date >= monthStart && t.Date < monthEnd)
            .GroupBy(t => t.Category)
            .Select(g => new { Category = g.Key, Amount = g.Sum(t => t.Amount) })
            .OrderByDescending(c => c.Amount)
            .Take(8)
            .ToListAsync(cancellationToken);
        var categories = categoryRows
            .Select(c => new CategorySpend(c.Category, c.Amount))
            .ToList();

        // Keep-from-salary: show how much of the keep is still "safe" this month (income − spent).
        decimal? toGoal = settings.SavingsTarget > 0 ? settings.SavingsTarget : null;
        decimal? dailyToGoal = null;
        if (settings.SavingsTarget > 0 && daysLeft > 0)
        {
            var keepLeft = Math.Max(0, income - spent - spendBudget); // usually = keep if on track
            _ = keepLeft;
            dailyToGoal = decimal.Round(settings.SavingsTarget / Math.Max(1, daysInMonth), 2);
        }

        return new MonthForecast(
            day,
            daysInMonth,
            daysLeft,
            spent,
            avg,
            projectedMonth,
            spendBudget,
            hasLimit,
            leftover,
            projectedYear,
            yearLeftover,
            band,
            categories,
            settings.SavingsTarget,
            settings.SavingsDeadline,
            toGoal,
            dailyToGoal,
            income);
    }

    /// <summary>
    /// Motivational month-grid toward keep/savings goal: past leftovers + forecast squares.
    /// </summary>
    public async Task<SavingsRoadmap> GetRoadmapAsync(CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var settings = await GetSettingsAsync(cancellationToken);
        var income = settings.MonthlyIncome > 0
            ? settings.MonthlyIncome
            : settings.MonthlyLimit + settings.SavingsTarget;
        var keep = settings.SavingsTarget;
        var today = DateOnly.FromDateTime(DateTime.Today);

        if (income <= 0)
        {
            return new SavingsRoadmap(
                0, 0, 0, 0, 0, 0, 0, [],
                "Set Full salary in Goals first.");
        }

        // Bracket = explicit Year goal (Goals). Fallback keep×12 only if year goal not set yet.
        var goal = settings.YearGoal > 0
            ? settings.YearGoal
            : keep > 0 ? decimal.Round(keep * 12m, 2) : 0m;
        if (goal <= 0)
        {
            return new SavingsRoadmap(
                0, 0, 0, 0, 0, 0, 0, [],
                "Set Year goal in Goals — Roadmap uses that as the big bracket.");
        }

        var oldest = await _db.Transactions
            .Where(t => t.AccountId == accountId)
            .OrderBy(t => t.Date)
            .Select(t => (DateOnly?)t.Date)
            .FirstOrDefaultAsync(cancellationToken);

        var start = oldest ?? new DateOnly(today.Year, today.Month, 1);
        start = new DateOnly(start.Year, start.Month, 1);
        var minStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-17);
        if (start < minStart)
        {
            start = minStart;
        }

        var history = new List<RoadmapMonth>();
        var thisMonthStart = new DateOnly(today.Year, today.Month, 1);
        for (var cursor = start;
             cursor <= thisMonthStart;
             cursor = cursor.AddMonths(1))
        {
            var monthEnd = cursor.AddMonths(1);
            var spent = await _db.Transactions
                .Where(t => t.AccountId == accountId && t.Date >= cursor && t.Date < monthEnd)
                .SumAsync(t => (decimal?)t.Amount, cancellationToken) ?? 0m;
            // Full leftover from salary counts toward year goal (not capped by keep).
            var leftover = Math.Max(0m, decimal.Round(income - spent, 2));
            var isCurrent = cursor == thisMonthStart;
            history.Add(new RoadmapMonth(
                cursor.ToString("MMM yy", CultureInfo.InvariantCulture),
                cursor.Year,
                cursor.Month,
                leftover,
                IsForecast: false,
                CountsTowardGoal: leftover > 0,
                IsCurrent: isCurrent));
        }

        var thisMonthSaved = history.LastOrDefault(m => m.IsCurrent)?.Saved ?? 0m;
        var accumulated = decimal.Round(history.Sum(m => m.Saved), 2);
        var monthsDone = history.Count(m => m.CountsTowardGoal);
        var remaining = Math.Max(0m, goal - accumulated);

        // Historical leftover pace (for no-deadline fallback).
        var positive = history.Where(m => m.Saved > 0).Select(m => m.Saved).ToList();
        var historicalPace = positive.Count > 0
            ? decimal.Round(positive.Average(), 2)
            : keep > 0 ? keep : decimal.Round(goal / 12m, 2);
        if (historicalPace <= 0)
        {
            historicalPace = decimal.Round(goal / 12m, 2);
        }

        int monthsLeft;
        decimal pace;
        string scheduleNote;
        if (settings.SavingsDeadline is DateOnly deadline && deadline >= thisMonthStart)
        {
            // Finish date rules: one square per month from now through finish month.
            var totalOnTimeline = RoadmapMath.MonthsInclusive(thisMonthStart, deadline);
            monthsLeft = RoadmapMath.ForecastSquaresUntil(today, deadline);
            pace = RoadmapMath.RequiredPace(remaining, monthsLeft);
            scheduleNote = $"by {deadline:yyyy-MM} · {totalOnTimeline} squares on the calendar";
        }
        else
        {
            // No finish date: squares = ceil(remaining / historical leftover pace).
            monthsLeft = remaining <= 0 || historicalPace <= 0
                ? 0
                : (int)Math.Ceiling(remaining / historicalPace);
            pace = historicalPace;
            scheduleNote = $"no finish date · {monthsLeft} mo at leftover pace €{pace:0.##}";
        }

        if (remaining <= 0)
        {
            monthsLeft = 0;
        }

        var monthsTotal = settings.SavingsDeadline is DateOnly d && d >= thisMonthStart
            ? RoadmapMath.MonthsInclusive(thisMonthStart, d)
            : monthsDone + monthsLeft;

        const int maxForecastSquares = 60;
        var squaresToDraw = Math.Min(monthsLeft, maxForecastSquares);
        var squares = new List<RoadmapMonth>(history);
        for (var i = 1; i <= squaresToDraw; i++)
        {
            var future = thisMonthStart.AddMonths(i);
            squares.Add(new RoadmapMonth(
                future.ToString("MMM yy", CultureInfo.InvariantCulture),
                future.Year,
                future.Month,
                pace,
                IsForecast: true,
                CountsTowardGoal: false,
                IsCurrent: false));
        }

        var percent = goal > 0
            ? Math.Min(100, (double)(accumulated / goal * 100m))
            : 0;

        var extra = monthsLeft > maxForecastSquares
            ? $" (+{monthsLeft - maxForecastSquares} mo not drawn)"
            : "";
        var hint = remaining <= 0
            ? $"Done — €{accumulated:0.##} / €{goal:0.##}. This month contributed €{thisMonthSaved:0.##}."
            : $"This month +€{thisMonthSaved:0.##} → €{accumulated:0.##} / €{goal:0.##} · still need €{remaining:0.##} · {scheduleNote} · need €{pace:0.##}/mo on dashed squares{extra}.";

        return new SavingsRoadmap(
            goal,
            accumulated,
            percent,
            pace,
            monthsDone,
            monthsLeft,
            Math.Max(1, monthsTotal),
            squares,
            hint,
            thisMonthSaved,
            remaining);
    }

    public async Task CheckAndNotifyAsync(DateOnly transactionDate, string? category, CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var settings = await GetSettingsAsync(cancellationToken);
        var spendBudget = ResolveSpendBudget(settings);
        if (spendBudget <= 0
            && !await _db.BudgetCategoryCaps.AnyAsync(cap => cap.AccountId == accountId, cancellationToken))
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        if (spendBudget > 0
            && transactionDate.Year == today.Year
            && transactionDate.Month == today.Month)
        {
            await NotifyMonthlyAsync(settings, transactionDate, cancellationToken);
            await NotifyWeeklyAsync(settings, transactionDate, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            await NotifyCategoryAsync(category, transactionDate, cancellationToken);
        }
    }

    public async Task ResetAccountBudgetAsync(CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var settings = await _db.BudgetSettings
            .Where(item => item.AccountId == accountId)
            .ToListAsync(cancellationToken);
        var caps = await _db.BudgetCategoryCaps
            .Where(item => item.AccountId == accountId)
            .ToListAsync(cancellationToken);
        _db.BudgetSettings.RemoveRange(settings);
        _db.BudgetCategoryCaps.RemoveRange(caps);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task NotifyMonthlyAsync(BudgetSettings settings, DateOnly day, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(day, cancellationToken);
        if (!status.Enabled || status.HighestThreshold == 0)
        {
            return;
        }

        var yearMonth = day.Year * 100 + day.Month;
        if (settings.AlertYearMonth != yearMonth)
        {
            settings.AlertYearMonth = yearMonth;
            settings.AlertLevel = 0;
        }

        var pending = MonthlyThresholds
            .Where(threshold => threshold > settings.AlertLevel && status.Percent + 0.0001 >= threshold)
            .ToList();
        foreach (var threshold in pending)
        {
            var message = threshold >= 100
                ? $"Month blown ({threshold}%): {Fmt(status.Spent)} / {Fmt(status.MonthlyLimit)} EUR."
                : $"Month {threshold}%: {Fmt(status.Spent)} / {Fmt(status.MonthlyLimit)} EUR ({status.Percent:0}%).";
            await _notifier.NotifyAsync(message, cancellationToken);
            settings.AlertLevel = threshold;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task NotifyWeeklyAsync(BudgetSettings settings, DateOnly day, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(day, cancellationToken);
        if (status.WeeklyLimit <= 0)
        {
            return;
        }

        var yearWeek = YearWeek(day);
        if (settings.WeeklyAlertYearWeek != yearWeek)
        {
            settings.WeeklyAlertYearWeek = yearWeek;
            settings.WeeklyAlertLevel = 0;
        }

        var pending = WeeklyThresholds
            .Where(threshold => threshold > settings.WeeklyAlertLevel && status.WeeklyPercent + 0.0001 >= threshold)
            .ToList();
        foreach (var threshold in pending)
        {
            var message = threshold >= 100
                ? $"Week blown ({threshold}%): {Fmt(status.WeeklySpent)} / {Fmt(status.WeeklyLimit)} EUR this week."
                : $"Week {threshold}%: {Fmt(status.WeeklySpent)} / {Fmt(status.WeeklyLimit)} EUR ({status.WeeklyPercent:0}%).";
            await _notifier.NotifyAsync(message, cancellationToken);
            settings.WeeklyAlertLevel = threshold;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task NotifyCategoryAsync(string category, DateOnly day, CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var cap = await _db.BudgetCategoryCaps
            .FirstOrDefaultAsync(
                item => item.AccountId == accountId && item.Category.ToLower() == category.Trim().ToLower(),
                cancellationToken);
        if (cap is null || cap.MonthlyCap <= 0)
        {
            return;
        }

        var monthStart = new DateOnly(day.Year, day.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var key = cap.Category.ToLower();
        var spent = await _db.Transactions
            .Where(t => t.AccountId == accountId
                        && t.Date >= monthStart
                        && t.Date < monthEnd
                        && t.Category.ToLower() == key)
            .SumAsync(t => (decimal?)t.Amount, cancellationToken) ?? 0m;
        if (spent < cap.MonthlyCap)
        {
            return;
        }

        var yearMonth = day.Year * 100 + day.Month;
        if (cap.AlertYearMonth != yearMonth)
        {
            cap.AlertYearMonth = yearMonth;
            cap.OverAlertSent = false;
        }

        if (cap.OverAlertSent)
        {
            return;
        }

        await _notifier.NotifyAsync(
            $"Category cap blown · {cap.Category}: {Fmt(spent)} / {Fmt(cap.MonthlyCap)} EUR this month.",
            cancellationToken);
        cap.OverAlertSent = true;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static decimal ResolveSpendBudget(BudgetSettings settings)
    {
        settings.SyncSpendFromPaycheck();
        return settings.SpendBudget;
    }

    private static BudgetStatus BuildStatus(
        decimal monthlyLimit,
        decimal monthSpent,
        decimal weeklyLimit,
        decimal weekSpent,
        IReadOnlyList<CategoryCapStatus> categoryCaps,
        decimal monthlyIncome = 0,
        decimal keepFromSalary = 0)
    {
        if (monthlyLimit <= 0)
        {
            return new BudgetStatus(
                0, monthSpent, 0, 0, 0, BudgetBand.Ok, false,
                weeklyLimit, weekSpent, weeklyLimit > 0 ? (double)(weekSpent / weeklyLimit * 100m) : 0,
                BandFor(weeklyLimit > 0 ? (double)(weekSpent / weeklyLimit * 100m) : 0),
                categoryCaps,
                monthlyIncome,
                keepFromSalary);
        }

        var percent = (double)(monthSpent / monthlyLimit * 100m);
        var weeklyPercent = weeklyLimit > 0 ? (double)(weekSpent / weeklyLimit * 100m) : 0;
        var highest = MonthlyThresholds.LastOrDefault(threshold => percent + 0.0001 >= threshold);
        return new BudgetStatus(
            monthlyLimit,
            monthSpent,
            monthlyLimit - monthSpent,
            percent,
            highest,
            BandFor(percent),
            true,
            weeklyLimit,
            weekSpent,
            weeklyPercent,
            BandFor(weeklyPercent),
            categoryCaps,
            monthlyIncome,
            keepFromSalary);
    }

    private static CategoryCapStatus BuildCategoryStatus(BudgetCategoryCap cap, decimal spent)
    {
        var percent = cap.MonthlyCap > 0 ? (double)(spent / cap.MonthlyCap * 100m) : 0;
        return new CategoryCapStatus(cap.Id, cap.Category, cap.MonthlyCap, spent, percent, BandFor(percent));
    }

    private static BudgetBand BandFor(double percent) =>
        percent >= 100 ? BudgetBand.Over : percent >= 80 ? BudgetBand.Warn : BudgetBand.Ok;

    private static decimal WeeklyShare(decimal monthlyLimit, DateOnly day)
    {
        if (monthlyLimit <= 0)
        {
            return 0;
        }

        var days = DateTime.DaysInMonth(day.Year, day.Month);
        return decimal.Round(monthlyLimit * 7m / days, 2);
    }

    private static (DateOnly Start, DateOnly End) WeekRange(DateOnly day)
    {
        var offset = ((int)day.DayOfWeek + 6) % 7;
        var start = day.AddDays(-offset);
        return (start, start.AddDays(7));
    }

    private static int YearWeek(DateOnly day)
    {
        var calendar = CultureInfo.InvariantCulture.Calendar;
        var week = calendar.GetWeekOfYear(
            day.ToDateTime(TimeOnly.MinValue),
            CalendarWeekRule.FirstFourDayWeek,
            DayOfWeek.Monday);
        return day.Year * 100 + week;
    }

    private static string Fmt(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}

public interface IBudgetNotifier
{
    Task NotifyAsync(string message, CancellationToken cancellationToken);
}
