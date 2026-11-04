using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dashboard.Api.Data;

public class Transaction
{
    public long Id { get; set; }

    public long AccountId { get; set; }

    public long? ImportBatchId { get; set; }

    public DateOnly Date { get; set; }

    [Column(TypeName = "decimal(19,2)")]
    public decimal Amount { get; set; }

    [MaxLength(100)]
    public string Category { get; set; } = "";

    [MaxLength(1000)]
    public string? Description { get; set; }

    [MaxLength(300)]
    public string? Why { get; set; }
}
