using System.Globalization;
using System.Text;
using System.Text.Json;
using Dashboard.Api.Auth;
using Dashboard.Api.Budget;
using Dashboard.Api.Data;
using Dashboard.Api.Demo;
using Dashboard.Api.Import;
using Dashboard.Api.Ops;
using Dashboard.Api.SystemInfo;
using Dashboard.Api.Transactions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Dashboard.Api.Controllers;

public class TransactionsPageController : Controller
{
    private readonly TransactionService _service;
    private readonly DashboardDbContext _db;
    private readonly BudgetService _budget;
    private readonly ExcelTransactionImporter _importer;
    private readonly OpTemplates _op;
    private readonly OpOplExport _oplExport;
    private readonly DemoDataService _demo;
    private readonly AppStatusService _status;
    private readonly AccountContext _account;
    private readonly IMemoryCache _cache;

    public TransactionsPageController(
        TransactionService service,
        DashboardDbContext db,
        BudgetService budget,
        ExcelTransactionImporter importer,
        OpTemplates op,
        OpOplExport oplExport,
        DemoDataService demo,
        AppStatusService status,
        AccountContext account,
        IMemoryCache cache)
    {
        _service = service;
        _db = db;
        _budget = budget;
        _importer = importer;
        _op = op;
        _oplExport = oplExport;
        _demo = demo;
        _status = status;
        _account = account;
        _cache = cache;
    }

    [HttpGet("/")]
    public IActionResult Home() => Redirect("/transactions");

    [HttpGet("/transactions")]
    public async Task<IActionResult> Index(
        [FromQuery] TransactionFilter filter,
        [FromQuery] int page = 0,
        [FromQuery] int size = 10,
        [FromQuery] string sort = "date",
        [FromQuery] string dir = "desc",
        [FromQuery] string? added = null,
        CancellationToken cancellationToken = default)
    {
        var model = await LoadPage(filter, page, size, sort, dir, cancellationToken);
        model.JustAdded = added is not null;
        model.InboxNeedsFields = string.Equals(HttpContext.Request.Query["inbox"], "needfields", StringComparison.OrdinalIgnoreCase);
        model.Flash = TempData["Flash"] as string;
        model.FlashBad = TempData["FlashBad"] as string;
        model.OpenPanel = TempData["OpenPanel"] as string;
        if (TempData["ImportPreviewToken"] is string previewToken)
        {
            model.ImportPreview = _importer.GetCachedPreview(previewToken);
            if (model.ImportPreview is null)
            {
                model.FlashBad ??= "Excel preview expired. Upload the file again.";
            }
        }

        if (TempData["OpResultToken"] is string opToken
            && _cache.TryGetValue($"op-result:{_account.AccountId}:{opToken}", out OpSolveResult? opCached)
            && opCached is not null)
        {
            model.LastOpResult = opCached;
        }

        return View("~/Views/Transactions/Index.cshtml", model);
    }

    [HttpPost("/transactions")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(TransactionRequest newTransaction, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var model = await LoadPage(new TransactionFilter(), 0, 10, "date", "desc", cancellationToken);
            model.NewTransaction = newTransaction;
            return View("~/Views/Transactions/Index.cshtml", model);
        }

        await _service.CreateAsync(newTransaction, cancellationToken);
        return Redirect("/transactions?added=1");
    }

    [HttpGet("/transactions/{id:long}/edit")]
    public async Task<IActionResult> Edit(long id, CancellationToken cancellationToken)
    {
        var transaction = await _service.GetAsync(id, cancellationToken);
        return View("~/Views/Transactions/Edit.cshtml", ToRequest(id, transaction));
    }

    [HttpPost("/transactions/{id:long}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(long id, EditTransaction editTransaction, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            editTransaction.Id = id;
            return View("~/Views/Transactions/Edit.cshtml", editTransaction);
        }

        await _service.UpdateAsync(id, editTransaction, cancellationToken);
        return Redirect("/transactions");
    }

    [HttpPost("/transactions/{id:long}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, cancellationToken);
        return Redirect("/transactions");
    }

    [HttpPost("/inbox/{id:long}/accept")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AcceptInbox(
        long id,
        decimal? amount,
        string? description,
        string? why,
        string? category,
        CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var entry = await _db.InboxEntries
            .FirstOrDefaultAsync(item => item.Id == id && item.AccountId == accountId, cancellationToken);
        if (entry is null)
        {
            return Redirect("/transactions");
        }

        var resolvedAmount = amount ?? entry.Amount;
        var resolvedDescription = string.IsNullOrWhiteSpace(description)
            ? entry.Description
            : description.Trim();
        if (string.IsNullOrWhiteSpace(resolvedDescription))
        {
            resolvedDescription = entry.RawText;
        }

        var resolvedWhy = string.IsNullOrWhiteSpace(why) ? entry.Why : why.Trim();
        var resolvedCategory = string.IsNullOrWhiteSpace(category)
            ? (string.IsNullOrWhiteSpace(entry.Category) ? "General" : entry.Category)
            : category.Trim();

        if (resolvedAmount is not decimal money || money <= 0 || string.IsNullOrWhiteSpace(resolvedDescription))
        {
            return Redirect("/transactions?inbox=needfields");
        }

        await _service.CreateAsync(
            new TransactionRequest
            {
                Date = entry.Date,
                Amount = money,
                Category = resolvedCategory,
                Description = resolvedDescription,
                Why = string.IsNullOrWhiteSpace(resolvedWhy) ? "—" : resolvedWhy
            },
            cancellationToken);
        _db.InboxEntries.Remove(entry);
        await _db.SaveChangesAsync(cancellationToken);
        return Redirect("/transactions?added=1");
    }

    [HttpPost("/inbox/{id:long}/discard")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DiscardInbox(long id, CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var entry = await _db.InboxEntries
            .FirstOrDefaultAsync(item => item.Id == id && item.AccountId == accountId, cancellationToken);
        if (entry is not null)
        {
            _db.InboxEntries.Remove(entry);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return Redirect("/transactions");
    }

    [HttpPost("/budget")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveBudget(
        decimal monthlyIncome,
        decimal? monthlyLimit,
        decimal savingsTarget,
        CancellationToken cancellationToken)
    {
        var income = monthlyIncome > 0 ? monthlyIncome : monthlyLimit ?? 0;
        await _budget.SetPaycheckAsync(income, savingsTarget, cancellationToken);
        TempData["Flash"] = "Paycheck saved.";
        TempData["OpenPanel"] = "analysis";
        return Redirect("/transactions");
    }

    [HttpPost("/budget/year-goal")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveYearGoal(
        string? yearGoalName,
        decimal yearGoal,
        DateOnly? savingsDeadline,
        CancellationToken cancellationToken)
    {
        await _budget.SetYearGoalAsync(yearGoalName, yearGoal, savingsDeadline, cancellationToken);
        TempData["Flash"] = "Year goal saved — Roadmap uses this bracket.";
        TempData["OpenPanel"] = "goals";
        return Redirect("/transactions");
    }

    [HttpPost("/budget/savings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSavings(decimal savingsTarget, DateOnly? savingsDeadline, CancellationToken cancellationToken)
    {
        await _budget.SetSavingsGoalAsync(savingsTarget, savingsDeadline, cancellationToken);
        TempData["Flash"] = "Keep updated.";
        TempData["OpenPanel"] = "analysis";
        return Redirect("/transactions");
    }

    [HttpPost("/budget/category")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCategoryCap(string category, decimal monthlyCap, CancellationToken cancellationToken)
    {
        await _budget.UpsertCategoryCapAsync(category ?? "", monthlyCap, cancellationToken);
        TempData["OpenPanel"] = "goals";
        return Redirect("/transactions");
    }

    [HttpPost("/budget/category/{id:long}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCategoryCap(long id, CancellationToken cancellationToken)
    {
        await _budget.DeleteCategoryCapAsync(id, cancellationToken);
        TempData["OpenPanel"] = "goals";
        return Redirect("/transactions");
    }

    [HttpGet("/transactions/export")]
    public async Task<IActionResult> Export(
        [FromQuery] TransactionFilter filter,
        CancellationToken cancellationToken)
    {
        var page = await _service.FindAsync(filter, 0, int.MaxValue, "date", "desc", cancellationToken);
        var csv = new StringBuilder("id,date,amount,category,description,why\n");
        foreach (var transaction in page.Items)
        {
            csv.Append(transaction.Id).Append(',')
                .Append(transaction.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(transaction.Amount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(ImportSanitize.EscapeCsv(transaction.Category)).Append(',')
                .Append(ImportSanitize.EscapeCsv(transaction.Description)).Append(',')
                .Append(ImportSanitize.EscapeCsv(transaction.Why))
                .Append('\n');
        }

        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "transactions.csv");
    }

    [HttpGet("/import/template.xlsx")]
    public IActionResult ImportTemplate()
    {
        var bytes = _importer.BuildTemplate();
        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "goal-keeper-import-template.xlsx");
    }

    [HttpPost("/import/excel")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(ExcelSecurityGate.MaxBytes + 64_000)]
    public async Task<IActionResult> ImportExcel(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            TempData["FlashBad"] = "Choose an .xlsx file.";
            return Redirect("/transactions");
        }

        var preview = await _importer.PreviewAsync(file, cancellationToken);
        if (preview.Error is not null)
        {
            TempData["FlashBad"] = preview.Error;
            return Redirect("/transactions");
        }

        // Keep only the token in TempData — full preview lives in memory cache (cookie size limit).
        TempData["ImportPreviewToken"] = preview.PreviewToken;
        TempData["Flash"] = $"Preview ready: {preview.Accepted.Count} ok, {preview.Rejected.Count} rejected. Scroll Advanced → Excel.";
        return Redirect("/transactions");
    }

    [HttpPost("/import/excel/commit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CommitImport(string previewToken, CancellationToken cancellationToken)
    {
        var (accepted, error) = await _importer.CommitAsync(previewToken ?? "", cancellationToken);
        if (error is not null)
        {
            TempData["FlashBad"] = error;
        }
        else
        {
            TempData["Flash"] = $"Imported {accepted} rows.";
        }

        return Redirect("/transactions");
    }

    [HttpGet("/op/export.mod")]
    public async Task<IActionResult> ExportOplMod(CancellationToken cancellationToken)
    {
        var (mod, _, _) = await _oplExport.BuildAsync(cancellationToken);
        return File(Encoding.UTF8.GetBytes(mod), "text/plain", "goal-keeper.mod");
    }

    [HttpGet("/op/export.dat")]
    public async Task<IActionResult> ExportOplDat(CancellationToken cancellationToken)
    {
        var (_, dat, _) = await _oplExport.BuildAsync(cancellationToken);
        return File(Encoding.UTF8.GetBytes(dat), "text/plain", "goal-keeper.dat");
    }

    [HttpPost("/op/solve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpSolve(
        string template,
        decimal? yearLeftoverTarget,
        decimal? extraCutPercent,
        string? whatIfCategory,
        decimal? whatIfChangePercent,
        decimal? mustKeepFloorPercent,
        int? horizonDays,
        CancellationToken cancellationToken)
    {
        var result = await _op.SolveAsync(
            new OpSolveRequest(
                template ?? "",
                yearLeftoverTarget,
                extraCutPercent,
                whatIfCategory,
                whatIfChangePercent,
                mustKeepFloorPercent,
                horizonDays),
            cancellationToken);
        var opToken = Guid.NewGuid().ToString("N");
        _cache.Set($"op-result:{_account.AccountId}:{opToken}", result, TimeSpan.FromMinutes(30));
        TempData["OpResultToken"] = opToken;
        return Redirect("/transactions");
    }

    [HttpPost("/op/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpSave(string slot, string resultJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            TempData["FlashBad"] = "Solve first, then save a scenario.";
            return Redirect("/transactions");
        }

        var result = JsonSerializer.Deserialize<OpSolveResult>(resultJson);
        if (result is null)
        {
            TempData["FlashBad"] = "Could not save scenario.";
            return Redirect("/transactions");
        }

        await _op.SaveScenarioAsync(slot ?? "A", result, cancellationToken);
        TempData["Flash"] = $"Saved scenario {slot}.";
        TempData["OpResult"] = resultJson;
        return Redirect("/transactions");
    }

    [HttpPost("/op/apply")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpApply(string resultJson, CancellationToken cancellationToken)
    {
        var result = JsonSerializer.Deserialize<OpSolveResult>(resultJson ?? "");
        if (result is null || result.Decisions.Count == 0)
        {
            TempData["FlashBad"] = "Nothing to apply.";
            return Redirect("/transactions");
        }

        await _op.ApplyCapsAsync(result, cancellationToken);
        TempData["Flash"] = "Suggested caps written to Goals.";
        TempData["OpResult"] = resultJson;
        return Redirect("/transactions");
    }

    [HttpPost("/demo/reset")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetDemo(CancellationToken cancellationToken)
    {
        await _demo.ResetAndSeedAsync(cancellationToken);
        TempData["Flash"] = "Demo data reset for this account.";
        return Redirect("/transactions");
    }

    private async Task<DashboardPage> LoadPage(
        TransactionFilter filter,
        int page,
        int size,
        string sort,
        string dir,
        CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var safeSort = sort is "id" or "date" or "amount" or "category" ? sort : "date";
        var safeDir = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        var safePage = Math.Max(page, 0);
        var safeSize = Math.Max(size, 1);
        var result = await _service.FindAsync(filter, safePage, safeSize, safeSort, safeDir, cancellationToken);
        var totalPages = result.Total == 0 ? 0 : (int)Math.Ceiling(result.Total / (double)safeSize);
        var inbox = await _db.InboxEntries
            .Where(entry => entry.AccountId == accountId)
            .OrderByDescending(entry => entry.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        var budget = await _budget.GetStatusAsync(null, cancellationToken);
        var forecast = await _budget.GetForecastAsync(cancellationToken);
        var roadmap = await _budget.GetRoadmapAsync(cancellationToken);
        var imports = await _importer.RecentAsync(8, cancellationToken);
        var scenarios = await _op.LoadScenariosAsync(cancellationToken);
        var status = await _status.GetAsync(cancellationToken);
        var settings = await _budget.GetSettingsAsync(cancellationToken);
        var (_, _, oplTitle) = await _oplExport.BuildAsync(cancellationToken);
        return new DashboardPage
        {
            Filter = filter,
            Transactions = result.Items,
            Inbox = inbox,
            Budget = budget,
            Forecast = forecast,
            Roadmap = roadmap,
            ImportBatches = imports,
            OpScenarios = scenarios,
            AppStatus = status,
            AccountId = accountId,
            SavingsTarget = settings.SavingsTarget,
            SavingsDeadline = settings.SavingsDeadline,
            YearGoal = settings.YearGoal,
            YearGoalName = settings.YearGoalName,
            MonthlyIncome = settings.MonthlyIncome > 0
                ? settings.MonthlyIncome
                : settings.MonthlyLimit + settings.SavingsTarget,
            OplExportTitle = oplTitle,
            CurrentPage = result.Page,
            PageSize = result.Size,
            TotalPages = totalPages,
            Sort = safeSort,
            Dir = safeDir
        };
    }

    private static EditTransaction ToRequest(long id, Transaction transaction) => new()
    {
        Id = id,
        Date = transaction.Date,
        Amount = transaction.Amount,
        Category = transaction.Category,
        Description = transaction.Description,
        Why = transaction.Why
    };
}

public class DashboardPage
{
    public TransactionFilter Filter { get; set; } = new();
    public TransactionRequest NewTransaction { get; set; } = new();
    public IReadOnlyList<Transaction> Transactions { get; set; } = [];
    public IReadOnlyList<InboxEntry> Inbox { get; set; } = [];
    public IReadOnlyList<ImportBatch> ImportBatches { get; set; } = [];
    public IReadOnlyList<OpScenario> OpScenarios { get; set; } = [];
    public BudgetStatus Budget { get; set; } = new(0, 0, 0, 0, 0, BudgetBand.Ok, false, 0, 0, 0, BudgetBand.Ok, []);
    public MonthForecast Forecast { get; set; } = new(0, 0, 0, 0, 0, 0, 0, false, null, 0, null, BudgetBand.Ok, []);
    public SavingsRoadmap Roadmap { get; set; } = new(0, 0, 0, 0, 0, 0, 0, [], "");
    public AppStatus? AppStatus { get; set; }
    public ImportPreview? ImportPreview { get; set; }
    public OpSolveResult? LastOpResult { get; set; }
    public long AccountId { get; set; }
    public decimal SavingsTarget { get; set; }
    public DateOnly? SavingsDeadline { get; set; }
    public decimal YearGoal { get; set; }
    public string? YearGoalName { get; set; }
    public decimal MonthlyIncome { get; set; }
    public string? OplExportTitle { get; set; }
    public int CurrentPage { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public string Sort { get; set; } = "date";
    public string Dir { get; set; } = "desc";
    public bool JustAdded { get; set; }
    public bool InboxNeedsFields { get; set; }
    public string? Flash { get; set; }
    public string? FlashBad { get; set; }
    public string? OpenPanel { get; set; }
}

public class EditTransaction : TransactionRequest
{
    public long Id { get; set; }
}
