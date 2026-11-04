using System.ComponentModel.DataAnnotations;

namespace Dashboard.Api.Transactions;

public class TransactionRequest
{
    [Required]
    public DateOnly? Date { get; set; }

    [Required]
    [Range(0.01, 999999999999999.99)]
    public decimal? Amount { get; set; }

    [Required]
    [MaxLength(100)]
    public string? Category { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    [MaxLength(300)]
    public string? Why { get; set; }
}

public class TransactionFilter
{
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    public string? Category { get; set; }
    public string? DescriptionContains { get; set; }
}

public record PagedTransactions(IReadOnlyList<Data.Transaction> Items, int Page, int Size, int Total);
