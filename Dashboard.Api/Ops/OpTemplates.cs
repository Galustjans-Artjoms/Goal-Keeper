using System.Globalization;
using System.Text.Json;
using Dashboard.Api.Auth;
using Dashboard.Api.Budget;
using Dashboard.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Api.Ops;

public sealed class OpTemplates
{
    private readonly BudgetService _budget;
    private readonly DashboardDbContext _db;
    private readonly AccountContext _account;

    public OpTemplates(BudgetService budget, DashboardDbContext db, AccountContext account)
    {
        _budget = budget;
        _db = db;
        _account = account;
    }

    public async Task<OpSolveResult> SolveAsync(OpSolveRequest request, CancellationToken cancellationToken)
    {
        var template = (request.Template ?? "").Trim().ToLowerInvariant();
        return template switch
        {
            "hit-limit" => await HitMonthLimitAsync(request.MustKeepFloorPercent ?? 0, cancellationToken),
            "cut-green" => await CutToGreenAsync(request.ExtraCutPercent ?? 0, cancellationToken),
            "pace-year" => await PaceVsYearAsync(request.YearLeftoverTarget ?? 0, cancellationToken),
            "burn-rate" => await BurnRateAsync(cancellationToken),
            "cap-stress" => await CapStressAsync(cancellationToken),
            "savings-path" => await SavingsPathAsync(cancellationToken),
            "what-if" => await WhatIfAsync(request.WhatIfCategory, request.WhatIfChangePercent ?? -20, cancellationToken),
            "rebalance" => await RebalanceAsync(request.MustKeepFloorPercent ?? 10, cancellationToken),
            "horizon" => await HorizonAsync(request.HorizonDays ?? 14, cancellationToken),
            _ => new OpSolveResult("unknown", "Unknown", "Pick a template.", 0, "", [], false)
        };
    }

    public async Task SaveScenarioAsync(string slot, OpSolveResult result, CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        slot = slot.Trim().ToUpperInvariant();
        if (slot is not ("A" or "B"))
        {
            slot = "A";
        }

        var existing = await _db.OpScenarios
            .FirstOrDefaultAsync(item => item.AccountId == accountId && item.Slot == slot, cancellationToken);
        var json = JsonSerializer.Serialize(result);
        if (existing is null)
        {
            _db.OpScenarios.Add(new OpScenario
            {
                AccountId = accountId,
                Slot = slot,
                Template = result.Template,
                Title = result.Title,
                ResultJson = json,
                SavedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.Template = result.Template;
            existing.Title = result.Title;
            existing.ResultJson = json;
            existing.SavedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OpScenario>> LoadScenariosAsync(CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        return await _db.OpScenarios
            .Where(item => item.AccountId == accountId)
            .OrderBy(item => item.Slot)
            .ToListAsync(cancellationToken);
    }

    public async Task ApplyCapsAsync(OpSolveResult result, CancellationToken cancellationToken)
    {
        foreach (var decision in result.Decisions.Where(d => d.Suggested > 0))
        {
            await _budget.UpsertCategoryCapAsync(decision.Category, decision.Suggested, cancellationToken);
        }
    }

    private async Task<List<CategorySpend>> LoadMonthCategoriesAsync(CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var rows = await _db.Transactions
            .Where(t => t.AccountId == accountId && t.Date >= monthStart && t.Date < monthEnd)
            .GroupBy(t => t.Category)
            .Select(g => new { Category = g.Key, Amount = g.Sum(t => t.Amount) })
            .OrderByDescending(c => c.Amount)
            .ToListAsync(cancellationToken);
        return rows.Select(r => new CategorySpend(r.Category, r.Amount)).ToList();
    }

    private async Task<OpSolveResult> HitMonthLimitAsync(decimal mustKeepFloorPercent, CancellationToken cancellationToken)
    {
        var forecast = await _budget.GetForecastAsync(cancellationToken);
        var status = await _budget.GetStatusAsync(null, cancellationToken);
        if (!forecast.HasLimit)
        {
            return Fail("hit-limit", "Hit month limit", "Set a monthly limit in Goals first.");
        }

        var remaining = Math.Max(0, forecast.MonthlyLimit - forecast.Spent);
        var dailyRoom = forecast.DaysLeft > 0
            ? decimal.Round(remaining / forecast.DaysLeft, 2)
            : remaining;
        var categories = await LoadMonthCategoriesAsync(cancellationToken);
        if (categories.Count == 0)
        {
            return new OpSolveResult(
                "hit-limit",
                "Hit month limit",
                $"€{Fmt(remaining)} left · €{Fmt(dailyRoom)} / day for {forecast.DaysLeft} days — no spend mix yet.",
                remaining,
                "allocatable EUR",
                [],
                true,
                Metrics(
                    ("Month leftover now", Money(remaining)),
                    ("Daily room", Money(dailyRoom)),
                    ("Days left", forecast.DaysLeft.ToString())));
        }

        var totalSpend = categories.Sum(c => c.Amount);
        var caps = status.CategoryCaps.ToDictionary(c => c.Category, c => c.Cap, StringComparer.OrdinalIgnoreCase);
        var floorShare = Math.Clamp(mustKeepFloorPercent, 0, 50) / 100m;
        var decisions = new List<OpDecision>();
        foreach (var cat in categories)
        {
            var share = totalSpend > 0 ? cat.Amount / totalSpend : 0;
            var raw = decimal.Round(remaining * share, 2);
            var floor = decimal.Round(remaining * floorShare / categories.Count, 2);
            var target = Math.Max(raw, floor);
            var cap = caps.GetValueOrDefault(cat.Category, 0);
            var headroom = cap > 0 ? Math.Max(0, cap - cat.Amount) : target;
            var suggested = cap > 0 ? Math.Min(target, headroom) : target;
            decisions.Add(new OpDecision(
                cat.Category,
                cat.Amount,
                suggested,
                cap,
                cap > 0 && suggested < target
                    ? $"Room under cap: €{Fmt(suggested)} (wanted €{Fmt(target)})."
                    : $"Allocate €{Fmt(suggested)} more this month ({Pct(share)} of mix).",
                suggested,
                cap > 0 ? (double)(cat.Amount / cap * 100m) : null));
        }

        RebalanceDrift(decisions, remaining);

        return new OpSolveResult(
            "hit-limit",
            "Hit month limit",
            $"Spread €{Fmt(remaining)} remaining (€{Fmt(dailyRoom)}/day × {forecast.DaysLeft}d) by spend mix"
            + (floorShare > 0 ? $", with {mustKeepFloorPercent:0}% floor split." : "."),
            remaining,
            "allocatable EUR",
            decisions,
            true,
            Metrics(
                ("Spent MTD", Money(forecast.Spent)),
                ("Limit", Money(forecast.MonthlyLimit)),
                ("Leftover", Money(remaining)),
                ("Daily room", Money(dailyRoom)),
                ("Projected month", Money(forecast.ProjectedMonth)),
                ("Categories", categories.Count.ToString())));
    }

    private async Task<OpSolveResult> CutToGreenAsync(decimal extraCutPercent, CancellationToken cancellationToken)
    {
        var forecast = await _budget.GetForecastAsync(cancellationToken);
        var status = await _budget.GetStatusAsync(null, cancellationToken);
        if (!forecast.HasLimit)
        {
            return Fail("cut-green", "Cut to green", "Set a monthly limit in Goals first.");
        }

        var categories = await LoadMonthCategoriesAsync(cancellationToken);
        var over = Math.Max(0, forecast.ProjectedMonth - forecast.MonthlyLimit);
        if (extraCutPercent > 0)
        {
            over = decimal.Round(over * (1 + extraCutPercent / 100m), 2);
        }

        if (over <= 0)
        {
            return new OpSolveResult(
                "cut-green",
                "Cut to green",
                "Already on track — projected month is within the limit.",
                0,
                "cut EUR",
                categories.Select(c => new OpDecision(c.Category, c.Amount, c.Amount, CapOf(status, c.Category), "Keep pace")).ToList(),
                true,
                Metrics(
                    ("Projected", Money(forecast.ProjectedMonth)),
                    ("Limit", Money(forecast.MonthlyLimit)),
                    ("Slack", Money(forecast.MonthlyLimit - forecast.ProjectedMonth))));
        }

        // Proportional cut (fairer than only largest), then greedy fix for rounding.
        var total = categories.Sum(c => c.Amount);
        var remainingCut = over;
        var decisions = new List<OpDecision>();
        foreach (var cat in categories.OrderByDescending(c => c.Amount))
        {
            var weight = total > 0 ? cat.Amount / total : 0;
            var cut = Math.Min(cat.Amount, decimal.Round(over * weight, 2));
            remainingCut -= cut;
            var suggested = cat.Amount - cut;
            decisions.Add(new OpDecision(
                cat.Category,
                cat.Amount,
                suggested,
                CapOf(status, cat.Category),
                cut > 0 ? $"Trim €{Fmt(cut)} ({Pct(weight)} of cut pack)." : "No cut.",
                -cut));
        }

        if (remainingCut > 0.01m)
        {
            foreach (var i in Enumerable.Range(0, decisions.Count).OrderByDescending(i => decisions[i].CurrentSpend))
            {
                var room = decisions[i].Suggested;
                var extra = Math.Min(room, remainingCut);
                if (extra <= 0)
                {
                    continue;
                }

                decisions[i] = decisions[i] with
                {
                    Suggested = decisions[i].Suggested - extra,
                    Delta = (decisions[i].Delta ?? 0) - extra,
                    Advice = $"Trim €{Fmt(-(decisions[i].Delta ?? 0))} total from this category."
                };
                remainingCut -= extra;
                if (remainingCut <= 0.01m)
                {
                    break;
                }
            }
        }

        var cutDone = over - Math.Max(0, remainingCut);
        return new OpSolveResult(
            "cut-green",
            "Cut to green",
            remainingCut > 0.01m
                ? $"Need €{Fmt(over)} cut; only €{Fmt(cutDone)} available in MTD categories."
                : $"Proportional cut of €{Fmt(over)} so projected month fits the limit"
                  + (extraCutPercent > 0 ? $" (includes +{extraCutPercent:0}% buffer)." : "."),
            cutDone,
            "cut EUR",
            decisions,
            remainingCut <= 0.01m,
            Metrics(
                ("Over projection", Money(over)),
                ("Cut applied", Money(cutDone)),
                ("Avg/day now", Money(forecast.AvgPerDay)),
                ("Safe avg/day", Money(forecast.DaysInMonth > 0 ? decimal.Round(forecast.MonthlyLimit / forecast.DaysInMonth, 2) : 0))));
    }

    private async Task<OpSolveResult> PaceVsYearAsync(decimal yearLeftoverTarget, CancellationToken cancellationToken)
    {
        var forecast = await _budget.GetForecastAsync(cancellationToken);
        var settings = await _budget.GetSettingsAsync(cancellationToken);
        if (!forecast.HasLimit)
        {
            return Fail("pace-year", "Pace vs year", "Set a monthly limit in Goals first.");
        }

        var categories = await LoadMonthCategoriesAsync(cancellationToken);
        var target = yearLeftoverTarget > 0
            ? yearLeftoverTarget
            : settings.SavingsTarget > 0 ? settings.SavingsTarget : 0;
        var yearBudget = forecast.MonthlyLimit * 12m;
        var maxYearSpend = Math.Max(0, yearBudget - target);
        var daily = decimal.Round(maxYearSpend / 365m, 2);
        var weekly = decimal.Round(daily * 7m, 2);
        var monthlyPace = decimal.Round(daily * forecast.DaysInMonth, 2);
        var gap = forecast.AvgPerDay - daily;

        var decisions = categories
            .Select(c =>
            {
                var share = forecast.Spent > 0 ? c.Amount / forecast.Spent : 0;
                var suggestedDaily = decimal.Round(daily * share, 2);
                return new OpDecision(
                    c.Category,
                    c.Amount,
                    suggestedDaily,
                    0,
                    $"≤ €{Fmt(suggestedDaily)}/day · €{Fmt(decimal.Round(suggestedDaily * 30, 2))}/mo at current mix.",
                    suggestedDaily);
            })
            .ToList();

        return new OpSolveResult(
            "pace-year",
            "Pace vs year",
            target > 0
                ? $"For €{Fmt(target)} year leftover: ≤ €{Fmt(daily)}/day (now avg €{Fmt(forecast.AvgPerDay)}/day, gap {(gap > 0 ? "+" : "")}{Fmt(gap)})."
                : $"Full year envelope pace: ≤ €{Fmt(daily)}/day.",
            daily,
            "max EUR / day",
            decisions,
            true,
            Metrics(
                ("Year budget", Money(yearBudget)),
                ("Max year spend", Money(maxYearSpend)),
                ("Daily ceiling", Money(daily)),
                ("Weekly ceiling", Money(weekly)),
                ("Month @ ceiling", Money(monthlyPace)),
                ("Current avg/day", Money(forecast.AvgPerDay)),
                ("Year spend @ pace", Money(forecast.ProjectedYearSpend))));
    }

    private async Task<OpSolveResult> BurnRateAsync(CancellationToken cancellationToken)
    {
        var forecast = await _budget.GetForecastAsync(cancellationToken);
        var categories = await LoadMonthCategoriesAsync(cancellationToken);
        if (!forecast.HasLimit)
        {
            return Fail("burn-rate", "Burn rate", "Set a monthly limit in Goals first.");
        }

        var remaining = forecast.MonthlyLimit - forecast.Spent;
        var daysToEmpty = forecast.AvgPerDay > 0
            ? (int)Math.Floor((double)(Math.Max(0, remaining) / forecast.AvgPerDay))
            : forecast.DaysLeft;
        var blowDate = DateOnly.FromDateTime(DateTime.Today).AddDays(Math.Max(0, daysToEmpty));
        var decisions = categories.Select(c =>
        {
            var share = forecast.Spent > 0 ? c.Amount / forecast.Spent : 0;
            var daily = decimal.Round(forecast.AvgPerDay * share, 2);
            return new OpDecision(
                c.Category,
                c.Amount,
                daily,
                0,
                $"Burn ≈ €{Fmt(daily)}/day ({Pct(share)} of mix).",
                daily,
                (double)(share * 100));
        }).ToList();

        return new OpSolveResult(
            "burn-rate",
            "Burn rate",
            remaining <= 0
                ? $"Limit already crossed by €{Fmt(-remaining)}. At €{Fmt(forecast.AvgPerDay)}/day you stay over."
                : $"At €{Fmt(forecast.AvgPerDay)}/day, cash runway ≈ {daysToEmpty} days (around {blowDate:dd MMM}) vs {forecast.DaysLeft} days left in month.",
            daysToEmpty,
            "days runway",
            decisions,
            remaining > 0,
            Metrics(
                ("Avg/day", Money(forecast.AvgPerDay)),
                ("Remaining budget", Money(remaining)),
                ("Days runway", daysToEmpty.ToString()),
                ("Days in month left", forecast.DaysLeft.ToString()),
                ("Projected over/under", Money(forecast.ProjectedLeftover ?? 0))));
    }

    private async Task<OpSolveResult> CapStressAsync(CancellationToken cancellationToken)
    {
        var status = await _budget.GetStatusAsync(null, cancellationToken);
        var forecast = await _budget.GetForecastAsync(cancellationToken);
        if (status.CategoryCaps.Count == 0)
        {
            return Fail("cap-stress", "Cap stress", "Add category caps in Goals first.");
        }

        var decisions = new List<OpDecision>();
        foreach (var cap in status.CategoryCaps.OrderByDescending(c => c.Percent))
        {
            var headroom = cap.Cap - cap.Spent;
            var projected = forecast.DaysLeft > 0 && forecast.Day > 0
                ? decimal.Round(cap.Spent / forecast.Day * forecast.DaysInMonth, 2)
                : cap.Spent;
            var advice = cap.Percent >= 100
                ? $"OVER by €{Fmt(-headroom)}. Freeze or cut."
                : projected > cap.Cap
                    ? $"On pace to hit €{Fmt(projected)} vs cap €{Fmt(cap.Cap)} — slow down."
                    : $"OK — €{Fmt(headroom)} headroom; pace €{Fmt(projected)}.";
            decisions.Add(new OpDecision(
                cap.Category,
                cap.Spent,
                Math.Max(0, headroom),
                cap.Cap,
                advice,
                headroom,
                cap.Percent));
        }

        var hottest = decisions.First();
        return new OpSolveResult(
            "cap-stress",
            "Cap stress",
            $"Hottest: {hottest.Category} at {hottest.UtilizationPercent:0}%."
            + $" {decisions.Count(d => (d.UtilizationPercent ?? 0) >= 100)} over, "
            + $"{decisions.Count(d => (d.UtilizationPercent ?? 0) >= 80 && (d.UtilizationPercent ?? 0) < 100)} warn.",
            (decimal)(hottest.UtilizationPercent ?? 0),
            "hottest %",
            decisions,
            decisions.All(d => (d.UtilizationPercent ?? 0) < 100),
            Metrics(
                ("Caps", status.CategoryCaps.Count.ToString()),
                ("Over", decisions.Count(d => (d.UtilizationPercent ?? 0) >= 100).ToString()),
                ("Warn ≥80%", decisions.Count(d => (d.UtilizationPercent ?? 0) >= 80).ToString()),
                ("Month spent", Money(forecast.Spent))));
    }

    private async Task<OpSolveResult> SavingsPathAsync(CancellationToken cancellationToken)
    {
        var forecast = await _budget.GetForecastAsync(cancellationToken);
        var settings = await _budget.GetSettingsAsync(cancellationToken);
        if (settings.SavingsTarget <= 0)
        {
            return Fail("savings-path", "Savings path", "Set a savings target in Goals first.");
        }

        if (!forecast.HasLimit)
        {
            return Fail("savings-path", "Savings path", "Set a monthly limit so leftover can fund the goal.");
        }

        var monthlyLeftoverPace = forecast.MonthlyLimit - forecast.ProjectedMonth;
        var months = monthlyLeftoverPace > 0
            ? (int)Math.Ceiling(settings.SavingsTarget / monthlyLeftoverPace)
            : -1;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var eta = months > 0 ? today.AddMonths(months) : (DateOnly?)null;
        var deadline = settings.SavingsDeadline;
        var onTrack = deadline is null || (eta is not null && eta <= deadline);
        var needMonthly = deadline is { } d && d > today
            ? decimal.Round(settings.SavingsTarget / Math.Max(1, ((d.Year - today.Year) * 12 + d.Month - today.Month)), 2)
            : settings.SavingsTarget;

        var categories = await LoadMonthCategoriesAsync(cancellationToken);
        var cutNeeded = Math.Max(0, needMonthly - Math.Max(0, monthlyLeftoverPace));
        var decisions = categories.Select(c =>
        {
            var share = forecast.Spent > 0 ? c.Amount / forecast.Spent : 0;
            var trim = decimal.Round(cutNeeded * share, 2);
            return new OpDecision(
                c.Category,
                c.Amount,
                Math.Max(0, c.Amount - trim),
                0,
                cutNeeded > 0
                    ? $"Free €{Fmt(trim)}/mo toward goal ({Pct(share)})."
                    : "Pace already funds the goal — keep mix.",
                -trim);
        }).ToList();

        return new OpSolveResult(
            "savings-path",
            "Savings path",
            months < 0
                ? $"Projected leftover is ≤ 0 — cannot fund €{Fmt(settings.SavingsTarget)} at current pace. Need ≈ €{Fmt(needMonthly)}/mo freed."
                : $"At €{Fmt(Math.Max(0, monthlyLeftoverPace))}/mo leftover → ~{months} months"
                  + (eta is { } e ? $" (≈ {e:MMM yyyy})" : "")
                  + (deadline is { } dl ? $"; deadline {dl:MMM yyyy} → {(onTrack ? "on track" : "LATE")}." : "."),
            months < 0 ? needMonthly : months,
            months < 0 ? "EUR / mo needed" : "months",
            decisions,
            months > 0 && onTrack,
            Metrics(
                ("Goal", Money(settings.SavingsTarget)),
                ("Deadline", deadline?.ToString("yyyy-MM-dd") ?? "—"),
                ("Leftover pace / mo", Money(Math.Max(0, monthlyLeftoverPace))),
                ("Needed / mo for deadline", Money(needMonthly)),
                ("Extra cut / mo", Money(cutNeeded)),
                ("On track", onTrack ? "yes" : "no")));
    }

    private async Task<OpSolveResult> WhatIfAsync(string? category, decimal changePercent, CancellationToken cancellationToken)
    {
        var forecast = await _budget.GetForecastAsync(cancellationToken);
        var categories = await LoadMonthCategoriesAsync(cancellationToken);
        if (categories.Count == 0)
        {
            return Fail("what-if", "What-if", "Add some spend first.");
        }

        changePercent = Math.Clamp(changePercent, -90, 200);
        var factor = 1 + changePercent / 100m;
        var targetName = string.IsNullOrWhiteSpace(category)
            ? categories[0].Category
            : category.Trim();
        var decisions = new List<OpDecision>();
        decimal deltaTotal = 0;
        foreach (var cat in categories)
        {
            var hit = cat.Category.Equals(targetName, StringComparison.OrdinalIgnoreCase);
            var suggested = hit ? decimal.Round(cat.Amount * factor, 2) : cat.Amount;
            var delta = suggested - cat.Amount;
            deltaTotal += delta;
            decisions.Add(new OpDecision(
                cat.Category,
                cat.Amount,
                suggested,
                0,
                hit
                    ? $"{(changePercent >= 0 ? "+" : "")}{changePercent:0}% → Δ €{Fmt(delta)} MTD-equivalent."
                    : "Unchanged in this scenario.",
                delta));
        }

        if (!decisions.Any(d => d.Category.Equals(targetName, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail("what-if", "What-if", $"Category '{targetName}' not found in this month.");
        }

        var newSpent = forecast.Spent + deltaTotal;
        var newAvg = forecast.Day > 0 ? decimal.Round(newSpent / forecast.Day, 2) : 0;
        var newProjected = decimal.Round(newAvg * forecast.DaysInMonth, 2);
        var newLeftover = forecast.HasLimit ? forecast.MonthlyLimit - newProjected : (decimal?)null;

        return new OpSolveResult(
            "what-if",
            "What-if shock",
            $"If {targetName} changes by {changePercent:0}%: projected month €{Fmt(newProjected)}"
            + (newLeftover is decimal left
                ? $" · leftover €{Fmt(left)} (was €{Fmt(forecast.ProjectedLeftover ?? 0)})."
                : "."),
            newProjected,
            "projected EUR",
            decisions,
            newLeftover is null or >= 0,
            Metrics(
                ("Shock category", targetName),
                ("Shock %", changePercent.ToString("0", CultureInfo.InvariantCulture)),
                ("Δ MTD", Money(deltaTotal)),
                ("New projected month", Money(newProjected)),
                ("New leftover", newLeftover is decimal l ? Money(l) : "—"),
                ("Old projected", Money(forecast.ProjectedMonth))));
    }

    private async Task<OpSolveResult> RebalanceAsync(decimal mustKeepFloorPercent, CancellationToken cancellationToken)
    {
        var forecast = await _budget.GetForecastAsync(cancellationToken);
        var status = await _budget.GetStatusAsync(null, cancellationToken);
        var categories = await LoadMonthCategoriesAsync(cancellationToken);
        if (!forecast.HasLimit)
        {
            return Fail("rebalance", "Rebalance", "Set a monthly limit in Goals first.");
        }

        if (categories.Count == 0)
        {
            return Fail("rebalance", "Rebalance", "No category spend to rebalance.");
        }

        var envelope = forecast.MonthlyLimit;
        var n = categories.Count;
        var equal = decimal.Round(envelope / n, 2);
        var floor = decimal.Round(envelope * Math.Clamp(mustKeepFloorPercent, 0, 40) / 100m / n, 2);
        var decisions = new List<OpDecision>();
        foreach (var cat in categories)
        {
            var cap = CapOf(status, cat.Category);
            var ideal = Math.Max(equal, floor);
            var suggested = cap > 0 ? Math.Min(ideal, cap) : ideal;
            var delta = suggested - cat.Amount;
            decisions.Add(new OpDecision(
                cat.Category,
                cat.Amount,
                suggested,
                cap,
                delta >= 0
                    ? $"Raise allowance toward €{Fmt(suggested)} (Δ +€{Fmt(delta)})."
                    : $"Pull back to €{Fmt(suggested)} (Δ €{Fmt(delta)}).",
                delta,
                envelope > 0 ? (double)(cat.Amount / envelope * 100m) : 0));
        }

        return new OpSolveResult(
            "rebalance",
            "Rebalance mix",
            $"Equal-ish month envelope €{Fmt(equal)}/category across {n} groups"
            + (floor > 0 ? $" (floor €{Fmt(floor)})." : ".")
            + " Suggested values work as caps via Apply.",
            equal,
            "EUR / category",
            decisions,
            true,
            Metrics(
                ("Envelope", Money(envelope)),
                ("Equal share", Money(equal)),
                ("Floor / cat", Money(floor)),
                ("Current HHI-ish", Pct(categories.Sum(c =>
                {
                    var s = forecast.Spent > 0 ? c.Amount / forecast.Spent : 0;
                    return s * s;
                })))));
    }

    private async Task<OpSolveResult> HorizonAsync(int horizonDays, CancellationToken cancellationToken)
    {
        var forecast = await _budget.GetForecastAsync(cancellationToken);
        var categories = await LoadMonthCategoriesAsync(cancellationToken);
        if (!forecast.HasLimit)
        {
            return Fail("horizon", "Horizon plan", "Set a monthly limit in Goals first.");
        }

        horizonDays = Math.Clamp(horizonDays, 1, Math.Max(1, forecast.DaysLeft));
        var remaining = Math.Max(0, forecast.MonthlyLimit - forecast.Spent);
        var budgetHorizon = decimal.Round(remaining * horizonDays / Math.Max(1, forecast.DaysLeft), 2);
        var daily = decimal.Round(budgetHorizon / horizonDays, 2);
        var total = categories.Sum(c => c.Amount);
        var decisions = categories.Select(c =>
        {
            var share = total > 0 ? c.Amount / total : 0;
            var slice = decimal.Round(budgetHorizon * share, 2);
            return new OpDecision(
                c.Category,
                c.Amount,
                slice,
                0,
                $"Next {horizonDays}d budget slice ≈ €{Fmt(slice)} ({Pct(share)}).",
                slice);
        }).ToList();

        return new OpSolveResult(
            "horizon",
            "Horizon plan",
            $"Next {horizonDays} days: spend ≤ €{Fmt(budgetHorizon)} total (≤ €{Fmt(daily)}/day) to stay on month limit.",
            budgetHorizon,
            $"EUR / {horizonDays}d",
            decisions,
            true,
            Metrics(
                ("Horizon days", horizonDays.ToString()),
                ("Horizon budget", Money(budgetHorizon)),
                ("Daily cap", Money(daily)),
                ("Month days left", forecast.DaysLeft.ToString()),
                ("Month leftover", Money(remaining))));
    }

    private static OpSolveResult Fail(string id, string title, string message) =>
        new(id, title, message, 0, "", [], false);

    private static IReadOnlyList<OpMetric> Metrics(params (string Label, string Value)[] items) =>
        items.Select(i => new OpMetric(i.Label, i.Value)).ToList();

    private static decimal CapOf(BudgetStatus status, string category) =>
        status.CategoryCaps.FirstOrDefault(c => c.Category.Equals(category, StringComparison.OrdinalIgnoreCase))?.Cap ?? 0;

    private static void RebalanceDrift(List<OpDecision> decisions, decimal targetSum)
    {
        var drift = targetSum - decisions.Sum(d => d.Suggested);
        if (drift == 0 || decisions.Count == 0)
        {
            return;
        }

        var top = decisions.OrderByDescending(d => d.Suggested).First();
        var idx = decisions.IndexOf(top);
        decisions[idx] = top with { Suggested = Math.Max(0, top.Suggested + drift) };
    }

    private static string Fmt(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Money(decimal value) => "€" + Fmt(value);
    private static string Pct(decimal share) => (share * 100m).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
