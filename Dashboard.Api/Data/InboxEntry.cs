namespace Dashboard.Api.Data;

public class InboxEntry
{
    public long Id { get; set; }

    public long AccountId { get; set; }

    public string RawText { get; set; } = "";

    public DateOnly Date { get; set; }

    public decimal? Amount { get; set; }

    public string? Category { get; set; }

    public string? Description { get; set; }

    public string? Why { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
