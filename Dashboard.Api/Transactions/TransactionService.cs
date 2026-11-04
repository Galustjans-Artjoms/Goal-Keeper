using Dashboard.Api.Auth;
using Dashboard.Api.Budget;
using Dashboard.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Api.Transactions;

public class TransactionNotFoundException : Exception
{
    public TransactionNotFoundException(long id)
        : base($"Transaction with id {id} not found")
    {
    }
}

public class TransactionService
{
    private readonly DashboardDbContext _db;
    private readonly BudgetService _budget;
    private readonly AccountContext _account;

    public TransactionService(DashboardDbContext db, BudgetService budget, AccountContext account)
    {
        _db = db;
        _budget = budget;
        _account = account;
    }

    public async Task<PagedTransactions> FindAsync(
        TransactionFilter filter,
        int page,
        int size,
        string sort,
        string direction,
        CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var query = ApplyFilters(_db.Transactions.Where(t => t.AccountId == accountId), filter);
        query = ApplySort(query, sort, direction);

        if (!string.IsNullOrWhiteSpace(filter.DescriptionContains))
        {
            var needle = filter.DescriptionContains.Trim();
            var matched = (await query.ToListAsync(cancellationToken))
                .Where(transaction =>
                    transaction.Description?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true
                    || transaction.Why?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
            return new PagedTransactions(
                matched.Skip(page * size).Take(size).ToList(),
                page,
                size,
                matched.Count);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip(page * size).Take(size).ToListAsync(cancellationToken);
        return new PagedTransactions(items, page, size, total);
    }

    public async Task<Transaction> GetAsync(long id, CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        return await _db.Transactions.FirstOrDefaultAsync(
                   transaction => transaction.Id == id && transaction.AccountId == accountId,
                   cancellationToken)
               ?? throw new TransactionNotFoundException(id);
    }

    public async Task<Transaction> CreateAsync(
        TransactionRequest request,
        CancellationToken cancellationToken,
        long? importBatchId = null)
    {
        var accountId = _account.Require();
        var transaction = new Transaction { AccountId = accountId, ImportBatchId = importBatchId };
        Copy(request, transaction);
        _db.Transactions.Add(transaction);
        await _db.SaveChangesAsync(cancellationToken);
        await _budget.CheckAndNotifyAsync(transaction.Date, transaction.Category, cancellationToken);
        return transaction;
    }

    public async Task<Transaction> UpdateAsync(long id, TransactionRequest request, CancellationToken cancellationToken)
    {
        var transaction = await GetAsync(id, cancellationToken);
        Copy(request, transaction);
        await _db.SaveChangesAsync(cancellationToken);
        return transaction;
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var transaction = await GetAsync(id, cancellationToken);
        _db.Transactions.Remove(transaction);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAllForAccountAsync(CancellationToken cancellationToken)
    {
        var accountId = _account.Require();
        var rows = await _db.Transactions.Where(t => t.AccountId == accountId).ToListAsync(cancellationToken);
        _db.Transactions.RemoveRange(rows);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static IQueryable<Transaction> ApplyFilters(IQueryable<Transaction> query, TransactionFilter filter)
    {
        if (filter.FromDate is { } fromDate)
        {
            query = query.Where(transaction => transaction.Date >= fromDate);
        }

        if (filter.ToDate is { } toDate)
        {
            query = query.Where(transaction => transaction.Date <= toDate);
        }

        if (filter.MinAmount is { } minAmount)
        {
            query = query.Where(transaction => transaction.Amount >= minAmount);
        }

        if (filter.MaxAmount is { } maxAmount)
        {
            query = query.Where(transaction => transaction.Amount <= maxAmount);
        }

        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            var category = filter.Category.Trim().ToLower();
            query = query.Where(transaction => transaction.Category.ToLower() == category);
        }

        return query;
    }

    private static IQueryable<Transaction> ApplySort(IQueryable<Transaction> query, string sort, string direction)
    {
        var descending = !string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase);
        return sort switch
        {
            "id" => descending ? query.OrderByDescending(transaction => transaction.Id) : query.OrderBy(transaction => transaction.Id),
            "amount" => descending ? query.OrderByDescending(transaction => transaction.Amount) : query.OrderBy(transaction => transaction.Amount),
            "category" => descending ? query.OrderByDescending(transaction => transaction.Category) : query.OrderBy(transaction => transaction.Category),
            _ => descending
                ? query.OrderByDescending(transaction => transaction.Date).ThenByDescending(transaction => transaction.Id)
                : query.OrderBy(transaction => transaction.Date).ThenBy(transaction => transaction.Id)
        };
    }

    private static void Copy(TransactionRequest request, Transaction transaction)
    {
        transaction.Date = request.Date!.Value;
        transaction.Amount = request.Amount!.Value;
        transaction.Category = request.Category!.Trim();
        // Quick entry: Amount + Category is enough; What defaults to category.
        transaction.Description = string.IsNullOrWhiteSpace(request.Description)
            ? transaction.Category
            : request.Description.Trim();
        transaction.Why = string.IsNullOrWhiteSpace(request.Why) ? null : request.Why.Trim();
    }
}
