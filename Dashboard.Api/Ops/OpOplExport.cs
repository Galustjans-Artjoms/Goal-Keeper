using System.Globalization;
using System.Text;
using Dashboard.Api.Auth;
using Dashboard.Api.Budget;
using Dashboard.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Api.Ops;

/// <summary>
/// Builds OPL-style .mod / .dat snapshots from live Goal Keeper data
/// (same modeling shape as CPLEX homework — for study / portfolio, not executed here).
/// </summary>
public sealed class OpOplExport
{
    private readonly BudgetService _budget;
    private readonly DashboardDbContext _db;
    private readonly AccountContext _account;

    public OpOplExport(BudgetService budget, DashboardDbContext db, AccountContext account)
    {
        _budget = budget;
        _db = db;
        _account = account;
    }

    public async Task<(string Mod, string Dat, string Title)> BuildAsync(CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var forecast = await _budget.GetForecastAsync(cancellationToken);
        var status = await _budget.GetStatusAsync(null, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var categories = await _db.Transactions
            .Where(t => t.AccountId == accountId && t.Date >= monthStart && t.Date < monthEnd)
            .GroupBy(t => t.Category)
            .Select(g => new { Category = g.Key, Amount = g.Sum(x => x.Amount) })
            .OrderBy(c => c.Category)
            .ToListAsync(cancellationToken);

        if (categories.Count == 0)
        {
            categories =
            [
                new { Category = "Food", Amount = 0m },
                new { Category = "Travel", Amount = 0m },
                new { Category = "General", Amount = 0m }
            ];
        }

        var remaining = forecast.HasLimit
            ? Math.Max(0, forecast.MonthlyLimit - forecast.Spent)
            : 500m;
        var caps = status.CategoryCaps.ToDictionary(c => c.Category, c => c.Cap, StringComparer.OrdinalIgnoreCase);

        var mod = new StringBuilder();
        mod.AppendLine("// Goal Keeper → OPL export (budget allocation LP)");
        mod.AppendLine("// Same shape as CPLEX OPL homework: sets, data, dvar, maximize, subject to.");
        mod.AppendLine("// Open in IBM ILOG CPLEX Optimization Studio with the matching .dat file.");
        mod.AppendLine();
        mod.AppendLine("{string} Categories = ...;");
        mod.AppendLine();
        mod.AppendLine("float RemainingBudget = ...;     // month limit − spent MTD");
        mod.AppendLine("float Cap[Categories] = ...;     // category caps (0 = no cap)");
        mod.AppendLine("float Weight[Categories] = ...;  // prefer mix close to current spend share");
        mod.AppendLine();
        mod.AppendLine("dvar float+ alloc[Categories];   // how much MORE to allow this month");
        mod.AppendLine();
        mod.AppendLine("maximize");
        mod.AppendLine("  sum(c in Categories) Weight[c] * alloc[c];");
        mod.AppendLine();
        mod.AppendLine("subject to {");
        mod.AppendLine("  ctBudget:");
        mod.AppendLine("    sum(c in Categories) alloc[c] <= RemainingBudget;");
        mod.AppendLine();
        mod.AppendLine("  forall(c in Categories : Cap[c] > 0)");
        mod.AppendLine("    ctCap:");
        mod.AppendLine("      alloc[c] <= Cap[c];");
        mod.AppendLine("}");
        mod.AppendLine();
        mod.AppendLine("execute {");
        mod.AppendLine("  writeln(\"Objective = \", cplex.getObjValue());");
        mod.AppendLine("  for (var c in Categories)");
        mod.AppendLine("    writeln(c, \": \", alloc[c]);");
        mod.AppendLine("}");

        var total = categories.Sum(c => c.Amount);
        var dat = new StringBuilder();
        dat.AppendLine("// Goal Keeper live snapshot " + DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture));
        dat.Append("Categories = {");
        dat.Append(string.Join(", ", categories.Select(c => "\"" + Escape(c.Category) + "\"")));
        dat.AppendLine("};");
        dat.AppendLine();
        dat.AppendLine("RemainingBudget = " + Fmt(remaining) + ";");
        dat.AppendLine();
        dat.Append("Cap = [");
        dat.Append(string.Join(", ", categories.Select(c =>
        {
            var cap = caps.GetValueOrDefault(c.Category, 0);
            var headroom = cap > 0 ? Math.Max(0, cap - c.Amount) : remaining;
            return Fmt(headroom);
        })));
        dat.AppendLine("];");
        dat.AppendLine();
        dat.Append("Weight = [");
        dat.Append(string.Join(", ", categories.Select(c =>
        {
            var w = total > 0 ? c.Amount / total : 1m / categories.Count;
            return Fmt(Math.Max(0.01m, w));
        })));
        dat.AppendLine("];");

        var title = forecast.HasLimit
            ? $"alloc under €{Fmt(remaining)} leftover · {categories.Count} categories"
            : "sample data (set monthly limit in Goals for live numbers)";

        return (mod.ToString(), dat.ToString(), title);
    }

    private static string Fmt(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Escape(string value) => value.Replace("\"", "'", StringComparison.Ordinal);
}
