using System.Globalization;
using ClosedXML.Excel;
using Dashboard.Api.Auth;
using Dashboard.Api.Data;
using Dashboard.Api.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Dashboard.Api.Import;

public sealed class ExcelTransactionImporter
{
    private readonly DashboardDbContext _db;
    private readonly TransactionService _transactions;
    private readonly AccountContext _account;
    private readonly ExcelSecurityGate _gate;
    private readonly IMemoryCache _cache;

    public ExcelTransactionImporter(
        DashboardDbContext db,
        TransactionService transactions,
        AccountContext account,
        ExcelSecurityGate gate,
        IMemoryCache cache)
    {
        _db = db;
        _transactions = transactions;
        _account = account;
        _gate = gate;
        _cache = cache;
    }

    public byte[] BuildTemplate()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Transactions");
        sheet.Cell(1, 1).Value = "Date";
        sheet.Cell(1, 2).Value = "Amount";
        sheet.Cell(1, 3).Value = "Category";
        sheet.Cell(1, 4).Value = "What";
        sheet.Cell(1, 5).Value = "Why";
        sheet.Cell(2, 1).Value = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        sheet.Cell(2, 2).Value = 4.50;
        sheet.Cell(2, 3).Value = "Food";
        sheet.Cell(2, 4).Value = "Coffee";
        sheet.Cell(2, 5).Value = "Morning";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public async Task<ImportPreview> PreviewAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        if (IsRateLimited(accountId))
        {
            return new ImportPreview([], [], "Too many imports. Wait a minute.");
        }

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        var gate = _gate.Inspect(file.FileName, file.ContentType, buffer.Length, buffer);
        if (!gate.Success)
        {
            return new ImportPreview([], [], gate.Error);
        }

        List<ImportRow> accepted;
        List<ImportRow> rejected;
        try
        {
            (accepted, rejected) = ParseWorkbook(buffer);
        }
        catch (Exception)
        {
            return new ImportPreview([], [], "Could not read the workbook.");
        }

        var token = Guid.NewGuid().ToString("N");
        _cache.Set(
            PreviewKey(accountId, token),
            new CachedPreview(gate.FileName!, accepted, rejected),
            TimeSpan.FromMinutes(15));
        RegisterImportAttempt(accountId);
        return new ImportPreview(accepted, rejected, null, token, gate.FileName);
    }

    public ImportPreview? GetCachedPreview(string previewToken)
    {
        var accountId = _account.Require();
        if (string.IsNullOrWhiteSpace(previewToken)
            || !_cache.TryGetValue(PreviewKey(accountId, previewToken), out CachedPreview? cached)
            || cached is null)
        {
            return null;
        }

        return new ImportPreview(cached.Rows, cached.Rejected, null, previewToken, cached.FileName);
    }

    public async Task<(int Accepted, string? Error)> CommitAsync(string previewToken, CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        if (!_cache.TryGetValue(PreviewKey(accountId, previewToken), out CachedPreview? cached) || cached is null)
        {
            return (0, "Preview expired. Upload again.");
        }

        var batch = new ImportBatch
        {
            AccountId = accountId,
            FileName = cached.FileName,
            AcceptedRows = cached.Rows.Count,
            RejectedRows = cached.Rejected.Count,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.ImportBatches.Add(batch);
        await _db.SaveChangesAsync(cancellationToken);

        foreach (var row in cached.Rows)
        {
            await _transactions.CreateAsync(
                new TransactionRequest
                {
                    Date = row.Date,
                    Amount = row.Amount,
                    Category = row.Category,
                    Description = row.What,
                    Why = row.Why
                },
                cancellationToken,
                batch.Id);
        }

        _cache.Remove(PreviewKey(accountId, previewToken));
        return (cached.Rows.Count, null);
    }

    public async Task<IReadOnlyList<ImportBatch>> RecentAsync(int take, CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        return await _db.ImportBatches
            .Where(batch => batch.AccountId == accountId)
            .OrderByDescending(batch => batch.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    private static (List<ImportRow> Accepted, List<ImportRow> Rejected) ParseWorkbook(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        var lastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 1;
        if (lastCol > ExcelSecurityGate.MaxColumns)
        {
            throw new InvalidOperationException("too many columns");
        }

        if (lastRow - 1 > ExcelSecurityGate.MaxRows)
        {
            throw new InvalidOperationException("too many rows");
        }

        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var col = 1; col <= lastCol; col++)
        {
            var header = sheet.Cell(1, col).GetString().Trim();
            if (header.Length > 0)
            {
                headers[NormalizeHeader(header)] = col;
            }
        }

        int Col(params string[] names)
        {
            foreach (var name in names)
            {
                if (headers.TryGetValue(name, out var col))
                {
                    return col;
                }
            }

            return -1;
        }

        var dateCol = Col("date");
        var amountCol = Col("amount");
        var categoryCol = Col("category");
        var whatCol = Col("what", "description");
        var whyCol = Col("why");
        if (dateCol < 0 || amountCol < 0 || categoryCol < 0 || whatCol < 0)
        {
            throw new InvalidOperationException("missing columns");
        }

        var accepted = new List<ImportRow>();
        var rejected = new List<ImportRow>();
        for (var row = 2; row <= lastRow; row++)
        {
            var rawDate = sheet.Cell(row, dateCol).GetFormattedString();
            var rawAmount = sheet.Cell(row, amountCol).GetFormattedString();
            var category = ImportSanitize.Text(sheet.Cell(row, categoryCol).GetString(), 100);
            var what = ImportSanitize.Text(sheet.Cell(row, whatCol).GetString(), 1000);
            var why = whyCol > 0
                ? ImportSanitize.Text(sheet.Cell(row, whyCol).GetString(), 300)
                : "—";

            if (string.IsNullOrWhiteSpace(rawDate)
                && string.IsNullOrWhiteSpace(rawAmount)
                && string.IsNullOrWhiteSpace(category)
                && string.IsNullOrWhiteSpace(what))
            {
                continue;
            }

            string? error = null;
            if (!TryParseDate(sheet.Cell(row, dateCol), out var date))
            {
                error = "Bad date";
            }
            else if (!TryParseAmount(sheet.Cell(row, amountCol), out var amount) || amount <= 0)
            {
                error = "Bad amount";
            }
            else if (string.IsNullOrWhiteSpace(category))
            {
                error = "Category required";
            }
            else if (string.IsNullOrWhiteSpace(what))
            {
                error = "What required";
            }
            else
            {
                accepted.Add(new ImportRow(row, date, amount, category, what, string.IsNullOrWhiteSpace(why) ? "—" : why, null));
                continue;
            }

            rejected.Add(new ImportRow(row, default, 0, category, what, why, error));
        }

        return (accepted, rejected);
    }

    private static string NormalizeHeader(string header) =>
        header.Trim().ToLowerInvariant() switch
        {
            "description" => "what",
            "what" => "what",
            "why" => "why",
            "category" => "category",
            "amount" => "amount",
            "date" => "date",
            _ => header.Trim().ToLowerInvariant()
        };

    private static bool TryParseDate(IXLCell cell, out DateOnly date)
    {
        if (cell.TryGetValue(out DateTime dt))
        {
            date = DateOnly.FromDateTime(dt);
            return true;
        }

        var text = cell.GetFormattedString().Trim();
        return DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
               || DateOnly.TryParse(text, out date);
    }

    private static bool TryParseAmount(IXLCell cell, out decimal amount)
    {
        if (cell.TryGetValue(out double number))
        {
            amount = decimal.Round((decimal)number, 2);
            return true;
        }

        var text = cell.GetFormattedString().Trim().Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    private bool IsRateLimited(long accountId)
    {
        return _cache.TryGetValue(RateKey(accountId), out int count) && count >= 6;
    }

    private void RegisterImportAttempt(long accountId)
    {
        var key = RateKey(accountId);
        var count = _cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return 0;
        });
        _cache.Set(key, count + 1, TimeSpan.FromMinutes(1));
    }

    private static string PreviewKey(long accountId, string token) => $"import-preview:{accountId}:{token}";
    private static string RateKey(long accountId) => $"import-rate:{accountId}";

    private sealed record CachedPreview(string FileName, List<ImportRow> Rows, List<ImportRow> Rejected);
}

public sealed record ImportRow(
    int SheetRow,
    DateOnly Date,
    decimal Amount,
    string Category,
    string What,
    string Why,
    string? Error);

public sealed record ImportPreview(
    IReadOnlyList<ImportRow> Accepted,
    IReadOnlyList<ImportRow> Rejected,
    string? Error,
    string? PreviewToken = null,
    string? FileName = null);
