namespace Dashboard.Api.Data;

public class ImportBatch
{
    public long Id { get; set; }

    public long AccountId { get; set; }

    public string FileName { get; set; } = "";

    public int AcceptedRows { get; set; }

    public int RejectedRows { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
